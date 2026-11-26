using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;

namespace backend.Services;

// Nivå etter score: lav 1–4, middels 5–9, høy 10–14, kritisk 15–25
public static class RiskLevels
{
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
    public const string Critical = "critical";

    public static readonly string[] All = [Low, Medium, High, Critical];

    public static string For(int score) => score switch
    {
        >= 15 => Critical,
        >= 10 => High,
        >= 5 => Medium,
        _ => Low,
    };

    public static (int Min, int Max) Range(string level) => level switch
    {
        Critical => (15, 25),
        High => (10, 14),
        Medium => (5, 9),
        _ => (1, 4),
    };
}

public record RiskInput(
    string? Title,
    string? Description,
    string? Category,
    int Likelihood,
    int Consequence,
    string? ExistingControls,
    string? RecommendedActions,
    int? ResidualLikelihood,
    int? ResidualConsequence,
    string? Strategy,
    int? OwnerEmployeeId,
    int? ActionOwnerEmployeeId,
    DateTime? DueDate,
    DateTime? NextReviewAt
);

public record RiskLinksInput(List<int>? ChangeIds, List<int>? ServiceIds, List<int>? TicketIds, List<int>? AlertIds);

// Gjeldende aksept: siste rad, og om den gjelder nå
public record RiskAcceptanceState(RiskAcceptance? Latest, bool Active, string? InactiveReason);

// Risikoregisteret (ENDRING_OG_RISIKO.md, C3). Statuser, tilgang og aksept avgjøres
// her på serveren. Aksept og tilbaketrekking legges til som nye rader.
public class RiskRegisterService
{
    public const int MaxAcceptanceMonths = 12;

    private readonly AppDbContext _db;
    private readonly GovernanceRoleService _roles;
    private readonly GovernanceNumberService _numbers;
    private readonly ICurrentUser _currentUser;

    public RiskRegisterService(AppDbContext db, GovernanceRoleService roles, GovernanceNumberService numbers, ICurrentUser currentUser)
    {
        _db = db;
        _roles = roles;
        _numbers = numbers;
        _currentUser = currentUser;
    }

    private string ActorObjectId =>
        _currentUser.ObjectId?.ToLowerInvariant() ?? throw GovernanceException.Forbidden("Innlogget bruker mangler.");

    private string ActorName => _currentUser.Name ?? _currentUser.ObjectId ?? "ukjent";

    public Task<GovernanceRoleService.PersonRoles> MyRolesAsync(CancellationToken ct = default) =>
        _roles.ForAsync(_currentUser.ObjectId, ct);

    public async Task<Risk> LoadAsync(int id, CancellationToken ct = default) =>
        await _db.Risks.Include(r => r.Acceptances).FirstOrDefaultAsync(r => r.Id == id, ct)
        ?? throw new GovernanceException(404, "Fant ikke risikoen.");

    public static bool CanRead(GovernanceRoleService.PersonRoles roles, bool isAgent) => isAgent || roles.HasAny;

    public async Task<List<int>> ServiceIdsAsync(int riskId, CancellationToken ct = default) =>
        await _db.RiskServices.Where(s => s.RiskId == riskId).Select(s => s.ServiceCatalogItemId).ToListAsync(ct);

    // Eier, rollen Risk Owner (global eller for en koblet tjeneste) eller Security Officer
    public bool CanEdit(Risk risk, GovernanceRoleService.PersonRoles roles, IReadOnlyCollection<int> serviceIds) =>
        (risk.OwnerEmployeeId != null && risk.OwnerEmployeeId == roles.EmployeeId) ||
        roles.Has(GovernanceRoles.RiskOwner, serviceIds) ||
        roles.Has(GovernanceRoles.SecurityOfficer, serviceIds);

    // Utpekt eier som også har rollen Risk Owner
    public static bool CanAccept(Risk risk, GovernanceRoleService.PersonRoles roles, IReadOnlyCollection<int> serviceIds) =>
        risk.OwnerEmployeeId != null && risk.OwnerEmployeeId == roles.EmployeeId &&
        roles.Has(GovernanceRoles.RiskOwner, serviceIds);

    public static RiskAcceptanceState AcceptanceState(Risk risk, DateTime now)
    {
        var latest = risk.Acceptances.OrderByDescending(a => a.DecidedAt).ThenByDescending(a => a.Id).FirstOrDefault();

        if (latest == null)
            return new(null, false, null);
        if (latest.Kind == RiskAcceptanceKinds.Revoked)
            return new(latest, false, "Aksepten er trukket tilbake.");
        if (latest.ValidUntil is { } until && until <= now)
            return new(latest, false, "Aksepten er utløpt.");
        if (latest.ScoreAtDecision != risk.Score)
            return new(latest, false, "Scoren er endret etter aksepten.");

        return new(latest, true, null);
    }

    // ---------- Registrere og endre ----------

    public async Task<Risk> CreateAsync(RiskInput input, bool isAgent, CancellationToken ct = default)
    {
        var roles = await MyRolesAsync(ct);
        if (!CanRead(roles, isAgent))
            throw GovernanceException.Forbidden("Krever Helpdesk.Agent eller en rolle i endrings- og risikoprosessen.");

        var now = DateTime.UtcNow;
        var risk = new Risk { Status = RiskStatuses.Identified, CreatedAt = now, UpdatedAt = now };
        await ApplyAsync(risk, input, ct);
        risk.Number = await _numbers.NextRiskNumberAsync(ct);

        _db.Risks.Add(risk);
        await _db.SaveChangesAsync(ct);
        return risk;
    }

    public async Task<Risk> UpdateAsync(int id, RiskInput input, CancellationToken ct = default)
    {
        var risk = await LoadAsync(id, ct);
        var roles = await MyRolesAsync(ct);

        if (!CanEdit(risk, roles, await ServiceIdsAsync(id, ct)))
            throw GovernanceException.Forbidden("Bare risikoeier, Risk Owner eller Security Officer kan endre risikoen.");
        if (risk.Status == RiskStatuses.Closed)
            throw GovernanceException.BadRequest("En lukket risiko kan ikke endres.");

        var wasActive = AcceptanceState(risk, DateTime.UtcNow).Active;

        await ApplyAsync(risk, input, ct);
        risk.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Scoren er beregnet i databasen og lest tilbake ved lagring
        await VoidAcceptanceIfNeededAsync(risk, wasActive, "Scoren er endret etter aksepten.", AuditActions.RiskAcceptanceVoided, ct);
        return risk;
    }

    // En aksept som ikke lenger gjelder, gir status «vurdert» igjen (logges én gang)
    public async Task VoidAcceptanceIfNeededAsync(
        Risk risk, bool wasActive, string reason, string action, CancellationToken ct, DateTime? nowUtc = null)
    {
        if (risk.Status != RiskStatuses.Accepted || AcceptanceState(risk, nowUtc ?? DateTime.UtcNow).Active)
            return;

        SetStatus(risk, RiskStatuses.Assessed, reason);
        _db.AddAuditEvent(nameof(Risk), risk.Id.ToString(), action, new { risk.Number, reason, wasActive });
        await _db.SaveChangesAsync(ct);
    }

    private async Task ApplyAsync(Risk risk, RiskInput input, CancellationToken ct)
    {
        var title = input.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200)
            throw GovernanceException.BadRequest("Tittel er påkrevd (maks 200 tegn).");
        if (string.IsNullOrWhiteSpace(input.Description) || input.Description.Length > 8000)
            throw GovernanceException.BadRequest("Beskrivelse er påkrevd (maks 8000 tegn).");
        if (input.Category == null || !RiskCategories.All.Contains(input.Category))
            throw GovernanceException.BadRequest("Ugyldig risikokategori.");
        if (!RiskScore.IsValid(input.Likelihood) || !RiskScore.IsValid(input.Consequence))
            throw GovernanceException.BadRequest("Sannsynlighet og konsekvens må være 1–5.");
        if ((input.ResidualLikelihood is { } rl && !RiskScore.IsValid(rl)) ||
            (input.ResidualConsequence is { } rc && !RiskScore.IsValid(rc)))
            throw GovernanceException.BadRequest("Restrisiko må være 1–5.");
        if ((input.ResidualLikelihood == null) != (input.ResidualConsequence == null))
            throw GovernanceException.BadRequest("Fyll ut både sannsynlighet og konsekvens for restrisiko.");
        if (input.Strategy != null && !RiskStrategies.All.Contains(input.Strategy))
            throw GovernanceException.BadRequest("Ugyldig strategi.");
        if (input.ExistingControls?.Length > 8000 || input.RecommendedActions?.Length > 8000)
            throw GovernanceException.BadRequest("Tiltak kan ha maks 8000 tegn.");

        foreach (var employeeId in new[] { input.OwnerEmployeeId, input.ActionOwnerEmployeeId }.OfType<int>())
        {
            if (!await _db.Employees.AnyAsync(e => e.Id == employeeId && e.AnonymizedAt == null, ct))
                throw GovernanceException.BadRequest("Ukjent ansatt.");
        }

        risk.Title = title;
        risk.Description = input.Description.Trim();
        risk.Category = input.Category;
        risk.Likelihood = input.Likelihood;
        risk.Consequence = input.Consequence;
        risk.ExistingControls = Clean(input.ExistingControls);
        risk.RecommendedActions = Clean(input.RecommendedActions);
        risk.ResidualLikelihood = input.ResidualLikelihood;
        risk.ResidualConsequence = input.ResidualConsequence;
        risk.Strategy = input.Strategy;
        risk.OwnerEmployeeId = input.OwnerEmployeeId;
        risk.ActionOwnerEmployeeId = input.ActionOwnerEmployeeId;
        risk.DueDate = input.DueDate is { } due ? DateTime.SpecifyKind(due, DateTimeKind.Utc) : null;
        risk.NextReviewAt = input.NextReviewAt is { } review ? DateTime.SpecifyKind(review, DateTimeKind.Utc) : null;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ---------- Statuser ----------

    // Alle overganger som finnes (uavhengig av rolle). «accepted» nås bare gjennom aksept.
    private static readonly HashSet<(string From, string To)> ValidTransitions =
    [
        (RiskStatuses.Identified, RiskStatuses.Assessed), (RiskStatuses.Identified, RiskStatuses.Closed),
        (RiskStatuses.Assessed, RiskStatuses.InTreatment), (RiskStatuses.Assessed, RiskStatuses.Closed),
        (RiskStatuses.InTreatment, RiskStatuses.Monitoring), (RiskStatuses.InTreatment, RiskStatuses.Closed),
        (RiskStatuses.Monitoring, RiskStatuses.InTreatment), (RiskStatuses.Monitoring, RiskStatuses.Closed),
        (RiskStatuses.Accepted, RiskStatuses.Closed),
    ];

    // Overgangene innlogget person kan gjøre nå
    public List<string> AllowedTransitions(Risk risk, GovernanceRoleService.PersonRoles roles, IReadOnlyCollection<int> serviceIds)
    {
        var editor = CanEdit(risk, roles, serviceIds);
        var actionOwner = risk.ActionOwnerEmployeeId != null && risk.ActionOwnerEmployeeId == roles.EmployeeId;

        return risk.Status switch
        {
            RiskStatuses.Identified when editor => [RiskStatuses.Assessed, RiskStatuses.Closed],
            RiskStatuses.Assessed when editor => [RiskStatuses.InTreatment, RiskStatuses.Closed],
            RiskStatuses.InTreatment when editor || actionOwner => editor
                ? [RiskStatuses.Monitoring, RiskStatuses.Closed]
                : [RiskStatuses.Monitoring],
            RiskStatuses.Monitoring when editor => [RiskStatuses.InTreatment, RiskStatuses.Closed],
            RiskStatuses.Accepted when editor => [RiskStatuses.Closed],
            _ => [],
        };
    }

    public async Task<Risk> TransitionAsync(int id, string? to, string? note, CancellationToken ct = default)
    {
        var risk = await LoadAsync(id, ct);
        var roles = await MyRolesAsync(ct);
        var serviceIds = await ServiceIdsAsync(id, ct);
        note = Clean(note);

        if (note?.Length > 4000)
            throw GovernanceException.BadRequest("Kommentaren kan ha maks 4000 tegn.");

        if (to == null || !ValidTransitions.Contains((risk.Status, to)))
            throw GovernanceException.BadRequest($"Ugyldig overgang fra {risk.Status} til {to}.");

        if (!AllowedTransitions(risk, roles, serviceIds).Contains(to))
            throw GovernanceException.Forbidden("Du kan ikke gjøre denne endringen av status.");

        var problem = to switch
        {
            RiskStatuses.Assessed when risk.OwnerEmployeeId == null => "Velg en risikoeier før risikoen er vurdert.",
            RiskStatuses.InTreatment when risk.Strategy is not (RiskStrategies.Mitigate or RiskStrategies.Transfer or RiskStrategies.Avoid) =>
                "Velg strategi Mitigate, Transfer eller Avoid. Accept gjøres med aksept.",
            RiskStatuses.InTreatment when risk.ActionOwnerEmployeeId == null || risk.DueDate == null =>
                "Velg tiltaksansvarlig og frist.",
            RiskStatuses.Closed when note == null => "Skriv hvorfor risikoen lukkes.",
            _ => null,
        };
        if (problem != null)
            throw GovernanceException.BadRequest(problem);

        SetStatus(risk, to, note);
        if (to == RiskStatuses.Closed)
            risk.ClosedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return risk;
    }

    private void SetStatus(Risk risk, string to, string? note)
    {
        var from = risk.Status;
        risk.Status = to;
        risk.UpdatedAt = DateTime.UtcNow;
        _db.SetAuditAction(risk, AuditActions.RiskStatusChanged);
        _db.AddAuditEvent(nameof(Risk), risk.Id.ToString(), AuditActions.RiskStatusChanged, new { risk.Number, from, to, note });
    }

    // ---------- Aksept ----------

    public async Task<Risk> AcceptAsync(
        int id, string? justification, DateTime? validUntil, int? expectedScore, CancellationToken ct = default)
    {
        var risk = await LoadAsync(id, ct);
        var roles = await MyRolesAsync(ct);
        var serviceIds = await ServiceIdsAsync(id, ct);
        var now = DateTime.UtcNow;
        justification = Clean(justification);

        if (!CanAccept(risk, roles, serviceIds))
            throw GovernanceException.Forbidden("Bare utpekt risikoeier med rollen Risk Owner kan akseptere risikoen.");
        if (risk.Status is not (RiskStatuses.Assessed or RiskStatuses.Monitoring))
            throw GovernanceException.BadRequest("Risikoen må være vurdert før den kan aksepteres.");
        if (risk.Strategy != RiskStrategies.Accept)
            throw GovernanceException.BadRequest("Velg strategien Accept før aksept.");
        if (justification == null || justification.Length > 4000)
            throw GovernanceException.BadRequest("Begrunnelse er påkrevd (maks 4000 tegn).");
        if (validUntil is not { } until || until <= now || until > now.AddMonths(MaxAcceptanceMonths))
            throw GovernanceException.BadRequest($"Velg en utløpsdato innen {MaxAcceptanceMonths} måneder.");
        if (expectedScore != risk.Score)
            throw GovernanceException.Conflict("Scoren er endret siden du åpnet risikoen. Se gjennom den på nytt.");

        // Kritiske risikoer kan ikke aksepteres av den som registrerte dem
        if (RiskLevels.For(risk.Score) == RiskLevels.Critical && await RegisteredByAsync(risk, ct) == ActorObjectId)
            throw GovernanceException.Forbidden("En kritisk risiko kan ikke aksepteres av den som registrerte den.");

        risk.Acceptances.Add(new RiskAcceptance
        {
            RiskId = risk.Id,
            Kind = RiskAcceptanceKinds.Accepted,
            ActorObjectId = ActorObjectId,
            ActorName = ActorName,
            ActorEmployeeId = roles.EmployeeId,
            DecidedAt = now,
            Justification = justification,
            ScoreAtDecision = risk.Score,
            ValidUntil = DateTime.SpecifyKind(until, DateTimeKind.Utc),
        });

        SetStatus(risk, RiskStatuses.Accepted, justification);
        _db.AddAuditEvent(nameof(Risk), risk.Id.ToString(), AuditActions.RiskAccepted,
            new { risk.Number, score = risk.Score, validUntil = until, justification });

        await _db.SaveChangesAsync(ct);
        return risk;
    }

    public async Task<Risk> RevokeAsync(int id, string? justification, CancellationToken ct = default)
    {
        var risk = await LoadAsync(id, ct);
        var roles = await MyRolesAsync(ct);
        justification = Clean(justification);

        if (!CanAccept(risk, roles, await ServiceIdsAsync(id, ct)))
            throw GovernanceException.Forbidden("Bare utpekt risikoeier med rollen Risk Owner kan trekke tilbake aksept.");
        if (!AcceptanceState(risk, DateTime.UtcNow).Active)
            throw GovernanceException.BadRequest("Risikoen har ingen gjeldende aksept.");
        if (justification == null || justification.Length > 4000)
            throw GovernanceException.BadRequest("Begrunnelse er påkrevd (maks 4000 tegn).");

        risk.Acceptances.Add(new RiskAcceptance
        {
            RiskId = risk.Id,
            Kind = RiskAcceptanceKinds.Revoked,
            ActorObjectId = ActorObjectId,
            ActorName = ActorName,
            ActorEmployeeId = roles.EmployeeId,
            DecidedAt = DateTime.UtcNow,
            Justification = justification,
            ScoreAtDecision = risk.Score,
        });

        SetStatus(risk, RiskStatuses.Assessed, justification);
        _db.AddAuditEvent(nameof(Risk), risk.Id.ToString(), AuditActions.RiskAcceptanceRevoked, new { risk.Number, justification });

        await _db.SaveChangesAsync(ct);
        return risk;
    }

    // Hvem som registrerte risikoen (fra revisjonsloggen, som ikke kan endres)
    private async Task<string?> RegisteredByAsync(Risk risk, CancellationToken ct) =>
        (await _db.AuditEvents
            .Where(e => e.EntityType == nameof(Risk) && e.EntityId == risk.Id.ToString() && e.Action == AuditActions.Created)
            .OrderBy(e => e.Id)
            .Select(e => e.ActorObjectId)
            .FirstOrDefaultAsync(ct))?.ToLowerInvariant();

    // ---------- Koblinger ----------

    public async Task<RiskLinksInput> LinksAsync(int id, CancellationToken ct = default) => new(
        await _db.RiskChanges.Where(l => l.RiskId == id).Select(l => l.ChangeRequestId).ToListAsync(ct),
        await ServiceIdsAsync(id, ct),
        await _db.RiskIncidents.Where(l => l.RiskId == id && l.TicketId != null).Select(l => l.TicketId!.Value).ToListAsync(ct),
        await _db.RiskIncidents.Where(l => l.RiskId == id && l.AlertId != null).Select(l => l.AlertId!.Value).ToListAsync(ct));

    public async Task<RiskLinksInput> SetLinksAsync(int id, RiskLinksInput input, CancellationToken ct = default)
    {
        var risk = await LoadAsync(id, ct);
        var roles = await MyRolesAsync(ct);
        if (!CanEdit(risk, roles, await ServiceIdsAsync(id, ct)))
            throw GovernanceException.Forbidden("Bare risikoeier, Risk Owner eller Security Officer kan endre koblingene.");

        var changes = (input.ChangeIds ?? []).Distinct().ToList();
        var services = (input.ServiceIds ?? []).Distinct().ToList();
        var tickets = (input.TicketIds ?? []).Distinct().ToList();
        var alerts = (input.AlertIds ?? []).Distinct().ToList();

        // Tenant-filteret gjør at ID-er fra andre tenanter ikke finnes
        if (await _db.ChangeRequests.CountAsync(c => changes.Contains(c.Id), ct) != changes.Count ||
            await _db.ServiceCatalogItems.CountAsync(s => services.Contains(s.Id), ct) != services.Count ||
            await _db.Tickets.CountAsync(t => tickets.Contains(t.Id), ct) != tickets.Count ||
            await _db.Alerts.CountAsync(a => alerts.Contains(a.Id), ct) != alerts.Count)
        {
            throw GovernanceException.BadRequest("En eller flere koblinger finnes ikke.");
        }

        _db.RiskChanges.RemoveRange(await _db.RiskChanges.Where(l => l.RiskId == id).ToListAsync(ct));
        _db.RiskServices.RemoveRange(await _db.RiskServices.Where(l => l.RiskId == id).ToListAsync(ct));
        _db.RiskIncidents.RemoveRange(await _db.RiskIncidents.Where(l => l.RiskId == id).ToListAsync(ct));

        _db.RiskChanges.AddRange(changes.Select(c => new RiskChange { RiskId = id, ChangeRequestId = c }));
        _db.RiskServices.AddRange(services.Select(s => new RiskService { RiskId = id, ServiceCatalogItemId = s }));
        _db.RiskIncidents.AddRange(tickets.Select(t => new RiskIncident { RiskId = id, TicketId = t }));
        _db.RiskIncidents.AddRange(alerts.Select(a => new RiskIncident { RiskId = id, AlertId = a }));

        _db.AddAuditEvent(nameof(Risk), id.ToString(), AuditActions.RiskLinksChanged,
            new { risk.Number, changes, services, tickets, alerts });
        await _db.SaveChangesAsync(ct);

        return await LinksAsync(id, ct);
    }
}

using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;

namespace backend.Services;

// Feil som gir en bestemt HTTP-status (400, 403, 404 eller 409) med forklaring
public class GovernanceException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public static GovernanceException BadRequest(string message) => new(400, message);
    public static GovernanceException Forbidden(string message) => new(403, message);
    public static GovernanceException NotFound() => new(404, "Fant ikke endringen.");
    public static GovernanceException Conflict(string message) => new(409, message);
}

public record ChangeAzureScopeInput(int AzureSubscriptionId, string? ResourceGroup);

// Feltene en innmelder eller Change Manager kan fylle ut
public record ChangeInput(
    string? Title,
    string? Description,
    string? Justification,
    string? ExpectedBenefit,
    string? Type,
    string? Priority,
    string? Environment,
    int? Likelihood,
    int? Impact,
    string? RiskAnalysis,
    string? ImpactAnalysis,
    string? SecurityImpact,
    string? GdprImpact,
    int? ProcessingActivityId,
    decimal? CostChangePerMonth,
    string? CostChangeNote,
    string? BackupStatus,
    string? BackupNote,
    string? ImplementationPlan,
    string? TestPlan,
    string? TestResults,
    string? RollbackPlan,
    DateTime? PlannedStart,
    DateTime? PlannedEnd,
    int? ImplementerEmployeeId,
    List<int>? ServiceIds,
    List<ChangeAzureScopeInput>? AzureScopes,
    int? TemplateId = null
);

// Én godkjenningsregel og hvem som har godkjent etter den (med gyldig hash)
public record ApprovalRequirement(string Role, int MinApprovals, IReadOnlyList<string> ApprovedBy, bool Satisfied);

// Arbeidsflyten for endringer (ENDRING_OG_RISIKO.md, C2). Alt avgjøres her på
// serveren: overganger, roller, separasjon av plikter og godkjenning låst til
// innholdet (ChangeScopeHash). Klienten kan bare be om en overgang.
public class ChangeWorkflowService
{
    // Statuser der endringen kan redigeres
    private static readonly string[] EditableStatuses =
    [
        ChangeStatuses.Draft, ChangeStatuses.Submitted, ChangeStatuses.RiskAssessment,
        ChangeStatuses.TechnicalReview, ChangeStatuses.Testing, ChangeStatuses.PendingApproval,
        ChangeStatuses.Approved, ChangeStatuses.Scheduled,
    ];

    // Statuser der en godkjenning finnes og kan bli ugyldig
    private static readonly string[] ApprovedStatuses = [ChangeStatuses.Approved, ChangeStatuses.Scheduled];

    private static readonly string[] TerminalStatuses =
        [ChangeStatuses.Closed, ChangeStatuses.Rejected, ChangeStatuses.Cancelled];

    // Pseudo-rolle i overgangstabellen: den som meldte inn endringen
    private const string Requester = "requester";

    private record Transition(
        string From, string To, string[] Types, string[] Roles, Func<ChangeRequest, string?>? Validate = null);

    private static readonly string[] AllTypes = ChangeTypes.All;
    private static readonly string[] NormalOnly = [ChangeTypes.Normal];

    private static readonly Transition[] Transitions =
    [
        new(ChangeStatuses.Draft, ChangeStatuses.Submitted, AllTypes, [Requester], ValidateSubmit),
        new(ChangeStatuses.Submitted, ChangeStatuses.RiskAssessment, NormalOnly, [GovernanceRoles.ChangeManager]),
        new(ChangeStatuses.RiskAssessment, ChangeStatuses.TechnicalReview, NormalOnly,
            [GovernanceRoles.ChangeManager, GovernanceRoles.TechnicalOwner], ValidateRiskAssessment),
        new(ChangeStatuses.TechnicalReview, ChangeStatuses.Testing, NormalOnly, [GovernanceRoles.TechnicalOwner]),
        new(ChangeStatuses.Testing, ChangeStatuses.PendingApproval, NormalOnly,
            [GovernanceRoles.TechnicalOwner, GovernanceRoles.Implementer], ValidateTesting),
        new(ChangeStatuses.Approved, ChangeStatuses.Scheduled, NormalOnly, [GovernanceRoles.ChangeManager], ValidateWindow),
        new(ChangeStatuses.Scheduled, ChangeStatuses.Implementation,
            [ChangeTypes.Normal, ChangeTypes.Standard], [GovernanceRoles.Implementer]),
        new(ChangeStatuses.Approved, ChangeStatuses.Implementation, [ChangeTypes.Emergency], [GovernanceRoles.Implementer]),
        new(ChangeStatuses.Implementation, ChangeStatuses.Verification, AllTypes, [GovernanceRoles.Implementer]),
        new(ChangeStatuses.Implementation, ChangeStatuses.Failed, AllTypes, [GovernanceRoles.Implementer]),
        new(ChangeStatuses.Implementation, ChangeStatuses.RolledBack, AllTypes, [GovernanceRoles.Implementer]),
        new(ChangeStatuses.Verification, ChangeStatuses.Completed, AllTypes, [GovernanceRoles.Reviewer]),
        new(ChangeStatuses.Completed, ChangeStatuses.Closed, AllTypes, [GovernanceRoles.ChangeManager], ValidateClose),
    ];

    // Avbryting er mulig fra alle statuser før implementering
    private static readonly string[] CancellableFrom =
    [
        ChangeStatuses.Draft, ChangeStatuses.Submitted, ChangeStatuses.RiskAssessment,
        ChangeStatuses.TechnicalReview, ChangeStatuses.Testing, ChangeStatuses.PendingApproval,
        ChangeStatuses.Approved, ChangeStatuses.Scheduled,
    ];

    private readonly AppDbContext _db;
    private readonly GovernanceRoleService _roles;
    private readonly GovernanceNumberService _numbers;
    private readonly GovernanceSetupService _setup;
    private readonly ICurrentUser _currentUser;
    private readonly GovernanceNotifier _notifier;

    // Hendelser som gir Teams-kort når endringen er lagret (C5)
    private readonly List<string> _statusEvents = [];
    private readonly List<string> _infoRequests = [];

    public ChangeWorkflowService(
        AppDbContext db,
        GovernanceRoleService roles,
        GovernanceNumberService numbers,
        GovernanceSetupService setup,
        ICurrentUser currentUser,
        GovernanceNotifier notifier)
    {
        _db = db;
        _roles = roles;
        _numbers = numbers;
        _setup = setup;
        _currentUser = currentUser;
        _notifier = notifier;
    }

    // Lagrer, og sender deretter Teams-kort for hendelsene (feil i Teams fanges i køen)
    private async Task SaveAndNotifyAsync(ChangeRequest change, CancellationToken ct)
    {
        await _db.SaveChangesAsync(ct);

        var statuses = _statusEvents.ToList();
        var infoRoles = _infoRequests.ToList();
        _statusEvents.Clear();
        _infoRequests.Clear();

        foreach (var status in statuses.Distinct())
        {
            var missing = status == ChangeStatuses.PendingApproval
                ? (await RequirementsAsync(change, ct)).Where(r => !r.Satisfied).Select(r => r.Role).ToList()
                : [];
            await _notifier.ChangeStatusAsync(change, status, missing, ct);
        }

        foreach (var role in infoRoles)
            await _notifier.InfoRequestedAsync(change, role, ct);
    }

    private string ActorObjectId =>
        _currentUser.ObjectId?.ToLowerInvariant() ?? throw GovernanceException.Forbidden("Innlogget bruker mangler.");

    private string ActorName => _currentUser.Name ?? _currentUser.ObjectId ?? "ukjent";

    public Task<GovernanceRoleService.PersonRoles> MyRolesAsync(CancellationToken ct = default) =>
        _roles.ForAsync(_currentUser.ObjectId, ct);

    public async Task<ChangeRequest> LoadAsync(int id, CancellationToken ct = default) =>
        await _db.ChangeRequests
            .Include(c => c.Services)
            .Include(c => c.AzureScopes)
            .Include(c => c.Approvals)
            .FirstOrDefaultAsync(c => c.Id == id, ct)
        ?? throw GovernanceException.NotFound();

    private static List<int> ServiceIds(ChangeRequest change) =>
        change.Services.Select(s => s.ServiceCatalogItemId).ToList();

    private bool IsRequester(ChangeRequest change) =>
        string.Equals(change.RequestedByObjectId, ActorObjectId, StringComparison.OrdinalIgnoreCase);

    // Kan personen se endringen? Helpdesk.Agent, en endringsrolle, eller egen endring.
    public bool CanRead(ChangeRequest change, GovernanceRoleService.PersonRoles roles, bool isAgent) =>
        isAgent || roles.HasAny || IsRequester(change);

    // ---------- Opprette og endre ----------

    public async Task<ChangeRequest> CreateAsync(ChangeInput input, bool isAgent, CancellationToken ct = default)
    {
        var roles = await MyRolesAsync(ct);
        if (!isAgent && !roles.Has(GovernanceRoles.ChangeRequester))
            throw GovernanceException.Forbidden("Krever Helpdesk.Agent eller rollen Change Requester.");

        var type = input.Type ?? ChangeTypes.Normal;
        if (!ChangeTypes.All.Contains(type))
            throw GovernanceException.BadRequest("Ugyldig endringstype.");

        var employeeId = roles.EmployeeId;
        var now = DateTime.UtcNow;

        var change = new ChangeRequest
        {
            Type = type,
            Status = ChangeStatuses.Draft,
            RequestedByObjectId = ActorObjectId,
            RequestedByName = ActorName,
            RequestedByEmployeeId = employeeId,
            PostReviewRequired = type == ChangeTypes.Emergency,
            CreatedAt = now,
            UpdatedAt = now,
        };

        if (type == ChangeTypes.Standard)
        {
            var template = await ActiveTemplateAsync(input.TemplateId, ct);
            change.TemplateId = template.Id;
            change.ImplementationPlan = template.ImplementationPlan;
            change.TestPlan = template.TestPlan;
            change.RollbackPlan = template.RollbackPlan;
        }
        else if (input.TemplateId != null)
        {
            throw GovernanceException.BadRequest("Maler brukes bare for standardendringer.");
        }

        await ApplyAsync(change, input with { Type = type }, isNew: true, ct);

        change.Number = await _numbers.NextChangeNumberAsync(ct);
        change.ScopeHash = ChangeScopeHash.Compute(change);

        _db.ChangeRequests.Add(change);
        await SaveAndNotifyAsync(change, ct);
        return change;
    }

    public async Task<ChangeRequest> UpdateAsync(int id, ChangeInput input, CancellationToken ct = default)
    {
        var change = await LoadAsync(id, ct);
        var roles = await MyRolesAsync(ct);
        var services = ServiceIds(change);

        if (!IsRequester(change) &&
            !roles.Has(GovernanceRoles.ChangeManager, services) &&
            !roles.Has(GovernanceRoles.TechnicalOwner, services))
        {
            throw GovernanceException.Forbidden("Bare innmelder, Change Manager eller Technical Owner kan endre endringen.");
        }

        if (!EditableStatuses.Contains(change.Status))
            throw GovernanceException.BadRequest("Endringen kan ikke endres etter at implementeringen har startet.");

        if (input.Type != null && input.Type != change.Type)
            throw GovernanceException.BadRequest("Endringstypen kan ikke endres. Opprett en ny endring.");

        var hashBefore = change.ScopeHash;

        await ApplyAsync(change, input with { Type = change.Type }, isNew: false, ct);

        change.ScopeHash = ChangeScopeHash.Compute(change);
        change.UpdatedAt = DateTime.UtcNow;

        if (change.ScopeHash != hashBefore)
        {
            var hadApprovals = change.Approvals.Any(a => a.ScopeHash == hashBefore);

            // Godkjent innhold er endret: tilbake til godkjenning (ikke for standard,
            // som er godkjent gjennom malen og ikke kan få endrede planer)
            if (ApprovedStatuses.Contains(change.Status) && change.Type != ChangeTypes.Standard)
            {
                SetStatus(change, ChangeStatuses.PendingApproval, "Godkjent innhold er endret.");
            }

            if (hadApprovals)
            {
                _db.AddAuditEvent(nameof(ChangeRequest), change.Id.ToString(), AuditActions.ApprovalInvalidated,
                    new { change.Number, reason = "Godkjent innhold er endret." });
            }
        }

        await SaveAndNotifyAsync(change, ct);
        return change;
    }

    // Validerer og setter feltene. Standard: planene er låst til malen.
    private async Task ApplyAsync(ChangeRequest change, ChangeInput input, bool isNew, CancellationToken ct)
    {
        var title = input.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200)
            throw GovernanceException.BadRequest("Tittel er påkrevd (maks 200 tegn).");

        var priority = input.Priority ?? TicketPriorities.Normal;
        if (!TicketPriorities.All.Contains(priority))
            throw GovernanceException.BadRequest("Ugyldig prioritet.");

        var environment = input.Environment ?? ChangeEnvironments.Development;
        if (!ChangeEnvironments.All.Contains(environment))
            throw GovernanceException.BadRequest("Ugyldig miljø.");

        var backup = input.BackupStatus ?? BackupStatuses.Unverified;
        if (!BackupStatuses.All.Contains(backup))
            throw GovernanceException.BadRequest("Ugyldig backupstatus.");

        if (input.Likelihood is { } l && !RiskScore.IsValid(l) || input.Impact is { } i && !RiskScore.IsValid(i))
            throw GovernanceException.BadRequest("Sannsynlighet og konsekvens må være 1–5.");

        if (input.PlannedStart is { } start && input.PlannedEnd is { } end && end <= start)
            throw GovernanceException.BadRequest("Planlagt slutt må være etter planlagt start.");

        CheckLength(input.Description, 8000, "Beskrivelse");
        CheckLength(input.Justification, 4000, "Begrunnelse");
        CheckLength(input.ExpectedBenefit, 4000, "Forventet gevinst");
        CheckLength(input.RiskAnalysis, 8000, "Risikoanalyse");
        CheckLength(input.ImpactAnalysis, 8000, "Konsekvensanalyse");
        CheckLength(input.SecurityImpact, 4000, "Sikkerhetskonsekvens");
        CheckLength(input.GdprImpact, 4000, "GDPR-konsekvens");
        CheckLength(input.CostChangeNote, 1000, "Kostnadsnotat");
        CheckLength(input.BackupNote, 1000, "Backupnotat");
        CheckLength(input.ImplementationPlan, 8000, "Implementeringsplan");
        CheckLength(input.TestPlan, 8000, "Testplan");
        CheckLength(input.TestResults, 8000, "Testresultater");
        CheckLength(input.RollbackPlan, 8000, "Rollback-plan");

        var serviceIds = (input.ServiceIds ?? []).Distinct().ToList();
        if (serviceIds.Count > 0 &&
            await _db.ServiceCatalogItems.CountAsync(s => serviceIds.Contains(s.Id), ct) != serviceIds.Count)
        {
            throw GovernanceException.BadRequest("Ukjent system eller tjeneste.");
        }

        var scopes = (input.AzureScopes ?? [])
            .Select(s => new ChangeAzureScopeInput(s.AzureSubscriptionId, string.IsNullOrWhiteSpace(s.ResourceGroup) ? null : s.ResourceGroup.Trim()))
            .Distinct()
            .ToList();
        if (scopes.Any(s => s.ResourceGroup?.Length > 90))
            throw GovernanceException.BadRequest("Ressursgruppenavn kan ha maks 90 tegn.");
        var subscriptionIds = scopes.Select(s => s.AzureSubscriptionId).Distinct().ToList();
        if (subscriptionIds.Count > 0 &&
            await _db.AzureSubscriptions.CountAsync(s => subscriptionIds.Contains(s.Id), ct) != subscriptionIds.Count)
        {
            throw GovernanceException.BadRequest("Ukjent Azure-abonnement.");
        }

        if (input.ProcessingActivityId is { } pa && !await _db.ProcessingActivities.AnyAsync(p => p.Id == pa, ct))
            throw GovernanceException.BadRequest("Ukjent behandling i protokollen.");

        if (input.ImplementerEmployeeId is { } emp && !await _db.Employees.AnyAsync(e => e.Id == emp, ct))
            throw GovernanceException.BadRequest("Ukjent ansatt som implementør.");

        // Standard: planene kommer fra malen og kan ikke endres
        if (change.Type == ChangeTypes.Standard && !isNew &&
            (Differs(input.ImplementationPlan, change.ImplementationPlan) ||
             Differs(input.TestPlan, change.TestPlan) ||
             Differs(input.RollbackPlan, change.RollbackPlan)))
        {
            throw GovernanceException.BadRequest("Planene i en standardendring kommer fra malen og kan ikke endres.");
        }

        change.Title = title;
        change.Description = input.Description?.Trim() ?? "";
        change.Justification = Clean(input.Justification);
        change.ExpectedBenefit = Clean(input.ExpectedBenefit);
        change.Priority = priority;
        change.Environment = environment;
        change.Likelihood = input.Likelihood;
        change.Impact = input.Impact;
        change.RiskAnalysis = Clean(input.RiskAnalysis);
        change.ImpactAnalysis = Clean(input.ImpactAnalysis);
        change.SecurityImpact = Clean(input.SecurityImpact);
        change.GdprImpact = Clean(input.GdprImpact);
        change.ProcessingActivityId = input.ProcessingActivityId;
        change.CostChangePerMonth = input.CostChangePerMonth;
        change.CostChangeNote = Clean(input.CostChangeNote);
        change.BackupStatus = backup;
        change.BackupNote = Clean(input.BackupNote);
        change.TestResults = Clean(input.TestResults);
        change.PlannedStart = input.PlannedStart is { } ps ? DateTime.SpecifyKind(ps, DateTimeKind.Utc) : null;
        change.PlannedEnd = input.PlannedEnd is { } pe ? DateTime.SpecifyKind(pe, DateTimeKind.Utc) : null;
        change.ImplementerEmployeeId = input.ImplementerEmployeeId ?? change.ImplementerEmployeeId;

        if (change.Type != ChangeTypes.Standard)
        {
            change.ImplementationPlan = Clean(input.ImplementationPlan);
            change.TestPlan = Clean(input.TestPlan);
            change.RollbackPlan = Clean(input.RollbackPlan);
        }

        // Tjenester og Azure-omfang: bytt ut listene
        change.Services.RemoveAll(s => !serviceIds.Contains(s.ServiceCatalogItemId));
        foreach (var serviceId in serviceIds.Where(id => change.Services.All(s => s.ServiceCatalogItemId != id)))
            change.Services.Add(new ChangeAffectedService { ServiceCatalogItemId = serviceId });

        change.AzureScopes.RemoveAll(s => !scopes.Contains(new ChangeAzureScopeInput(s.AzureSubscriptionId, s.ResourceGroup)));
        foreach (var scope in scopes.Where(s => !change.AzureScopes.Any(a =>
                     a.AzureSubscriptionId == s.AzureSubscriptionId && a.ResourceGroup == s.ResourceGroup)))
        {
            change.AzureScopes.Add(new ChangeAzureScope
            {
                AzureSubscriptionId = scope.AzureSubscriptionId,
                ResourceGroup = scope.ResourceGroup,
            });
        }
    }

    private static void CheckLength(string? value, int max, string field)
    {
        if (value?.Length > max)
            throw GovernanceException.BadRequest($"{field} kan ha maks {max} tegn.");
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Null betyr «ikke sendt», og regnes ikke som en endring
    private static bool Differs(string? input, string? current) =>
        input != null && Clean(input) != Clean(current);

    private async Task<ChangeTemplate> ActiveTemplateAsync(int? templateId, CancellationToken ct)
    {
        if (templateId == null)
            throw GovernanceException.BadRequest("En standardendring må lages fra en mal.");

        var template = await _db.ChangeTemplates.FirstOrDefaultAsync(t => t.Id == templateId, ct)
            ?? throw GovernanceException.BadRequest("Ukjent mal.");

        if (!template.IsActive || template.ApprovedAt == null)
            throw GovernanceException.BadRequest("Malen er ikke aktiv og forhåndsgodkjent.");

        if (template.NextReviewAt is { } review && review < DateTime.UtcNow)
            throw GovernanceException.BadRequest("Malen har passert datoen for neste gjennomgang.");

        return template;
    }

    // ---------- Vilkår for overganger ----------

    private static string? ValidateSubmit(ChangeRequest change)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(change.Description)) missing.Add("beskrivelse");
        if (change.Services.Count == 0) missing.Add("minst én tjeneste");

        if (change.Type != ChangeTypes.Standard)
        {
            if (change.Justification == null) missing.Add("begrunnelse");
            if (change.ImplementationPlan == null) missing.Add("implementeringsplan");
            if (change.RollbackPlan == null) missing.Add("rollback-plan");
        }

        if (change.Type == ChangeTypes.Normal)
        {
            if (change.TestPlan == null) missing.Add("testplan");
            if (change.PlannedStart == null || change.PlannedEnd == null) missing.Add("planlagt tidspunkt");
        }

        if (change.Type == ChangeTypes.Standard)
        {
            if (change.PlannedStart == null || change.PlannedEnd == null) missing.Add("planlagt tidspunkt");
            if (change.Likelihood == null || change.Impact == null) missing.Add("sannsynlighet og konsekvens");
        }

        if (missing.Count > 0)
            return $"Mangler før innsending: {string.Join(", ", missing)}.";

        if (change.Environment == ChangeEnvironments.Production && change.BackupStatus == BackupStatuses.Missing)
            return "En produksjonsendring kan ikke sendes inn når backup mangler.";

        return null;
    }

    private static string? ValidateRiskAssessment(ChangeRequest change) =>
        change.Likelihood == null || change.Impact == null || change.RiskAnalysis == null ||
        change.SecurityImpact == null || change.GdprImpact == null
            ? "Fyll ut sannsynlighet, konsekvens, risikoanalyse, sikkerhets- og GDPR-konsekvens."
            : null;

    private static string? ValidateTesting(ChangeRequest change) =>
        change.Environment != ChangeEnvironments.Development && change.TestResults == null
            ? "Fyll ut testresultater før godkjenning."
            : null;

    private static string? ValidateWindow(ChangeRequest change) =>
        change.PlannedStart == null || change.PlannedEnd == null
            ? "Planlagt start og slutt mangler."
            : null;

    private static string? ValidateClose(ChangeRequest change) =>
        change.PostReviewRequired && change.PostReviewedAt == null
            ? "En hasteendring kan ikke lukkes før etterkontrollen er gjort."
            : null;

    // ---------- Overganger ----------

    // Overgangene innlogget person kan gjøre nå (vises i detaljene)
    public async Task<List<string>> AllowedTransitionsAsync(
        ChangeRequest change, GovernanceRoleService.PersonRoles roles, CancellationToken ct = default)
    {
        var allowed = new List<string>();

        foreach (var transition in Transitions.Where(t => t.From == change.Status && t.Types.Contains(change.Type)))
        {
            if (HasTransitionRole(change, transition, roles))
                allowed.Add(transition.To);
        }

        if (CancellableFrom.Contains(change.Status) &&
            (IsRequester(change) || roles.Has(GovernanceRoles.ChangeManager, ServiceIds(change))))
        {
            allowed.Add(ChangeStatuses.Cancelled);
        }

        await Task.CompletedTask;
        return allowed.Distinct().ToList();
    }

    private bool HasTransitionRole(ChangeRequest change, Transition transition, GovernanceRoleService.PersonRoles roles) =>
        transition.Roles.Any(role => role == Requester ? IsRequester(change) : roles.Has(role, ServiceIds(change)));

    public async Task<ChangeRequest> TransitionAsync(int id, string? to, string? note, CancellationToken ct = default)
    {
        var change = await LoadAsync(id, ct);
        var roles = await MyRolesAsync(ct);
        note = Clean(note);

        if (note?.Length > 4000)
            throw GovernanceException.BadRequest("Notatet kan ha maks 4000 tegn.");

        if (to == ChangeStatuses.Cancelled)
        {
            if (!CancellableFrom.Contains(change.Status))
                throw GovernanceException.BadRequest("Endringen kan ikke avbrytes etter at implementeringen har startet.");
            if (!IsRequester(change) && !roles.Has(GovernanceRoles.ChangeManager, ServiceIds(change)))
                throw GovernanceException.Forbidden("Bare innmelder eller Change Manager kan avbryte.");

            SetStatus(change, ChangeStatuses.Cancelled, note);
            await SaveAndNotifyAsync(change, ct);
            return change;
        }

        var transition = Transitions.FirstOrDefault(t =>
            t.From == change.Status && t.To == to && t.Types.Contains(change.Type))
            ?? throw GovernanceException.BadRequest($"Ugyldig overgang fra {change.Status} til {to}.");

        if (!HasTransitionRole(change, transition, roles))
            throw GovernanceException.Forbidden("Du har ikke rollen som kreves for denne overgangen.");

        if (transition.Validate?.Invoke(change) is { } problem)
            throw GovernanceException.BadRequest(problem);

        switch (to)
        {
            case ChangeStatuses.Submitted when change.Type == ChangeTypes.Standard:
                await ValidateStandardAsync(change, ct);
                break;

            case ChangeStatuses.Implementation:
                await RequireValidApprovalAsync(change, ct);

                // Implementøren registreres, så hen ikke kan godkjenne eller verifisere selv
                if (roles.EmployeeId == null)
                    throw GovernanceException.Forbidden("Du må være registrert som ansatt for å implementere.");
                if (change.ImplementerEmployeeId != null && change.ImplementerEmployeeId != roles.EmployeeId)
                    throw GovernanceException.Forbidden("Endringen er tildelt en annen implementør.");
                change.ImplementerEmployeeId = roles.EmployeeId;
                break;

            case ChangeStatuses.Verification:
            case ChangeStatuses.Failed:
            case ChangeStatuses.RolledBack:
                if (note == null)
                    throw GovernanceException.BadRequest("Beskriv resultatet av implementeringen.");
                change.ImplementationResult = note;
                break;

            case ChangeStatuses.Completed:
                if (await IsImplementerAsync(change, ct))
                    throw GovernanceException.Forbidden("Den som implementerte kan ikke verifisere endringen.");
                break;
        }

        SetStatus(change, to!, note);

        // Standard går rett til planlagt; emergency rett til hurtiggodkjenning
        if (to == ChangeStatuses.Submitted && change.Type == ChangeTypes.Standard)
            SetStatus(change, ChangeStatuses.Scheduled, "Forhåndsgodkjent gjennom malen.");
        else if (to == ChangeStatuses.Submitted && change.Type == ChangeTypes.Emergency)
            SetStatus(change, ChangeStatuses.PendingApproval, "Hasteendring til hurtiggodkjenning.");

        await SaveAndNotifyAsync(change, ct);
        return change;
    }

    private async Task ValidateStandardAsync(ChangeRequest change, CancellationToken ct)
    {
        var template = await ActiveTemplateAsync(change.TemplateId, ct);

        var allowed = template.AllowedEnvironments.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (!allowed.Contains(change.Environment))
            throw GovernanceException.BadRequest("Malen kan ikke brukes i dette miljøet.");

        if (RiskScore.Of(change.Likelihood, change.Impact) > template.MaxRiskScore)
            throw GovernanceException.BadRequest(
                $"Risikoscoren er høyere enn malen tillater ({template.MaxRiskScore}). Bruk en normal endring.");

        if (Clean(change.ImplementationPlan) != Clean(template.ImplementationPlan) ||
            Clean(change.TestPlan) != Clean(template.TestPlan) ||
            Clean(change.RollbackPlan) != Clean(template.RollbackPlan))
        {
            throw GovernanceException.BadRequest("Planene samsvarer ikke lenger med malen.");
        }
    }

    // Ingen implementering uten gyldig godkjenning: standard via aktiv mal, ellers
    // godkjenninger etter reglene med samme hash som endringen har nå
    private async Task RequireValidApprovalAsync(ChangeRequest change, CancellationToken ct)
    {
        if (ChangeScopeHash.Compute(change) != change.ScopeHash)
            throw GovernanceException.Conflict("Endringen er endret siden den ble godkjent.");

        if (change.Type == ChangeTypes.Standard)
        {
            await ValidateStandardAsync(change, ct);
            return;
        }

        var requirements = await RequirementsAsync(change, ct);
        if (requirements.Count == 0 || requirements.Any(r => !r.Satisfied))
            throw GovernanceException.Forbidden("Endringen mangler gyldig godkjenning.");
    }

    private void SetStatus(ChangeRequest change, string to, string? note)
    {
        var from = change.Status;
        change.Status = to;
        change.UpdatedAt = DateTime.UtcNow;
        _statusEvents.Add(to);

        if (TerminalStatuses.Contains(to))
            change.ClosedAt ??= DateTime.UtcNow;

        _db.SetAuditAction(change, AuditActions.ChangeStatusChanged);
        _db.AddAuditEvent(nameof(ChangeRequest), change.Id == 0 ? change.Number : change.Id.ToString(),
            AuditActions.ChangeStatusChanged, new { change.Number, from, to, note });
    }

    // ---------- Godkjenning ----------

    // Reglene som gjelder endringen (type og miljø). Produksjon krever alltid minst
    // Change Manager, også om reglene er slettet.
    private async Task<List<ApprovalRule>> RulesAsync(ChangeRequest change, CancellationToken ct)
    {
        if (change.Type == ChangeTypes.Standard)
            return [];

        await _setup.EnsureDefaultsAsync(ct);

        var rules = await _db.ApprovalRules
            .AsNoTracking()
            .Where(r => r.ChangeType == change.Type &&
                (r.Environment == change.Environment || r.Environment == ApprovalRule.AnyEnvironment))
            .OrderBy(r => r.SortOrder).ThenBy(r => r.Id)
            .ToListAsync(ct);

        if (rules.Count == 0 && change.Environment == ChangeEnvironments.Production)
        {
            rules.Add(new ApprovalRule
            {
                ChangeType = change.Type, Environment = change.Environment,
                RequiredRole = GovernanceRoles.ChangeManager, MinApprovals = 1,
            });
        }

        return rules;
    }

    // Hvem som oppfyller hver regel. Bare gyldige beslutninger (samme hash), siste
    // beslutning per person og rolle, aldri innmelder eller implementør, og hver
    // person teller for bare én regel.
    public async Task<List<ApprovalRequirement>> RequirementsAsync(ChangeRequest change, CancellationToken ct = default)
    {
        var rules = await RulesAsync(change, ct);
        var excluded = await ExcludedApproversAsync(change, ct);

        var latest = change.Approvals
            .Where(a => a.ScopeHash == change.ScopeHash)
            .GroupBy(a => (Approver: a.ApproverObjectId.ToLowerInvariant(), a.Role))
            .Select(g => g.OrderByDescending(a => a.DecidedAt).ThenByDescending(a => a.Id).First())
            .Where(a => a.Decision == ApprovalDecisions.Approved && !excluded.Contains(a.ApproverObjectId.ToLowerInvariant()))
            .OrderBy(a => a.DecidedAt)
            .ToList();

        var used = new HashSet<string>();
        var result = new List<ApprovalRequirement>();

        foreach (var rule in rules)
        {
            var approvers = latest
                .Where(a => a.Role == rule.RequiredRole && !used.Contains(a.ApproverObjectId.ToLowerInvariant()))
                .Take(rule.MinApprovals)
                .ToList();

            foreach (var a in approvers)
                used.Add(a.ApproverObjectId.ToLowerInvariant());

            result.Add(new ApprovalRequirement(
                rule.RequiredRole, rule.MinApprovals,
                approvers.Select(a => a.ApproverName).ToList(),
                approvers.Count >= rule.MinApprovals));
        }

        return result;
    }

    // Innmelder og implementør (objekt-ID-er) kan ikke godkjenne
    private async Task<HashSet<string>> ExcludedApproversAsync(ChangeRequest change, CancellationToken ct)
    {
        var excluded = new HashSet<string> { change.RequestedByObjectId.ToLowerInvariant() };

        if (change.ImplementerEmployeeId is { } implementer)
        {
            var oid = await _db.Employees
                .Where(e => e.Id == implementer)
                .Select(e => e.EntraObjectId)
                .FirstOrDefaultAsync(ct);

            if (oid != null)
                excluded.Add(oid.ToLowerInvariant());
        }

        return excluded;
    }

    private async Task<bool> IsImplementerAsync(ChangeRequest change, CancellationToken ct)
    {
        if (change.ImplementerEmployeeId == null)
            return false;

        var excluded = await ExcludedApproversAsync(change, ct);
        excluded.Remove(change.RequestedByObjectId.ToLowerInvariant());
        return excluded.Contains(ActorObjectId);
    }

    public async Task<ChangeRequest> DecideAsync(
        int id, string? role, string? decision, string? comment, string? expectedScopeHash, CancellationToken ct = default)
    {
        var change = await LoadAsync(id, ct);
        var roles = await MyRolesAsync(ct);

        if (change.Status != ChangeStatuses.PendingApproval)
            throw GovernanceException.BadRequest("Endringen venter ikke på godkjenning.");

        if (decision == null || !ApprovalDecisions.All.Contains(decision))
            throw GovernanceException.BadRequest("Ugyldig beslutning.");

        comment = Clean(comment);
        if (comment?.Length > 2000)
            throw GovernanceException.BadRequest("Kommentaren kan ha maks 2000 tegn.");
        if (decision != ApprovalDecisions.Approved && comment == null)
            throw GovernanceException.BadRequest("Skriv en begrunnelse.");

        var rules = await RulesAsync(change, ct);
        if (role == null || rules.All(r => r.RequiredRole != role))
            throw GovernanceException.BadRequest("Rollen er ikke en påkrevd godkjenner for denne endringen.");

        if (!roles.Has(role, ServiceIds(change)))
            throw GovernanceException.Forbidden("Du har ikke rollen du forsøker å godkjenne i.");

        // Separasjon av plikter
        var excluded = await ExcludedApproversAsync(change, ct);
        if (excluded.Contains(ActorObjectId))
            throw GovernanceException.Forbidden("Den som har meldt inn eller skal implementere endringen, kan ikke godkjenne den.");

        // Godkjenningen gjelder innholdet personen så
        if (string.IsNullOrWhiteSpace(expectedScopeHash) ||
            !string.Equals(expectedScopeHash, change.ScopeHash, StringComparison.OrdinalIgnoreCase))
        {
            throw GovernanceException.Conflict("Endringen er endret siden du åpnet den. Se gjennom den på nytt.");
        }

        // Én person teller bare for én påkrevd regel
        var otherRole = change.Approvals.Any(a =>
            a.ScopeHash == change.ScopeHash &&
            a.Role != role &&
            a.Decision == ApprovalDecisions.Approved &&
            string.Equals(a.ApproverObjectId, ActorObjectId, StringComparison.OrdinalIgnoreCase));
        if (otherRole && decision == ApprovalDecisions.Approved)
            throw GovernanceException.Forbidden("Du har allerede godkjent i en annen rolle. Én person kan bare godkjenne én gang.");

        change.Approvals.Add(new ChangeApproval
        {
            ChangeRequestId = change.Id,
            Role = role,
            Decision = decision,
            ApproverObjectId = ActorObjectId,
            ApproverName = ActorName,
            ApproverEmployeeId = roles.EmployeeId,
            DecidedAt = DateTime.UtcNow,
            Comment = comment,
            ScopeHash = change.ScopeHash!,
        });

        var action = decision switch
        {
            ApprovalDecisions.Approved => AuditActions.ChangeApproved,
            ApprovalDecisions.Rejected => AuditActions.ChangeRejected,
            _ => AuditActions.ChangeInfoRequested,
        };
        _db.AddAuditEvent(nameof(ChangeRequest), change.Id.ToString(), action,
            new { change.Number, role, decision, comment });

        if (decision == ApprovalDecisions.InfoRequested)
            _infoRequests.Add(role);

        if (decision == ApprovalDecisions.Rejected)
        {
            SetStatus(change, ChangeStatuses.Rejected, comment);
        }
        else if (decision == ApprovalDecisions.Approved &&
                 (await RequirementsAsync(change, ct)).All(r => r.Satisfied))
        {
            SetStatus(change, ChangeStatuses.Approved, "Alle påkrevde godkjenninger er gitt.");
        }

        await SaveAndNotifyAsync(change, ct);
        return change;
    }

    // Venter endringen på mer informasjon? (siste gyldige beslutning er info_requested)
    public static bool WaitingForInfo(ChangeRequest change) =>
        change.Status == ChangeStatuses.PendingApproval &&
        change.Approvals
            .Where(a => a.ScopeHash == change.ScopeHash)
            .OrderByDescending(a => a.DecidedAt).ThenByDescending(a => a.Id)
            .FirstOrDefault()?.Decision == ApprovalDecisions.InfoRequested;

    // ---------- Etterkontroll (emergency) ----------

    public async Task<ChangeRequest> PostReviewAsync(int id, string? notes, CancellationToken ct = default)
    {
        var change = await LoadAsync(id, ct);
        var roles = await MyRolesAsync(ct);
        notes = Clean(notes);

        if (!change.PostReviewRequired)
            throw GovernanceException.BadRequest("Endringen krever ikke etterkontroll.");
        if (change.Status != ChangeStatuses.Completed)
            throw GovernanceException.BadRequest("Etterkontrollen gjøres når endringen er gjennomført.");
        if (change.PostReviewedAt != null)
            throw GovernanceException.BadRequest("Etterkontrollen er allerede gjort.");
        if (notes == null || notes.Length > 4000)
            throw GovernanceException.BadRequest("Skriv funnene fra etterkontrollen (maks 4000 tegn).");

        var services = ServiceIds(change);
        if (!roles.Has(GovernanceRoles.Reviewer, services) && !roles.Has(GovernanceRoles.ChangeManager, services))
            throw GovernanceException.Forbidden("Etterkontroll krever rollen Reviewer eller Change Manager.");
        if (IsRequester(change) || await IsImplementerAsync(change, ct))
            throw GovernanceException.Forbidden("Den som meldte inn eller implementerte, kan ikke gjøre etterkontrollen.");

        change.PostReviewNotes = notes;
        change.PostReviewedAt = DateTime.UtcNow;
        change.PostReviewedByName = ActorName;
        change.UpdatedAt = DateTime.UtcNow;
        _db.SetAuditAction(change, AuditActions.ChangePostReviewed);

        await SaveAndNotifyAsync(change, ct);
        return change;
    }
}

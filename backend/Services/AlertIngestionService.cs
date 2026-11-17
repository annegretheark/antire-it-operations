using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;

namespace backend.Services;

// Et varsel slik en kilde leverer det (Defender, Azure Monitor eller simulert)
public record AlertSignal(
    string Source,
    string ExternalId,
    string Title,
    string Severity,
    string? ResourceId,
    string? PortalUrl,
    // fired | resolved
    string State,
    // Når varselet fyrte (siste aktivitet); nytt tidspunkt = ny forekomst
    DateTime FiredAt,
    string? RawPayload
);

// Hva mottaket gjorde med varselet
public static class AlertOutcomes
{
    public const string TicketCreated = "ticket_created";
    public const string TicketReopened = "ticket_reopened";
    // Ny forekomst på en åpen sak
    public const string Recurred = "recurred";
    // Lagret uten sak (under terskelen eller regelen lager ikke saker)
    public const string StoredOnly = "stored_only";
    public const string Resolved = "resolved";
    // Samme forekomst som før (f.eks. hentet på nytt)
    public const string Unchanged = "unchanged";
}

public record AlertIngestResult(int AlertId, int? TicketId, string Outcome);

// Felles inngangspunkt for alle varselkilder (4.1): finn eller opprett varselet,
// lag eller gjenåpne sak etter regelen, og varsle Teams.
public class AlertIngestionService
{
    private readonly AppDbContext _db;
    private readonly AlertRuleService _rules;
    private readonly SlaService _sla;
    private readonly TicketAssignmentService _assignment;
    private readonly TeamsNotificationService _teams;

    public AlertIngestionService(
        AppDbContext db,
        AlertRuleService rules,
        SlaService sla,
        TicketAssignmentService assignment,
        TeamsNotificationService teams)
    {
        _db = db;
        _rules = rules;
        _sla = sla;
        _assignment = assignment;
        _teams = teams;
    }

    public async Task<AlertIngestResult> IngestAsync(AlertSignal signal, CancellationToken cancellationToken = default)
    {
        // Standardmålene lagres for seg, før varselet og saken endres
        await _sla.EnsureSeededAsync(cancellationToken);
        var rule = await _rules.GetAsync(signal.Source, cancellationToken);

        var now = DateTime.UtcNow;
        var firedAt = DateTime.SpecifyKind(signal.FiredAt, DateTimeKind.Utc);
        var alert = await _db.Alerts.FirstOrDefaultAsync(
            a => a.Source == signal.Source && a.ExternalId == signal.ExternalId, cancellationToken);

        bool newFiring;

        if (alert == null)
        {
            alert = new Alert
            {
                Source = signal.Source,
                ExternalId = signal.ExternalId,
                FirstSeenAt = now,
                LastFiredAt = firedAt,
                Occurrences = 1,
                State = signal.State,
            };
            Copy(signal, alert, now);
            _db.Alerts.Add(alert);
            newFiring = signal.State == AlertStates.Fired;
        }
        // «Løst» for en eldre forekomst enn den siste gjelder ikke lenger
        else if (signal.State == AlertStates.Resolved && alert.State != AlertStates.Resolved &&
                 firedAt >= alert.LastFiredAt)
        {
            Copy(signal, alert, now);
            alert.State = AlertStates.Resolved;
            await _db.SaveChangesAsync(cancellationToken);

            // Logges på saken; saken lukkes ikke automatisk
            if (alert.TicketId is int resolvedTicketId)
            {
                LogOnTicket(resolvedTicketId, AuditActions.AlertResolved, alert);
                await _db.SaveChangesAsync(cancellationToken);
            }

            return new(alert.Id, alert.TicketId, AlertOutcomes.Resolved);
        }
        else if (signal.State == AlertStates.Fired &&
                 (firedAt > alert.LastFiredAt || alert.State == AlertStates.Resolved))
        {
            Copy(signal, alert, now);
            alert.Occurrences++;
            alert.LastFiredAt = firedAt;
            alert.State = AlertStates.Fired;
            _db.SetAuditAction(alert, AuditActions.AlertRecurred);
            newFiring = true;
        }
        else
        {
            // Samme forekomst hentet på nytt: ingenting endres, og ingenting logges
            return new(alert.Id, alert.TicketId, AlertOutcomes.Unchanged);
        }

        await _db.SaveChangesAsync(cancellationToken);

        if (!newFiring)
            return new(alert.Id, alert.TicketId, AlertOutcomes.StoredOnly);

        var createsTicket = rule.CreateTickets &&
            AlertSeverities.Rank(alert.Source, alert.Severity) <= AlertSeverities.Rank(alert.Source, rule.MinSeverity);

        if (!createsTicket)
            return new(alert.Id, alert.TicketId, AlertOutcomes.StoredOnly);

        if (alert.TicketId is int ticketId &&
            await _db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId, cancellationToken) is { } existing)
        {
            if (existing.Status != TicketStatuses.Resolved)
            {
                LogOnTicket(existing.Id, AuditActions.AlertRecurred, alert);
                await _db.SaveChangesAsync(cancellationToken);
                return new(alert.Id, existing.Id, AlertOutcomes.Recurred);
            }

            // Fyrer varselet etter at saken er løst, gjenåpnes saken
            existing.Status = TicketStatuses.New;
            existing.ResolvedAt = null;
            _db.SetAuditAction(existing, AuditActions.ReopenedByAlert);
            await _db.SaveChangesAsync(cancellationToken);

            if (rule.NotifyTeams)
                await _teams.NotifyAlertTicketAsync(existing, alert, reopened: true, cancellationToken);

            return new(alert.Id, existing.Id, AlertOutcomes.TicketReopened);
        }

        var ticket = new Ticket
        {
            Category = rule.Category,
            Description = Describe(alert),
            Status = TicketStatuses.New,
            Priority = rule.PriorityOverride ?? AlertSeverities.PriorityFor(alert.Source, alert.Severity),
            CreatedAt = now,
            Source = TicketSources.Alert,
            AlertId = alert.Id,
        };

        await _sla.ApplyDeadlinesAsync(ticket, cancellationToken);
        ticket.AssignedToEmployeeId = await _assignment.FindAssigneeAsync(ticket.Category);

        _db.Tickets.Add(ticket);
        await _db.SaveChangesAsync(cancellationToken);

        alert.TicketId = ticket.Id;
        await _db.SaveChangesAsync(cancellationToken);

        if (rule.NotifyTeams)
            await _teams.NotifyAlertTicketAsync(ticket, alert, reopened: false, cancellationToken);

        return new(alert.Id, ticket.Id, AlertOutcomes.TicketCreated);
    }

    private static void Copy(AlertSignal signal, Alert alert, DateTime now)
    {
        alert.Title = Limit(signal.Title, 500);
        alert.Severity = signal.Severity;
        alert.ResourceId = signal.ResourceId is { } r ? Limit(r, 1000) : null;
        alert.PortalUrl = signal.PortalUrl is { } u ? Limit(u, 2000) : null;
        alert.RawPayload = signal.RawPayload;
        alert.LastSeenAt = now;
    }

    private void LogOnTicket(int ticketId, string action, Alert alert) =>
        _db.AddAuditEvent(nameof(Ticket), ticketId.ToString(), action, new
        {
            alertId = alert.Id,
            occurrences = alert.Occurrences,
        });

    // Sakstekst: tittel, alvorlighetsgrad, ressurs, tidspunkt og lenke
    private static string Describe(Alert alert)
    {
        var source = alert.Source == AlertSources.Defender ? "Microsoft Defender" : "Azure Monitor";
        var lines = new List<string>
        {
            $"[{source}] {alert.Title}",
            $"Alvorlighetsgrad: {alert.Severity}",
        };

        if (!string.IsNullOrEmpty(alert.ResourceId))
            lines.Add($"Ressurs: {alert.ResourceId}");

        lines.Add($"Fyrte: {alert.LastFiredAt:yyyy-MM-dd HH:mm} UTC");

        if (!string.IsNullOrEmpty(alert.PortalUrl))
            lines.Add($"Varselet: {alert.PortalUrl}");

        return string.Join("\n", lines);
    }

    private static string Limit(string value, int max) => value.Length > max ? value[..max] : value;
}

// Varselregler per kilde. En tenant uten regler får standardreglene første gang:
// Azure Sev0–2 og Defender høy/middels gir sak.
public class AlertRuleService
{
    private readonly AppDbContext _db;
    private readonly ILogger<AlertRuleService> _logger;

    public AlertRuleService(AppDbContext db, ILogger<AlertRuleService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public static IReadOnlyList<AlertRule> DefaultRules() =>
    [
        new() { Source = AlertSources.AzureMonitor, MinSeverity = "Sev2", Category = "Feil" },
        new() { Source = AlertSources.Defender, MinSeverity = "medium", Category = "Sikkerhet" },
    ];

    public async Task<List<AlertRule>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSeededAsync(cancellationToken);
        return await _db.AlertRules.OrderBy(r => r.Source).ToListAsync(cancellationToken);
    }

    public async Task<AlertRule> GetAsync(string source, CancellationToken cancellationToken = default)
    {
        await EnsureSeededAsync(cancellationToken);
        return await _db.AlertRules.FirstOrDefaultAsync(r => r.Source == source, cancellationToken)
            ?? DefaultRules().First(r => r.Source == source);
    }

    private async Task EnsureSeededAsync(CancellationToken cancellationToken)
    {
        if (await _db.AlertRules.AnyAsync(cancellationToken))
            return;

        _db.AlertRules.AddRange(DefaultRules());

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e)
        {
            // En annen forespørsel rakk å opprette reglene først
            _logger.LogInformation(e, "Standard varselregler fantes allerede.");

            foreach (var entry in _db.ChangeTracker.Entries<AlertRule>().ToList())
                entry.State = EntityState.Detached;
        }
    }
}

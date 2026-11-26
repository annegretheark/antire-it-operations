using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using backend.Data;
using backend.Models;

namespace backend.Services;

// Følger med på endringer og risikoregisteret i alle tillatte tenanter
// (ENDRING_OG_RISIKO.md, C3 og C5). Hver hendelse logges én gang i revisjonsloggen
// (aktør «system»), også etter omstart, og gir et Teams-kort:
//   - risiko: tiltak forbi frist, vurdering forfalt, ikke behandlet (høy/kritisk uten
//     eier, eller identifisert i mer enn 7 dager), aksept utløper om 14 dager, aksept
//     utløpt (risikoen går tilbake til «vurdert»)
//   - endring: produksjonsendring starter innen ReminderDaysBefore, hasteendring
//     gjennomført for over ett døgn siden uten etterkontroll
//
// Konfigurasjon: Governance:MonitorEnabled og Governance:CheckIntervalSeconds
// (3600). De eldre navnene Risks:MonitorEnabled og Risks:CheckIntervalSeconds virker også.
public class GovernanceMonitorService : BackgroundService
{
    public static readonly TimeSpan UntreatedAfter = TimeSpan.FromDays(7);
    public static readonly TimeSpan AcceptanceWarning = TimeSpan.FromDays(14);
    public static readonly TimeSpan PostReviewGrace = TimeSpan.FromDays(1);

    private static readonly string[] OpenForActions =
        [RiskStatuses.Identified, RiskStatuses.Assessed, RiskStatuses.InTreatment];

    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<TenantOptions> _tenants;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GovernanceMonitorService> _logger;

    public GovernanceMonitorService(
        IServiceScopeFactory scopes,
        IOptionsMonitor<TenantOptions> tenants,
        IConfiguration configuration,
        ILogger<GovernanceMonitorService> logger)
    {
        _scopes = scopes;
        _tenants = tenants;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = _configuration.GetValue<bool?>("Governance:MonitorEnabled")
            ?? _configuration.GetValue("Risks:MonitorEnabled", true);
        if (!enabled)
            return;

        var seconds = _configuration.GetValue<int?>("Governance:CheckIntervalSeconds")
            ?? _configuration.GetValue("Risks:CheckIntervalSeconds", 3600);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(60, seconds)));

        do
        {
            await CheckAllAsync(DateTime.UtcNow, stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // Alle tillatte tenanter; én tenant som feiler stopper ikke de andre
    public async Task CheckAllAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        foreach (var tenant in _tenants.CurrentValue.Allowed)
        {
            try
            {
                await CheckTenantAsync(tenant.TenantId, nowUtc, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Sjekk av endringer og risiko feilet for tenant {TenantId}", tenant.TenantId);
            }
        }
    }

    // Gir antall nye hendelser i tenanten
    public async Task<int> CheckTenantAsync(string tenantId, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        using var scope = _scopes.CreateTenantScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var register = scope.ServiceProvider.GetRequiredService<RiskRegisterService>();
        var notifier = scope.ServiceProvider.GetRequiredService<GovernanceNotifier>();

        // Varsler sendes etter at hendelsene er lagret
        var notifications = new List<Func<Task>>();
        var count = 0;

        // ---------- Risiko ----------

        var risks = await db.Risks
            .Include(r => r.Acceptances)
            .Where(r => r.Status != RiskStatuses.Closed)
            .ToListAsync(cancellationToken);

        foreach (var risk in risks)
        {
            if (risk.DueDate is { } due && due < nowUtc && OpenForActions.Contains(risk.Status) &&
                await LogOnceAsync(db, nameof(Risk), risk.Id, risk.Number, AuditActions.RiskActionOverdue, Key(due), cancellationToken))
            {
                count++;
                notifications.Add(() => notifier.RiskOverdueAsync(risk, cancellationToken));
            }

            if (risk.NextReviewAt is { } review && review < nowUtc &&
                await LogOnceAsync(db, nameof(Risk), risk.Id, risk.Number, AuditActions.RiskReviewDue, Key(review), cancellationToken))
            {
                count++;
            }

            var untreated =
                (RiskLevels.For(risk.Score) is RiskLevels.High or RiskLevels.Critical && risk.OwnerEmployeeId == null) ||
                (risk.Status == RiskStatuses.Identified && risk.CreatedAt < nowUtc - UntreatedAfter);
            if (untreated &&
                await LogOnceAsync(db, nameof(Risk), risk.Id, risk.Number, AuditActions.RiskUntreatedNotified, "untreated", cancellationToken))
            {
                count++;
                notifications.Add(() => notifier.RiskUntreatedAsync(risk, cancellationToken));
            }

            var acceptance = RiskRegisterService.AcceptanceState(risk, nowUtc);
            if (risk.Status == RiskStatuses.Accepted && acceptance.Active &&
                acceptance.Latest?.ValidUntil is { } until && until - nowUtc <= AcceptanceWarning &&
                await LogOnceAsync(db, nameof(Risk), risk.Id, risk.Number, AuditActions.RiskAcceptanceExpiring, Key(until), cancellationToken))
            {
                count++;
                notifications.Add(() => notifier.AcceptanceExpiringAsync(risk, until, cancellationToken));
            }

            if (risk.Status == RiskStatuses.Accepted && !acceptance.Active)
            {
                await register.VoidAcceptanceIfNeededAsync(
                    risk, true, "Aksepten er utløpt.", AuditActions.RiskAcceptanceExpired, cancellationToken, nowUtc);
                count++;
                notifications.Add(() => notifier.AcceptanceExpiredAsync(risk, cancellationToken));
            }
        }

        // ---------- Endringer ----------

        var reminderDays = await db.GovernanceSettings
            .Select(s => (int?)s.ReminderDaysBefore)
            .FirstOrDefaultAsync(cancellationToken) ?? 3;
        var horizon = nowUtc.AddDays(reminderDays);

        var upcoming = await db.ChangeRequests
            .Where(c => c.Environment == ChangeEnvironments.Production &&
                (c.Status == ChangeStatuses.Approved || c.Status == ChangeStatuses.Scheduled) &&
                c.PlannedStart != null && c.PlannedStart >= nowUtc && c.PlannedStart <= horizon)
            .ToListAsync(cancellationToken);

        foreach (var change in upcoming)
        {
            if (await LogOnceAsync(db, nameof(ChangeRequest), change.Id, change.Number,
                    AuditActions.ChangeUpcomingNotified, Key(change.PlannedStart!.Value, withTime: true), cancellationToken))
            {
                count++;
                notifications.Add(() => notifier.UpcomingAsync(change, cancellationToken));
            }
        }

        var reviewBefore = nowUtc - PostReviewGrace;
        var missingReview = await db.ChangeRequests
            .Where(c => c.PostReviewRequired && c.PostReviewedAt == null &&
                c.Status == ChangeStatuses.Completed && c.UpdatedAt < reviewBefore)
            .ToListAsync(cancellationToken);

        foreach (var change in missingReview)
        {
            if (await LogOnceAsync(db, nameof(ChangeRequest), change.Id, change.Number,
                    AuditActions.ChangePostReviewReminder, "post_review", cancellationToken))
            {
                count++;
                notifications.Add(() => notifier.PostReviewMissingAsync(change, cancellationToken));
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        foreach (var notify in notifications)
            await notify();

        if (count > 0)
            _logger.LogInformation("Endringer og risiko: {Count} nye hendelser i tenant {TenantId}", count, tenantId);

        return count;
    }

    private static string Key(DateTime value, bool withTime = false) =>
        value.ToString(withTime ? "yyyy-MM-ddTHH:mm" : "yyyy-MM-dd");

    // Én hendelse per rad, type og nøkkel (dato eller fast tekst)
    private static async Task<bool> LogOnceAsync(
        AppDbContext db, string entityType, int id, string number, string action, string key, CancellationToken ct)
    {
        var entityId = id.ToString();

        var earlier = await db.AuditEvents
            .Where(e => e.EntityType == entityType && e.EntityId == entityId && e.Action == action)
            .Select(e => e.Changes)
            .ToListAsync(ct);

        if (earlier.Any(c => c != null && c.Contains($"\"{key}\"")))
            return false;

        db.AddAuditEvent(entityType, entityId, action, new { number, key });
        return true;
    }
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;

namespace backend.Services;

// Beregner nøkkeltallene til driftsrapporten (FASE5_DRIFTSRAPPORT.md kap. 2) for
// tenanten i scopet og lagrer rapporten. Koden regner; AI-delen kommer senere,
// så rapportene lagres foreløpig med AiStatus «disabled».
//
// Kjører i API-et (innlogget tenant) eller i et scope fra CreateTenantScope
// (bakgrunnsjobb). Tenant-filteret i AppDbContext sørger for at bare tenantens
// data telles.
public class OpsReportService
{
    public const string SystemActor = "system";

    public static readonly TimeSpan OldTicketAge = TimeSpan.FromDays(14);
    public static readonly TimeSpan LifecycleLookahead = TimeSpan.FromDays(14);
    public static readonly TimeSpan GuestInactivity = TimeSpan.FromDays(90);

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ICurrentUser _currentUser;
    private readonly GraphService _graph;
    private readonly AlertSourceStatusRegistry _alertStatus;
    private readonly ILogger<OpsReportService> _logger;
    private readonly AiService _ai;
    private readonly TeamsNotificationService _teams;

    public OpsReportService(
        AppDbContext db,
        ITenantContext tenant,
        ICurrentUser currentUser,
        GraphService graph,
        AlertSourceStatusRegistry alertStatus,
        ILogger<OpsReportService> logger,
        AiService ai,
        TeamsNotificationService teams)
    {
        _ai = ai;
        _teams = teams;
        _db = db;
        _tenant = tenant;
        _currentUser = currentUser;
        _graph = graph;
        _alertStatus = alertStatus;
        _logger = logger;
    }

    // Forrige hele uke før nowUtc: mandag 00:00 til neste mandag 00:00 norsk tid, i UTC
    public static (DateTime Start, DateTime End) PreviousWeek(DateTime nowUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), SlaCalculator.Norway);

        var daysSinceMonday = ((int)local.DayOfWeek + 6) % 7;
        var thisMonday = local.Date.AddDays(-daysSinceMonday);
        var lastMonday = thisMonday.AddDays(-7);

        return (ToUtc(lastMonday), ToUtc(thisMonday));
    }

    private static DateTime ToUtc(DateTime localMidnight) =>
        TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(localMidnight, DateTimeKind.Unspecified), SlaCalculator.Norway);

    // Lager rapporten for perioden hvis den mangler. Finnes den fra før, gis den
    // eksisterende tilbake uendret (Created = false).
    public async Task<(OpsReport Report, bool Created)> GenerateAsync(
        DateTime periodStart,
        DateTime periodEnd,
        DateTime nowUtc,
        string? createdBy = null,
        CancellationToken cancellationToken = default)
    {
        var existing = await FindAsync(periodStart, cancellationToken);
        if (existing != null)
            return (existing, false);

        var metrics = await ComputeMetricsAsync(periodStart, periodEnd, nowUtc, cancellationToken);

        var report = new OpsReport
        {
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            Metrics = JsonSerializer.Serialize(metrics, JsonOptions),
            AiStatus = OpsReportAiStatuses.Disabled,
            CreatedAt = nowUtc,
            CreatedBy = createdBy ?? _currentUser.Name ?? SystemActor,
        };

        await WriteAiPartAsync(report, metrics, cancellationToken);

        _db.OpsReports.Add(report);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // En annen instans rakk å lage rapporten (unik indeks på tenant og periode)
            _db.Entry(report).State = EntityState.Detached;
            existing = await FindAsync(periodStart, cancellationToken);
            if (existing == null)
                throw;

            return (existing, false);
        }

        _logger.LogInformation(
            "Driftsrapport for {Start:yyyy-MM-dd} laget i tenant {TenantId}", periodStart, _tenant.TenantId);

        // Teams-kort bare for en ny rapport (køen fanger feil i Teams)
        await _teams.NotifyOpsReportAsync(report, metrics, cancellationToken);

        return (report, true);
    }

    // AI-delen (FASE5_DRIFTSRAPPORT.md kap. 4): bare de aggregerte nøkkeltallene sendes.
    // Feiler AI, lagres rapporten likevel med AiStatus «failed».
    private async Task WriteAiPartAsync(OpsReport report, OpsReportMetrics metrics, CancellationToken ct)
    {
        if (!_ai.Enabled)
        {
            report.AiStatus = OpsReportAiStatuses.Disabled;
            return;
        }

        try
        {
            var result = await _ai.CompleteAsync("ops-report", metrics, userKey: null, ct);

            var summary = result.TryGetProperty("oppsummering", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString() : null;
            var actions = result.TryGetProperty("tiltak", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Take(3).ToList()
                : [];

            if (string.IsNullOrWhiteSpace(summary))
                throw new AiUnavailableException("AI-svaret manglet oppsummering.");

            report.AiSummary = summary.Length > 4000 ? summary[..4000] : summary;
            report.AiActions = JsonSerializer.Serialize(actions, JsonOptions);
            report.AiStatus = OpsReportAiStatuses.Ok;
        }
        catch (AiUnavailableException e)
        {
            _logger.LogWarning(e, "AI-delen av driftsrapporten feilet");
            report.AiStatus = OpsReportAiStatuses.Failed;
        }
    }

    private Task<OpsReport?> FindAsync(DateTime periodStart, CancellationToken cancellationToken) =>
        _db.OpsReports.SingleOrDefaultAsync(r => r.PeriodStart == periodStart, cancellationToken);

    public static OpsReportMetrics? ReadMetrics(OpsReport report) =>
        JsonSerializer.Deserialize<OpsReportMetrics>(report.Metrics, JsonOptions);

    public async Task<OpsReportMetrics> ComputeMetricsAsync(
        DateTime periodStart,
        DateTime periodEnd,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        return new OpsReportMetrics(
            nowUtc,
            await TicketMetricsAsync(periodStart, periodEnd, nowUtc, cancellationToken),
            await SlaMetricsAsync(periodStart, periodEnd, nowUtc, cancellationToken),
            await AlertMetricsAsync(periodStart, periodEnd, cancellationToken),
            await AccessMetricsAsync(nowUtc, cancellationToken),
            await LifecycleMetricsAsync(nowUtc, cancellationToken),
            await M365MetricsAsync(nowUtc, cancellationToken));
    }

    private async Task<OpsTicketMetrics> TicketMetricsAsync(
        DateTime start, DateTime end, DateTime now, CancellationToken ct)
    {
        var created = await _db.Tickets
            .CountAsync(t => t.CreatedAt >= start && t.CreatedAt < end, ct);

        var resolved = await _db.Tickets
            .CountAsync(t => t.ResolvedAt >= start && t.ResolvedAt < end, ct);

        var open = await _db.Tickets
            .Where(t => t.Status != TicketStatuses.Resolved)
            .Select(t => new { t.Priority, t.CreatedAt })
            .ToListAsync(ct);

        var byPriority = TicketPriorities.All.ToDictionary(
            p => p, p => open.Count(t => t.Priority == p));

        var oldCutoff = now - OldTicketAge;

        return new OpsTicketMetrics(
            created, resolved, byPriority, open.Count(t => t.CreatedAt < oldCutoff));
    }

    // Brudd regnes fra fristene (som SlaEvaluator), ikke fra *BreachedAt: SLA-jobben
    // markerer ikke et responsbrudd på en sak som fikk svar for sent mellom to kjøringer.
    private async Task<OpsSlaMetrics> SlaMetricsAsync(
        DateTime start, DateTime end, DateTime now, CancellationToken ct)
    {
        var responseBreaches = await _db.Tickets
            .Where(t => t.ResponseDueAt >= start && t.ResponseDueAt < end)
            .CountAsync(t => (t.FirstRespondedAt ?? now) > t.ResponseDueAt, ct);

        var resolutionBreaches = await _db.Tickets
            .Where(t => t.ResolutionDueAt >= start && t.ResolutionDueAt < end)
            .CountAsync(t => (t.ResolvedAt ?? now) > t.ResolutionDueAt, ct);

        var open = await _db.Tickets
            .Where(t => t.Status != TicketStatuses.Resolved)
            .Where(t => t.ResponseDueAt != null || t.ResolutionDueAt != null)
            .ToListAsync(ct);

        var atRisk = open.Count(t => SlaEvaluator.Evaluate(t, now) == SlaStatuses.AtRisk);

        return new OpsSlaMetrics(responseBreaches, resolutionBreaches, atRisk);
    }

    private async Task<OpsAlertMetrics> AlertMetricsAsync(
        DateTime start, DateTime end, CancellationToken ct)
    {
        var created = await _db.Alerts
            .Where(a => a.FirstSeenAt >= start && a.FirstSeenAt < end)
            .GroupBy(a => new { a.Source, a.Severity })
            .Select(g => new OpsAlertCount(g.Key.Source, g.Key.Severity, g.Count()))
            .ToListAsync(ct);

        var recurred = await _db.Alerts
            .Where(a => a.FirstSeenAt < start && a.LastFiredAt >= start && a.LastFiredAt < end)
            .GroupBy(a => new { a.Source, a.Severity })
            .Select(g => new OpsAlertCount(g.Key.Source, g.Key.Severity, g.Count()))
            .ToListAsync(ct);

        // En kilde som ikke er hentet ennå (LastRunAt null) regnes ikke som feil
        var failing = _tenant.TenantId is { } tenantId
            ? _alertStatus.For(tenantId)
                .Where(s => !s.Ok && s.LastRunAt != null)
                .Select(s => s.Source)
                .ToList()
            : [];

        return new OpsAlertMetrics(Sorted(created), Sorted(recurred), failing);
    }

    private static List<OpsAlertCount> Sorted(IEnumerable<OpsAlertCount> counts) =>
        counts
            .OrderBy(c => c.Source)
            .ThenBy(c => AlertSeverities.Rank(c.Source, c.Severity))
            .ThenBy(c => c.Severity)
            .ToList();

    private async Task<OpsAccessMetrics> AccessMetricsAsync(DateTime now, CancellationToken ct)
    {
        var pending = await _db.AccessRequests
            .Where(r => r.Status == AccessRequestStatuses.Pending)
            .Select(r => r.CreatedAt)
            .ToListAsync(ct);

        int? oldestDays = pending.Count == 0
            ? null
            : Math.Max(0, (int)(now - pending.Min()).TotalDays);

        return new OpsAccessMetrics(pending.Count, oldestDays);
    }

    private async Task<OpsLifecycleMetrics> LifecycleMetricsAsync(DateTime now, CancellationToken ct)
    {
        // Datoene er kalenderdatoer; fra og med i dag (norsk tid) og 14 dager frem
        var today = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(now, DateTimeKind.Utc), SlaCalculator.Norway).Date;
        var from = DateTime.SpecifyKind(today, DateTimeKind.Utc);
        var to = from + LifecycleLookahead;

        var upcoming = await _db.LifecycleProcesses
            .Where(p => p.Status == LifecycleStatuses.Active)
            .Where(p => p.ReferenceDate >= from && p.ReferenceDate <= to)
            .GroupBy(p => p.Type)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return new OpsLifecycleMetrics(
            upcoming.FirstOrDefault(u => u.Type == LifecycleTypes.Onboarding)?.Count ?? 0,
            upcoming.FirstOrDefault(u => u.Type == LifecycleTypes.Offboarding)?.Count ?? 0);
    }

    // Samme regel som M365-siden (M365InactiveUsers). Feil i Graph stopper ikke rapporten.
    private async Task<OpsM365Metrics> M365MetricsAsync(DateTime now, CancellationToken ct)
    {
        if (_tenant.TenantId is not { } tenantId)
            return new OpsM365Metrics(null, "Ingen tenant.");

        try
        {
            var overview = await _graph.GetUserOverviewAsync(tenantId, ct);
            var cutoff = new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc) - GuestInactivity);
            var inactive = M365InactiveUsers.From(overview, "Guest", cutoff, includeNeverSignedIn: true);

            return inactive.SignInUnavailableReason != null
                ? new OpsM365Metrics(null, inactive.SignInUnavailableReason)
                : new OpsM365Metrics(inactive.Users.Count, null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Microsoft 365-tall for driftsrapporten feilet i tenant {TenantId}", tenantId);
            return new OpsM365Metrics(null, "Microsoft 365-data var ikke tilgjengelige.");
        }
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using backend.Data;
using backend.Models;

namespace backend.Services;

// Følger med på risikoregisteret i alle tillatte tenanter (ENDRING_OG_RISIKO.md, C3).
// Logger én gang per hendelse (aktør «system»): tiltak forbi frist, vurdering forfalt,
// og aksept utløpt (risikoen går da tilbake til «vurdert»). Teams-varsler kommer i C5.
//
// Konfigurasjon: Risks:MonitorEnabled (standard true) og Risks:CheckIntervalSeconds (3600).
public class RiskMonitorService : BackgroundService
{
    private static readonly string[] OpenForActions =
        [RiskStatuses.Identified, RiskStatuses.Assessed, RiskStatuses.InTreatment];

    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<TenantOptions> _tenants;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RiskMonitorService> _logger;

    public RiskMonitorService(
        IServiceScopeFactory scopes,
        IOptionsMonitor<TenantOptions> tenants,
        IConfiguration configuration,
        ILogger<RiskMonitorService> logger)
    {
        _scopes = scopes;
        _tenants = tenants;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("Risks:MonitorEnabled", true))
            return;

        var interval = TimeSpan.FromSeconds(Math.Max(60, _configuration.GetValue("Risks:CheckIntervalSeconds", 3600)));
        using var timer = new PeriodicTimer(interval);

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
                _logger.LogError(ex, "Risikosjekk feilet for tenant {TenantId}", tenant.TenantId);
            }
        }
    }

    // Gir antall nye hendelser i tenanten
    public async Task<int> CheckTenantAsync(string tenantId, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        using var scope = _scopes.CreateTenantScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var register = scope.ServiceProvider.GetRequiredService<RiskRegisterService>();

        var risks = await db.Risks
            .Include(r => r.Acceptances)
            .Where(r => r.Status != RiskStatuses.Closed)
            .ToListAsync(cancellationToken);

        var count = 0;

        foreach (var risk in risks)
        {
            if (risk.DueDate is { } due && due < nowUtc && OpenForActions.Contains(risk.Status) &&
                await LogOnceAsync(db, risk, AuditActions.RiskActionOverdue, due, cancellationToken))
            {
                count++;
            }

            if (risk.NextReviewAt is { } review && review < nowUtc &&
                await LogOnceAsync(db, risk, AuditActions.RiskReviewDue, review, cancellationToken))
            {
                count++;
            }

            if (risk.Status == RiskStatuses.Accepted && !RiskRegisterService.AcceptanceState(risk, nowUtc).Active)
            {
                await register.VoidAcceptanceIfNeededAsync(
                    risk, true, "Aksepten er utløpt.", AuditActions.RiskAcceptanceExpired, cancellationToken, nowUtc);
                count++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        if (count > 0)
            _logger.LogInformation("Risikosjekk: {Count} nye hendelser i tenant {TenantId}", count, tenantId);

        return count;
    }

    // Én hendelse per risiko, type og dato (fristen eller vurderingsdatoen)
    private static async Task<bool> LogOnceAsync(
        AppDbContext db, Risk risk, string action, DateTime date, CancellationToken ct)
    {
        var key = date.ToString("yyyy-MM-dd");
        var entityId = risk.Id.ToString();

        var earlier = await db.AuditEvents
            .Where(e => e.EntityType == nameof(Risk) && e.EntityId == entityId && e.Action == action)
            .Select(e => e.Changes)
            .ToListAsync(ct);

        if (earlier.Any(c => c != null && c.Contains(key)))
            return false;

        db.AddAuditEvent(nameof(Risk), entityId, action, new { risk.Number, date = key });
        return true;
    }
}

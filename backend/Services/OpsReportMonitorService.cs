using Microsoft.Extensions.Options;

namespace backend.Services;

// Lager ukentlig driftsrapport (FASE5_DRIFTSRAPPORT.md, steg 4) i alle tillatte
// tenanter: mandag etter OpsReport:Hour (norsk tid), eller senere i uken hvis den
// mangler. Aldri to rapporter for samme uke (sjekk før lagring og unik indeks).
// Aktør «system». Én tenant som feiler stopper ikke de andre.
//
// Konfigurasjon: OpsReport:Enabled (standard false) og OpsReport:Hour (7).
public class OpsReportMonitorService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<TenantOptions> _tenants;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OpsReportMonitorService> _logger;

    public OpsReportMonitorService(
        IServiceScopeFactory scopes,
        IOptionsMonitor<TenantOptions> tenants,
        IConfiguration configuration,
        ILogger<OpsReportMonitorService> logger)
    {
        _scopes = scopes;
        _tenants = tenants;
        _configuration = configuration;
        _logger = logger;
    }

    public bool Enabled => _configuration.GetValue("OpsReport:Enabled", false);

    private int Hour => Math.Clamp(_configuration.GetValue("OpsReport:Hour", 7), 0, 23);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
            return;

        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));

        // OpsReport:RunOnStart = false (testene) venter til første time har gått
        if (_configuration.GetValue("OpsReport:RunOnStart", true))
            await RunAsync(DateTime.UtcNow, stoppingToken);

        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RunAsync(DateTime.UtcNow, stoppingToken);
    }

    // Er det tid for forrige ukes rapport? Ikke før mandag kl. Hour norsk tid.
    public bool IsDue(DateTime nowUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), SlaCalculator.Norway);
        return local.DayOfWeek != DayOfWeek.Monday || local.Hour >= Hour;
    }

    // Gir antall nye rapporter
    public async Task<int> RunAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        if (!Enabled || !IsDue(nowUtc))
            return 0;

        var (start, end) = OpsReportService.PreviousWeek(nowUtc);
        var created = 0;

        foreach (var tenant in _tenants.CurrentValue.Allowed)
        {
            try
            {
                using var scope = _scopes.CreateTenantScope(tenant.TenantId);
                var reports = scope.ServiceProvider.GetRequiredService<OpsReportService>();
                var (_, isNew) = await reports.GenerateAsync(start, end, nowUtc, OpsReportService.SystemActor, cancellationToken);
                if (isNew)
                    created++;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Driftsrapport feilet for tenant {TenantId}", tenant.TenantId);
            }
        }

        return created;
    }
}

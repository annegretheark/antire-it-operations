using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using backend.Data;

namespace backend.Services;

// Anonymiserer automatisk ansatte som har sluttet, når fristen i GDPR-innstillingene
// er passert (én gang i døgnet). Ansatte som fortsatt har utstyr eller en aktiv
// prosess, hoppes over til det er ordnet. Aktør i revisjonsloggen: system.
//
// Konfigurasjon: Gdpr:MonitorEnabled (standard true) og Gdpr:CheckIntervalHours (24).
public class GdprMonitorService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<TenantOptions> _tenants;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GdprMonitorService> _logger;

    public GdprMonitorService(
        IServiceScopeFactory scopes,
        IOptionsMonitor<TenantOptions> tenants,
        IConfiguration configuration,
        ILogger<GdprMonitorService> logger)
    {
        _scopes = scopes;
        _tenants = tenants;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("Gdpr:MonitorEnabled", true))
            return;

        var interval = TimeSpan.FromHours(Math.Max(1, _configuration.GetValue("Gdpr:CheckIntervalHours", 24)));
        using var timer = new PeriodicTimer(interval);

        do
        {
            foreach (var tenant in _tenants.CurrentValue.Allowed)
            {
                try
                {
                    await RunTenantAsync(tenant.TenantId, DateTime.UtcNow, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "GDPR-anonymisering feilet for tenant {TenantId}", tenant.TenantId);
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // Gir antall ansatte som ble anonymisert
    public async Task<int> RunTenantAsync(string tenantId, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        using var scope = _scopes.CreateTenantScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var gdpr = scope.ServiceProvider.GetRequiredService<GdprService>();

        var months = (await db.GdprSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken))?.AnonymizeAfterMonths;

        if (months is not int after)
            return 0;

        var cutoff = nowUtc.Date.AddMonths(-after);

        var due = await db.Employees
            .AsNoTracking()
            .Where(e => e.Status == "Sluttet" && e.AnonymizedAt == null && e.EndDate != null && e.EndDate <= cutoff)
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);

        var count = 0;

        foreach (var id in due)
        {
            var (result, error) = await gdpr.AnonymizeAsync(id, cancellationToken);

            if (result != null)
                count++;
            else
                _logger.LogInformation("Ansatt {Id} anonymiseres ikke ennå: {Reason}", id, error);
        }

        return count;
    }
}

using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using backend.Models;

namespace backend.Services;

public record AlertSourceStatus(
    string Source,
    DateTimeOffset? LastRunAt,
    bool Ok,
    string Message,
    int Received
);

// Siste henting per tenant og kilde, til visning på Varsler-siden (bare i minnet)
public class AlertSourceStatusRegistry
{
    private readonly ConcurrentDictionary<(string Tenant, string Source), AlertSourceStatus> _statuses = new();

    public void Set(string tenantId, AlertSourceStatus status) =>
        _statuses[(tenantId.ToLowerInvariant(), status.Source)] = status;

    public IReadOnlyList<AlertSourceStatus> For(string tenantId) =>
        AlertSources.All
            .Select(source => _statuses.GetValueOrDefault((tenantId.ToLowerInvariant(), source))
                ?? new AlertSourceStatus(source, null, false, source == AlertSources.AzureMonitor
                    ? "Henting fra Azure Monitor kommer i steg J."
                    : "Ikke hentet ennå.", 0))
            .ToList();
}

// Henter varsler for hver tillatt tenant hvert 5. minutt og sender dem gjennom
// AlertIngestionService, og prøver Teams-meldinger som ikke er sendt på nytt.
// Kjører uten innlogget bruker: tenanten settes i et eget scope, og alt logges
// med aktør «system».
//
// Konfigurasjon: Alerts:MonitorEnabled (standard true) og Alerts:CheckIntervalSeconds (300).
public class AlertMonitorService : BackgroundService
{
    // Første henting etter oppstart ser så langt tilbake
    private static readonly TimeSpan InitialLookback = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<TenantOptions> _tenants;
    private readonly GraphService _graph;
    private readonly AlertSourceStatusRegistry _status;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AlertMonitorService> _logger;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _defenderSince = new();

    public AlertMonitorService(
        IServiceScopeFactory scopes,
        IOptionsMonitor<TenantOptions> tenants,
        GraphService graph,
        AlertSourceStatusRegistry status,
        IConfiguration configuration,
        ILogger<AlertMonitorService> logger)
    {
        _scopes = scopes;
        _tenants = tenants;
        _graph = graph;
        _status = status;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("Alerts:MonitorEnabled", true))
            return;

        var interval = TimeSpan.FromSeconds(
            Math.Max(30, _configuration.GetValue("Alerts:CheckIntervalSeconds", 300)));

        using var timer = new PeriodicTimer(interval);

        do
        {
            foreach (var tenant in _tenants.CurrentValue.Allowed)
            {
                try
                {
                    await RunTenantAsync(tenant.TenantId, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    // Én tenant som feiler skal ikke stoppe de andre eller appen
                    _logger.LogError(ex, "Varselhenting feilet for tenant {TenantId}", tenant.TenantId);
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // Én runde for én tenant: Defender-varsler og Teams-køen
    public async Task RunTenantAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        await PollDefenderAsync(tenantId, cancellationToken);

        using var scope = _scopes.CreateTenantScope(tenantId);
        await scope.ServiceProvider.GetRequiredService<TeamsNotificationService>()
            .DeliverPendingAsync(cancellationToken);
    }

    private async Task PollDefenderAsync(string tenantId, CancellationToken cancellationToken)
    {
        var key = tenantId.ToLowerInvariant();
        var startedAt = DateTimeOffset.UtcNow;
        var since = _defenderSince.GetValueOrDefault(key, startedAt - InitialLookback);

        List<AlertSignal> signals;

        try
        {
            signals = await _graph.GetDefenderAlertsAsync(tenantId, since, cancellationToken);
        }
        catch (GraphUnavailableException e)
        {
            // Mangler rettighet eller lisens: vises på Varsler-siden, ikke en feil i appen
            _status.Set(tenantId, new AlertSourceStatus(AlertSources.Defender, startedAt, false, e.Message, 0));
            _logger.LogInformation("Defender-varsler er ikke tilgjengelige i {TenantId}: {Message}", tenantId, e.Message);
            return;
        }

        using var scope = _scopes.CreateTenantScope(tenantId);
        var ingestion = scope.ServiceProvider.GetRequiredService<AlertIngestionService>();

        foreach (var signal in signals)
            await ingestion.IngestAsync(signal, cancellationToken);

        _defenderSince[key] = startedAt;
        _status.Set(tenantId, new AlertSourceStatus(
            AlertSources.Defender, startedAt, true, $"{signals.Count} varsler hentet.", signals.Count));
    }
}

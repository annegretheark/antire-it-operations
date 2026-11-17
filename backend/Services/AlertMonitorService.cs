using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using backend.Data;
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
                    ? "Ikke hentet ennå."
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
    private readonly AzureService _azure;
    private readonly AlertSourceStatusRegistry _status;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AlertMonitorService> _logger;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _defenderSince = new();

    public AlertMonitorService(
        IServiceScopeFactory scopes,
        IOptionsMonitor<TenantOptions> tenants,
        GraphService graph,
        AzureService azure,
        AlertSourceStatusRegistry status,
        IConfiguration configuration,
        ILogger<AlertMonitorService> logger)
    {
        _scopes = scopes;
        _tenants = tenants;
        _graph = graph;
        _azure = azure;
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

    // Én runde for én tenant: Defender, Azure Monitor og Teams-køen
    public async Task RunTenantAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        await PollDefenderAsync(tenantId, cancellationToken);
        await PollAzureMonitorAsync(tenantId, cancellationToken);

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

    // Azure Monitor (4.2): varsler i abonnementene som er synket og synlige
    private async Task PollAzureMonitorAsync(string tenantId, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;

        using var scope = _scopes.CreateTenantScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var subscriptionIds = await db.AzureSubscriptions
            .AsNoTracking()
            .Where(s => s.IsVisible)
            .Select(s => s.SubscriptionId)
            .ToListAsync(cancellationToken);

        if (subscriptionIds.Count == 0)
        {
            _status.Set(tenantId, new AlertSourceStatus(AlertSources.AzureMonitor, startedAt, false,
                "Ingen Azure-abonnementer er synket (Azure → Abonnementer → Synk).", 0));
            return;
        }

        List<AlertSignal> signals;

        try
        {
            signals = await _azure.ListMonitorAlertsAsync(tenantId, subscriptionIds, cancellationToken);
        }
        catch (AzureUnavailableException e)
        {
            _status.Set(tenantId, new AlertSourceStatus(AlertSources.AzureMonitor, startedAt, false, e.Message, 0));
            _logger.LogInformation("Azure Monitor-varsler er ikke tilgjengelige i {TenantId}: {Message}", tenantId, e.Message);
            return;
        }

        var ingestion = scope.ServiceProvider.GetRequiredService<AlertIngestionService>();

        // Samme regel og ressurs kan ha flere forekomster de siste 7 dagene (en løst
        // og en ny). Bare den siste gjelder, ellers ville varselet veksle mellom
        // løst og fyrt ved hver henting.
        var latest = signals
            .GroupBy(s => s.ExternalId)
            .Select(g => g.OrderByDescending(s => s.FiredAt).First());

        foreach (var signal in latest)
            await ingestion.IngestAsync(signal, cancellationToken);

        _status.Set(tenantId, new AlertSourceStatus(AlertSources.AzureMonitor, startedAt, true,
            $"{signals.Count} varsler siste 7 dager i {subscriptionIds.Count} abonnementer.", signals.Count));
    }
}

using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using backend.Services;

namespace backend.Tests;

// AzureService uten Azure, for tester. Abonnementer og ressurser settes per tenant.
public class FakeAzureService(IHttpClientFactory httpClients, IConfiguration configuration)
    : AzureService(httpClients, configuration)
{
    public ConcurrentDictionary<string, List<AzureSubscriptionInfo>> Subscriptions { get; } = new();

    public List<AzureResource> Resources { get; } = [];

    // Settes for å la neste kall til Azure feile
    public Exception? Failure { get; set; }

    // Abonnementene siste ressursspørring gjaldt, og antall spørringer
    public IReadOnlyCollection<string> LastQueriedSubscriptions { get; private set; } = [];
    public int ResourceQueries;

    // Azure Monitor-varsler per tenant (ListMonitorAlertsAsync)
    public ConcurrentDictionary<string, List<AlertSignal>> MonitorAlerts { get; } = new();

    // Settes for å la bare varselhentingen feile
    public Exception? MonitorFailure { get; set; }

    public override Task<List<AlertSignal>> ListMonitorAlertsAsync(
        string tenantId, IReadOnlyCollection<string> subscriptionIds,
        CancellationToken cancellationToken = default)
    {
        if (MonitorFailure is { } failure)
            throw failure;

        return Task.FromResult(MonitorAlerts.GetValueOrDefault(tenantId)?.ToList() ?? []);
    }

    // Kostnader per (abonnement, periodestart): tjenestetype → beløp (steg 5)
    public ConcurrentDictionary<(string SubscriptionId, DateTime From), Dictionary<string, decimal>> Costs { get; } = new();

    // Abonnementer appen ikke har tilgang til i Cost Management
    public HashSet<string> CostDenied { get; } = [];

    // Settes for å la hele kostnadshentingen feile
    public Exception? CostFailure { get; set; }

    public override Task<AzureCostResult> GetCostsAsync(
        string tenantId, IReadOnlyCollection<string> subscriptionIds, DateTime fromUtc, DateTime toUtc,
        CancellationToken cancellationToken = default)
    {
        if (CostFailure is { } failure)
            throw failure;

        var byService = new Dictionary<string, decimal>();
        int counted = 0, skipped = 0;

        foreach (var id in subscriptionIds)
        {
            if (CostDenied.Contains(id))
            {
                skipped++;
                continue;
            }

            counted++;
            foreach (var (service, amount) in Costs.GetValueOrDefault((id, fromUtc)) ?? [])
                byService[service] = byService.GetValueOrDefault(service) + amount;
        }

        if (counted == 0 && skipped > 0)
            throw new AzureUnavailableException("Appen har ikke tilgang i Azure.", 403);

        return Task.FromResult(new AzureCostResult("NOK", byService, counted, skipped));
    }

    public override Task<List<AzureSubscriptionInfo>> ListSubscriptionsAsync(
        string tenantId, CancellationToken cancellationToken = default)
    {
        if (Failure is { } failure)
            throw failure;

        return Task.FromResult(Subscriptions.GetValueOrDefault(tenantId)?.ToList() ?? []);
    }

    public override Task<List<AzureResource>> QueryResourcesAsync(
        string tenantId, IReadOnlyCollection<string> subscriptionIds,
        CancellationToken cancellationToken = default)
    {
        if (Failure is { } failure)
            throw failure;

        Interlocked.Increment(ref ResourceQueries);
        LastQueriedSubscriptions = subscriptionIds.ToList();

        return Task.FromResult(Resources
            .Where(r => subscriptionIds.Contains(r.SubscriptionId, StringComparer.OrdinalIgnoreCase))
            .ToList());
    }
}

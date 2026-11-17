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

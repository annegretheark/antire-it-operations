namespace backend.Models;

// Et Azure-abonnement appen kan se i tenanten (Reader-rolle, se fase 3).
// Synkes fra Azure Resource Manager; slettes ikke når appen mister tilgangen,
// men får IsVisible = false.
public class AzureSubscription : ITenantOwned, IAudited
{
    public int Id { get; set; }

    // Entra-tenanten raden hører til (settes automatisk)
    public string TenantId { get; set; } = string.Empty;

    // Abonnements-ID (GUID), unik i tenanten
    public string SubscriptionId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    // Enabled | Warned | PastDue | Disabled | Deleted (fra Azure)
    public string State { get; set; } = string.Empty;

    // Om appen så abonnementet ved siste synk
    public bool IsVisible { get; set; } = true;

    public DateTime FirstSeenAt { get; set; }

    public DateTime LastSeenAt { get; set; }
}

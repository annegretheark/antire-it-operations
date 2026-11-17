namespace backend.Models;

public class TicketCategoryGroup : ITenantOwned, IAudited
{
    public int Id { get; set; }

    // Entra-tenanten raden hører til (settes automatisk)
    public string TenantId { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string EntraGroupId { get; set; } = string.Empty;
}

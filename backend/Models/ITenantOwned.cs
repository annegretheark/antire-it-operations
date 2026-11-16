namespace backend.Models;

// Data som hører til én Entra-tenant. TenantId settes automatisk ved lagring
// (AppDbContext) og filtreres automatisk ved spørringer.
public interface ITenantOwned
{
    string TenantId { get; set; }
}

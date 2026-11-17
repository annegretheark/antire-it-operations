namespace backend.Models;

public class AccessRequest : ITenantOwned, IAudited
{
    public int Id { get; set; }

    // Entra-tenanten raden hører til (settes automatisk)
    public string TenantId { get; set; } = string.Empty;

    // Entra objekt-ID til den som opprettet bestillingen (null for eldre bestillinger)
    public string? RequestedByObjectId { get; set; }

    public string EmployeeName { get; set; } = string.Empty;

    public string SystemName { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public string ManagerName { get; set; } = string.Empty;

    public string Status { get; set; } = "Venter på godkjenning";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ApprovedAt { get; set; }
}
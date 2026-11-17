
namespace backend.Models;

public class Ticket : ITenantOwned, IAudited
{
    public int Id { get; set; }

    // Entra-tenanten raden hører til (settes automatisk)
    public string TenantId { get; set; } = string.Empty;

    public int? ReportedByEmployeeId { get; set; }
    public Employee? ReportedByEmployee { get; set; }    

    public string Category { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Status { get; set; } = "Ny";

    public string Priority { get; set; } = "Normal";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Ansvarlig ansatt (eneste ansvarlig-felt, se migrering MergeTicketAssignee)
    public int? AssignedToEmployeeId { get; set; }
    public Employee? AssignedToEmployee { get; set; }

    // Tjenesten saken ble meldt inn på (valgfri). Category settes fra tjenesten.
    public int? ServiceCatalogItemId { get; set; }
}

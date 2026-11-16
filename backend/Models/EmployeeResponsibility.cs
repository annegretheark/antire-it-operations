namespace backend.Models;

public class EmployeeResponsibility : ITenantOwned
{
    public int Id { get; set; }

    // Entra-tenanten raden hører til (settes automatisk)
    public string TenantId { get; set; } = string.Empty;

    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public string Category { get; set; } = string.Empty;

    public bool CanBeAssigned { get; set; } = true;
}
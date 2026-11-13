
namespace backend.Models;

public class Employee
{
    public int Id { get; set; }

    // Kobling til Microsoft Entra ID
    public string? EntraObjectId { get; set; }
    public string? EmployeeNumber { get; set; }

    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;

    public string Department { get; set; } = string.Empty;
    public string JobTitle { get; set; } = string.Empty;

    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }

    public string Status { get; set; } = "Aktiv";
}

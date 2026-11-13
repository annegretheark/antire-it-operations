namespace backend.Models;

public class EmployeeResponsibility
{
    public int Id { get; set; }

    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public string Category { get; set; } = string.Empty;

    public bool CanBeAssigned { get; set; } = true;
}
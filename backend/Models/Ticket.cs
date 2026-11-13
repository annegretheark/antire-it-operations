
namespace backend.Models;

public class Ticket
{
    public int Id { get; set; }

    public int? ReportedByEmployeeId { get; set; }
    public Employee? ReportedByEmployee { get; set; }    

    public string Category { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Status { get; set; } = "Ny";

    public string Priority { get; set; } = "Normal";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

   public string? AssignedTo { get; set; }
   public int? AssignedToEmployeeId { get; set; }
   public Employee? AssignedToEmployee { get; set; }
}

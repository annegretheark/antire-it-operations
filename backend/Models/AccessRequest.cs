namespace backend.Models;

public class AccessRequest
{
    public int Id { get; set; }

    public string EmployeeName { get; set; } = string.Empty;

    public string SystemName { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public string ManagerName { get; set; } = string.Empty;

    public string Status { get; set; } = "Venter på godkjenning";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ApprovedAt { get; set; }
}
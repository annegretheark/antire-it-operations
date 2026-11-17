namespace backend.Models;

public class AccessRequest : ITenantOwned, IAudited
{
    public int Id { get; set; }

    // Entra-tenanten raden hører til (settes automatisk)
    public string TenantId { get; set; } = string.Empty;

    // Entra objekt-ID til den som opprettet bestillingen (null for eldre bestillinger)
    public string? RequestedByObjectId { get; set; }

    // Den ansatte som skal ha tilgangen. EmployeeName settes fra den ansatte, og
    // er fritekst bare for bestillinger fra før koblingen fantes.
    public int? EmployeeId { get; set; }

    public string EmployeeName { get; set; } = string.Empty;

    public string SystemName { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public string ManagerName { get; set; } = string.Empty;

    public string Status { get; set; } = AccessRequestStatuses.Pending;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ApprovedAt { get; set; }

    // Systemet fra tjenestekatalogen (type access). SystemName settes fra navnet,
    // og er fritekst bare for bestillinger fra før katalogen.
    public int? ServiceCatalogItemId { get; set; }
}

// Statusene er norske tekster (brukes slik i frontend)
public static class AccessRequestStatuses
{
    public const string Pending = "Venter på godkjenning";
    public const string Approved = "Godkjent";
    public const string Rejected = "Avslått";
    public const string Completed = "Fullført";
    // Tilgangen er fjernet igjen (offboarding)
    public const string Revoked = "Tilgang fjernet";
}

namespace backend.Models;

// IT-utstyr (PC, skjerm, mobil …). Slettes ikke; utrangert utstyr får status retired,
// slik at saker som gjelder utstyret beholder koblingen.
public class Asset : ITenantOwned, IAudited
{
    public int Id { get; set; }

    // Entra-tenanten raden hører til (settes automatisk)
    public string TenantId { get; set; } = string.Empty;

    // Utstyrsnummer (merkelapp), unikt i tenanten
    public string AssetTag { get; set; } = string.Empty;

    // laptop | desktop | mobile | monitor | other (AssetTypes)
    public string Type { get; set; } = AssetTypes.Laptop;

    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }

    // in_stock | in_use | repair | retired (AssetStatuses)
    public string Status { get; set; } = AssetStatuses.InStock;

    public int? AssignedToEmployeeId { get; set; }
    public Employee? AssignedToEmployee { get; set; }

    public DateTime? PurchaseDate { get; set; }
    public DateTime? WarrantyEndsAt { get; set; }

    // manual | intune (Intune-synk kommer i fase 2, 2.8)
    public string Source { get; set; } = AssetSources.Manual;

    // Intune device id når Source = intune
    public string? ExternalId { get; set; }

    public string? Notes { get; set; }
}

public static class AssetTypes
{
    public const string Laptop = "laptop";
    public const string Desktop = "desktop";
    public const string Mobile = "mobile";
    public const string Monitor = "monitor";
    public const string Other = "other";

    public static readonly string[] All = [Laptop, Desktop, Mobile, Monitor, Other];
}

public static class AssetStatuses
{
    public const string InStock = "in_stock";
    public const string InUse = "in_use";
    public const string Repair = "repair";
    public const string Retired = "retired";

    public static readonly string[] All = [InStock, InUse, Repair, Retired];
}

public static class AssetSources
{
    public const string Manual = "manual";
    public const string Intune = "intune";
}

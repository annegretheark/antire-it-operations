namespace backend.Models;

// Én tjeneste i tjenestekatalogen, f.eks. «Feil», «Bærbar PC» eller tilgang til et system.
// Saker og tilgangsbestillinger peker hit; Category styrer automatisk tildeling.
public class ServiceCatalogItem : ITenantOwned, IAudited
{
    public int Id { get; set; }

    // Entra-tenanten raden hører til (settes automatisk)
    public string TenantId { get; set; } = string.Empty;

    // Navnet brukeren velger, unikt i tenanten
    public string Name { get; set; } = string.Empty;

    // Kobler til ansvarsområder og kategorigrupper (blir Ticket.Category)
    public string Category { get; set; } = string.Empty;

    public string? Description { get; set; }

    // incident | request | access (se ServiceCatalogTypes)
    public string Type { get; set; } = ServiceCatalogTypes.Incident;

    // Prioritet nye saker får (TicketPriorities)
    public string DefaultPriority { get; set; } = TicketPriorities.Normal;

    // Tatt i bruk når godkjent tilgang gis automatisk (2.4)
    public bool RequiresApproval { get; set; }

    // type=access: Entra-gruppen som gir tilgangen (2.4)
    public string? EntraGroupId { get; set; }

    // Deaktiverte tjenester vises ikke for brukerne, men gamle saker beholder koblingen
    public bool IsActive { get; set; } = true;
}

public static class ServiceCatalogTypes
{
    public const string Incident = "incident";
    public const string Request = "request";
    public const string Access = "access";

    public static readonly string[] All = [Incident, Request, Access];
}

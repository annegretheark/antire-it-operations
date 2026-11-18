namespace backend.Models;

// Hvem som har brukt appen og når: én rad per bruker i tenanten, oppdatert når
// brukeren kaller API-et (AppSignInService). Føres ikke i revisjonsloggen, siden
// den oppdateres ved bruk og ikke ved endringer.
public class AppSignIn : ITenantOwned
{
    public int Id { get; set; }

    // Entra-tenanten raden hører til (settes automatisk)
    public string TenantId { get; set; } = string.Empty;

    // Entra objekt-ID (oid), unik i tenanten
    public string EntraObjectId { get; set; } = string.Empty;

    // Fra tokenet ved siste besøk
    public string? Name { get; set; }
    public string? UserPrincipalName { get; set; }

    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }

    // Antall besøk: et nytt besøk regnes når det har gått mer enn 30 minutter siden sist
    public int Visits { get; set; }
}

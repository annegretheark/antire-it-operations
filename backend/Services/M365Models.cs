namespace backend.Services;

// Data fra Microsoft Graph slik appen bruker dem. Egne typer (ikke Graph SDK-
// modellene), slik at testene kan bytte ut GraphService uten Graph.

public record M365User(
    string Id,
    string? DisplayName,
    string? UserPrincipalName,
    string? Mail,
    bool? AccountEnabled,
    string? UserType,
    string? Department,
    string? JobTitle,
    DateTimeOffset? CreatedAt,
    IReadOnlyList<string> Licenses,
    // Null når dataene ikke er tilgjengelige (se M365UserOverview)
    DateTimeOffset? LastSignInAt,
    bool? MfaRegistered,
    // Siste vellykkede pålogging, interaktiv eller ikke (lastSuccessfulSignInDateTime).
    // Microsoft registrerer den fra desember 2023.
    DateTimeOffset? LastSuccessfulSignInAt = null
);

// Brukere av én type (gjest eller medlem) uten vellykket pålogging siden en dato
public record M365InactiveUsers(
    IReadOnlyList<M365User> Users,
    // Antall brukere av typen som ble vurdert
    int TotalOfType,
    // Satt når påloggingsdatoene ikke er tilgjengelige; da er listen tom
    string? SignInUnavailableReason,
    DateTimeOffset FetchedAt
)
{
    // Inaktiv: siste vellykkede pålogging før datoen, eller (hvis includeNeverSignedIn)
    // ingen registrert vellykket pålogging og opprettet før datoen.
    public static M365InactiveUsers From(
        M365UserOverview overview, string userType, DateTimeOffset before, bool includeNeverSignedIn)
    {
        var ofType = overview.Users
            .Where(u => string.Equals(u.UserType, userType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (overview.SignInUnavailableReason != null)
            return new([], ofType.Count, overview.SignInUnavailableReason, overview.FetchedAt);

        var inactive = ofType
            .Where(u => u.LastSuccessfulSignInAt is { } last
                ? last < before
                : includeNeverSignedIn && u.CreatedAt is { } created && created < before)
            .OrderBy(u => u.LastSuccessfulSignInAt ?? DateTimeOffset.MinValue)
            .ThenBy(u => u.DisplayName)
            .ToList();

        return new(inactive, ofType.Count, null, overview.FetchedAt);
    }
}

// Brukeroversikten. Delene som krever lisens eller ekstra rettighet hentes for
// seg; mangler de, står årsaken her i stedet for at hele oversikten feiler.
public record M365UserOverview(
    IReadOnlyList<M365User> Users,
    string? SignInUnavailableReason,
    string? MfaUnavailableReason,
    string? LicensesUnavailableReason,
    DateTimeOffset FetchedAt
);

public record M365Group(
    string Id,
    string? DisplayName,
    string? Description,
    string? Mail,
    // microsoft365 | security | distribution | mail_security
    string Kind,
    bool IsDynamic
);

public record M365GroupMember(
    string Id,
    string? DisplayName,
    string? UserPrincipalName,
    // user | group | device | other
    string Type
);

// Resultat av å legge en bruker i en gruppe
public enum AddMemberResult
{
    Added,
    AlreadyMember,
}

// Graph svarte med feil. Message er til brukeren (norsk); StatusCode fra Graph.
public class GraphUnavailableException(string message, int? statusCode, Exception? inner = null)
    : Exception(message, inner)
{
    public int? StatusCode { get; } = statusCode;
}

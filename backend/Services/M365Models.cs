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
    bool? MfaRegistered
);

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

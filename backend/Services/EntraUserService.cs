using Microsoft.Extensions.Caching.Memory;
using backend.Data;
using backend.Models;

namespace backend.Services;

public enum EntraUserAction
{
    Disable,
    Enable,
    Delete,
}

// Resultat for én bruker: Error er satt når endringen feilet
public record UserChangeResult(string UserId, string? DisplayName, bool Ok, string? Error);

// Kontostatus i Entra for en ansatt (Ansatte og offboarding)
public static class EntraAccountStatuses
{
    public const string Enabled = "enabled";
    public const string Disabled = "disabled";
    // Den ansatte peker på en konto som ikke finnes (f.eks. slettet)
    public const string Missing = "missing";
    // Den ansatte har ingen konto i Entra (EntraObjectId mangler)
    public const string NotLinked = "not_linked";
    // Brukeroversikten kunne ikke hentes
    public const string Unknown = "unknown";

    public static string For(string? entraObjectId, IReadOnlyDictionary<string, M365User>? usersById)
    {
        if (string.IsNullOrWhiteSpace(entraObjectId))
            return NotLinked;
        if (usersById == null)
            return Unknown;
        if (!usersById.TryGetValue(entraObjectId.ToLowerInvariant(), out var user))
            return Missing;

        return user.AccountEnabled == false ? Disabled : Enabled;
    }
}

// Forespørselen kan ikke utføres (tomt valg, for mange, deg selv osv.); gir 400
public class EntraChangeRejectedException(string message) : Exception(message);

// Brukerkontoer i Entra ID for tenanten i scopet, felles for Microsoft 365-siden,
// Ansatte og offboarding.
//
// - Brukeroversikten hentes med appens leserettighet og caches i 5 minutter.
// - Endringer (deaktivere, aktivere, slette) gjøres med den innloggede personens
//   egne rettigheter (on-behalf-of): Entra sjekker personens rolle. Hver endring
//   logges i revisjonsloggen, og oversikten hentes på nytt etterpå.
public class EntraUserService
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    public const int MaxUsersPerChange = 50;

    private readonly GraphService _graph;
    private readonly ITenantContext _tenant;
    private readonly IMemoryCache _cache;
    private readonly ICurrentUser _currentUser;
    private readonly AppDbContext _db;
    private readonly IHttpContextAccessor _http;

    public EntraUserService(
        GraphService graph,
        ITenantContext tenant,
        IMemoryCache cache,
        ICurrentUser currentUser,
        AppDbContext db,
        IHttpContextAccessor http)
    {
        _graph = graph;
        _tenant = tenant;
        _cache = cache;
        _currentUser = currentUser;
        _db = db;
        _http = http;
    }

    private string TenantId =>
        _tenant.TenantId ?? throw new InvalidOperationException("Tenant mangler.");

    // Samme nøkkel som M365Controller bruker for grupper («m365:{tenant}:…»)
    public static string CacheKey(string tenantId, string key) => $"m365:{tenantId}:{key}";

    private string UsersKey => CacheKey(TenantId, "users");

    // Brukeroversikten (cachet). Kaster GraphUnavailableException når Graph ikke svarer.
    public async Task<M365UserOverview> GetOverviewAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (!refresh && _cache.TryGetValue(UsersKey, out M365UserOverview? cached) && cached != null)
            return cached;

        var overview = await _graph.GetUserOverviewAsync(TenantId, cancellationToken);
        _cache.Set(UsersKey, overview, CacheFor);
        return overview;
    }

    // Brukerne etter objekt-ID (små bokstaver), eller null og årsaken når Graph ikke svarer
    public async Task<(IReadOnlyDictionary<string, M365User>? UsersById, string? UnavailableReason)> TryGetUsersByIdAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var overview = await GetOverviewAsync(cancellationToken: cancellationToken);
            return (overview.Users.ToDictionary(u => u.Id.ToLowerInvariant()), null);
        }
        catch (GraphUnavailableException e)
        {
            return (null, e.Message);
        }
    }

    public async Task<List<UserChangeResult>> ChangeAsync(
        IEnumerable<string>? userIds,
        EntraUserAction action,
        CancellationToken cancellationToken = default)
    {
        var ids = (userIds ?? [])
            .Select(id => id?.Trim().ToLowerInvariant() ?? "")
            .Distinct()
            .ToList();

        if (ids.Count == 0)
            throw new EntraChangeRejectedException("Velg minst én bruker.");
        if (ids.Count > MaxUsersPerChange)
            throw new EntraChangeRejectedException($"Maks {MaxUsersPerChange} brukere om gangen.");
        if (ids.Any(id => !Guid.TryParse(id, out _)))
            throw new EntraChangeRejectedException("Ugyldig bruker-ID.");

        var self = _currentUser.ObjectId?.ToLowerInvariant();
        if (self != null && ids.Contains(self))
            throw new EntraChangeRejectedException("Du kan ikke deaktivere eller slette deg selv.");

        var token = UserToken();

        // Bare brukere i denne tenanten (fra oversikten, som også gir navn til loggen)
        var overview = await GetOverviewAsync(cancellationToken: cancellationToken);
        var known = overview.Users.ToDictionary(u => u.Id.ToLowerInvariant());
        var results = new List<UserChangeResult>();

        foreach (var id in ids)
        {
            if (!known.TryGetValue(id, out var user))
            {
                results.Add(new UserChangeResult(id, null, false, "Fant ikke brukeren i tenanten."));
                continue;
            }

            try
            {
                await (action switch
                {
                    EntraUserAction.Delete => _graph.DeleteUserAsync(TenantId, token, user.Id, cancellationToken),
                    _ => _graph.SetAccountEnabledAsync(
                        TenantId, token, user.Id, action == EntraUserAction.Enable, cancellationToken),
                });

                results.Add(new UserChangeResult(user.Id, user.DisplayName, true, null));
                _db.AddAuditEvent("EntraUser", user.Id, AuditAction(action),
                    new { user.DisplayName, user.UserPrincipalName });
            }
            catch (GraphUnavailableException e)
            {
                results.Add(new UserChangeResult(user.Id, user.DisplayName, false, e.Message));
            }
        }

        if (results.Any(r => r.Ok))
        {
            await _db.SaveChangesAsync(cancellationToken);
            _cache.Remove(UsersKey);
        }

        return results;
    }

    private static string AuditAction(EntraUserAction action) => action switch
    {
        EntraUserAction.Disable => AuditActions.UserDisabled,
        EntraUserAction.Enable => AuditActions.UserEnabled,
        _ => AuditActions.UserDeleted,
    };

    // Tokenet personen sendte til API-et (byttes mot et Graph-token for samme person)
    private string UserToken()
    {
        var header = _http.HttpContext?.Request.Headers.Authorization.ToString() ?? "";
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : "";
    }
}

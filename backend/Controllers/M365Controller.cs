using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using backend.Data;
using backend.Models;
using backend.Services;

namespace backend.Controllers;

public record SetAccountEnabledRequest(List<string>? UserIds, bool Enabled);

public record DeleteUsersRequest(List<string>? UserIds);

// Resultat for én bruker: Error er satt når endringen feilet
public record UserChangeResult(string UserId, string? DisplayName, bool Ok, string? Error);

// Microsoft 365 i innlogget tenant: brukere og grupper fra Graph (Helpdesk.Admin).
// Svarene caches i 5 minutter per tenant; ?refresh=true henter på nytt.
//
// Deaktivering, aktivering og sletting av brukere skjer med den innloggede
// personens egne rettigheter (on-behalf-of): i tillegg til Helpdesk.Admin må
// personen ha en rolle i Entra som tillater det.
[ApiController]
[Route("api/m365")]
[Authorize(Policy = AuthPolicies.HelpdeskAdmin)]
public class M365Controller : ControllerBase
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    public const int MaxUsersPerChange = 50;

    private readonly GraphService _graph;
    private readonly ITenantContext _tenant;
    private readonly IMemoryCache _cache;
    private readonly ICurrentUser _currentUser;
    private readonly AppDbContext _db;

    public M365Controller(
        GraphService graph, ITenantContext tenant, IMemoryCache cache, ICurrentUser currentUser, AppDbContext db)
    {
        _graph = graph;
        _tenant = tenant;
        _cache = cache;
        _currentUser = currentUser;
        _db = db;
    }

    [HttpPost("users/account-enabled")]
    public Task<IActionResult> SetAccountEnabled([FromBody] SetAccountEnabledRequest request) =>
        ChangeUsersAsync(
            request.UserIds,
            request.Enabled ? AuditActions.UserEnabled : AuditActions.UserDisabled,
            (token, id, ct) => _graph.SetAccountEnabledAsync(TenantId, token, id, request.Enabled, ct));

    [HttpPost("users/delete")]
    public Task<IActionResult> DeleteUsers([FromBody] DeleteUsersRequest request) =>
        ChangeUsersAsync(
            request.UserIds,
            AuditActions.UserDeleted,
            (token, id, ct) => _graph.DeleteUserAsync(TenantId, token, id, ct));

    // Felles for endringene: kontroll, én bruker om gangen, revisjonslogg og ny brukeroversikt
    private async Task<IActionResult> ChangeUsersAsync(
        List<string>? userIds,
        string auditAction,
        Func<string, string, CancellationToken, Task> change)
    {
        var ids = (userIds ?? [])
            .Select(id => id?.Trim().ToLowerInvariant() ?? "")
            .Distinct()
            .ToList();

        if (ids.Count == 0)
            return BadRequest(new { message = "Velg minst én bruker." });
        if (ids.Count > MaxUsersPerChange)
            return BadRequest(new { message = $"Maks {MaxUsersPerChange} brukere om gangen." });
        if (ids.Any(id => !Guid.TryParse(id, out _)))
            return BadRequest(new { message = "Ugyldig bruker-ID." });

        var self = _currentUser.ObjectId?.ToLowerInvariant();
        if (self != null && ids.Contains(self))
            return BadRequest(new { message = "Du kan ikke deaktivere eller slette deg selv." });

        var token = UserToken();
        var ct = HttpContext.RequestAborted;

        // Bare brukere i denne tenanten (fra oversikten, som også gir navn til loggen)
        M365UserOverview overview;
        try
        {
            overview = _cache.TryGetValue(CacheKey("users"), out M365UserOverview? cached) && cached != null
                ? cached
                : await _graph.GetUserOverviewAsync(TenantId, ct);
        }
        catch (GraphUnavailableException e)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = e.Message });
        }

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
                await change(token, user.Id, ct);
                results.Add(new UserChangeResult(user.Id, user.DisplayName, true, null));
                _db.AddAuditEvent("EntraUser", user.Id, auditAction,
                    new { user.DisplayName, user.UserPrincipalName });
            }
            catch (GraphUnavailableException e)
            {
                results.Add(new UserChangeResult(user.Id, user.DisplayName, false, e.Message));
            }
        }

        if (results.Any(r => r.Ok))
        {
            await _db.SaveChangesAsync(ct);
            _cache.Remove(CacheKey("users"));
        }

        return Ok(results);
    }

    // Tokenet personen sendte til API-et (byttes mot et Graph-token for samme person)
    private string UserToken()
    {
        var header = Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : "";
    }

    private string CacheKey(string key) => $"m365:{TenantId}:{key}";

    [HttpGet("users")]
    public Task<IActionResult> GetUsers([FromQuery] bool refresh = false) =>
        CachedAsync("users", refresh, ct => _graph.GetUserOverviewAsync(TenantId, ct));

    // Gjester eller medlemmer uten vellykket pålogging siden «before» (dato).
    // Bygger på samme (cachede) brukeroversikt som /users. Kun lesing.
    [HttpGet("inactive-users")]
    public async Task<IActionResult> GetInactiveUsers(
        [FromQuery] string? userType,
        [FromQuery] DateOnly? before,
        [FromQuery] bool includeNeverSignedIn = true,
        [FromQuery] bool refresh = false)
    {
        var type = userType?.ToLowerInvariant() switch
        {
            "guest" => "Guest",
            "member" => "Member",
            _ => null,
        };
        if (type == null)
            return BadRequest(new { message = "Brukertype må være Guest eller Member." });

        if (before is not { } date)
            return BadRequest(new { message = "Dato for siste pålogging mangler (before=ÅÅÅÅ-MM-DD)." });
        if (date > DateOnly.FromDateTime(DateTime.UtcNow))
            return BadRequest(new { message = "Datoen kan ikke være frem i tid." });

        var cutoff = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        return await CachedAsync("users", refresh,
            ct => _graph.GetUserOverviewAsync(TenantId, ct),
            overview => M365InactiveUsers.From(overview, type, cutoff, includeNeverSignedIn));
    }

    [HttpGet("groups")]
    public Task<IActionResult> GetGroups([FromQuery] bool refresh = false) =>
        CachedAsync("groups", refresh, ct => _graph.GetGroupsAsync(TenantId, ct));

    [HttpGet("groups/{groupId:guid}/members")]
    public Task<IActionResult> GetGroupMembers(Guid groupId, [FromQuery] bool refresh = false) =>
        CachedAsync($"groups/{groupId}", refresh,
            ct => _graph.GetGroupMembersAsync(TenantId, groupId.ToString(), ct));

    private string TenantId => _tenant.TenantId!;

    private Task<IActionResult> CachedAsync<T>(
        string key, bool refresh, Func<CancellationToken, Task<T>> load) =>
        CachedAsync<T, T>(key, refresh, load, value => value);

    // Henter fra cachen (eller Graph) og gjør om svaret med «shape» før det sendes
    private async Task<IActionResult> CachedAsync<T, TResult>(
        string key, bool refresh, Func<CancellationToken, Task<T>> load, Func<T, TResult> shape)
    {
        var cacheKey = CacheKey(key);

        if (!refresh && _cache.TryGetValue(cacheKey, out T? cached) && cached != null)
            return Ok(shape(cached));

        try
        {
            var value = await load(HttpContext.RequestAborted);
            _cache.Set(cacheKey, value, CacheFor);
            return Ok(shape(value));
        }
        catch (GraphUnavailableException e)
        {
            // Manglende rettighet eller lisens er ikke en feil i appen
            return StatusCode(StatusCodes.Status502BadGateway, new { message = e.Message });
        }
    }
}

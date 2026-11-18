using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using backend.Services;

namespace backend.Controllers;

// Microsoft 365 i innlogget tenant: brukere og grupper fra Graph (Helpdesk.Admin).
// Svarene caches i 5 minutter per tenant; ?refresh=true henter på nytt.
[ApiController]
[Route("api/m365")]
[Authorize(Policy = AuthPolicies.HelpdeskAdmin)]
public class M365Controller : ControllerBase
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    private readonly GraphService _graph;
    private readonly ITenantContext _tenant;
    private readonly IMemoryCache _cache;

    public M365Controller(GraphService graph, ITenantContext tenant, IMemoryCache cache)
    {
        _graph = graph;
        _tenant = tenant;
        _cache = cache;
    }

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
        var cacheKey = $"m365:{TenantId}:{key}";

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

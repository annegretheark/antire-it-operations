using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using backend.Services;

namespace backend.Controllers;

public record SetAccountEnabledRequest(List<string>? UserIds, bool Enabled);

public record DeleteUsersRequest(List<string>? UserIds);

// Microsoft 365 i innlogget tenant: brukere og grupper fra Graph (Helpdesk.Admin).
// Svarene caches i 5 minutter per tenant; ?refresh=true henter på nytt.
//
// Deaktivering, aktivering og sletting av brukere skjer med den innloggede
// personens egne rettigheter (on-behalf-of, se EntraUserService): i tillegg til
// Helpdesk.Admin må personen ha en rolle i Entra som tillater det.
[ApiController]
[Route("api/m365")]
[Authorize(Policy = AuthPolicies.HelpdeskAdmin)]
public class M365Controller : ControllerBase
{
    public const int MaxUsersPerChange = EntraUserService.MaxUsersPerChange;

    private readonly GraphService _graph;
    private readonly ITenantContext _tenant;
    private readonly IMemoryCache _cache;
    private readonly EntraUserService _users;

    public M365Controller(GraphService graph, ITenantContext tenant, IMemoryCache cache, EntraUserService users)
    {
        _graph = graph;
        _tenant = tenant;
        _cache = cache;
        _users = users;
    }

    [HttpPost("users/account-enabled")]
    public Task<IActionResult> SetAccountEnabled([FromBody] SetAccountEnabledRequest request) =>
        ChangeAsync(request.UserIds, request.Enabled ? EntraUserAction.Enable : EntraUserAction.Disable);

    [HttpPost("users/delete")]
    public Task<IActionResult> DeleteUsers([FromBody] DeleteUsersRequest request) =>
        ChangeAsync(request.UserIds, EntraUserAction.Delete);

    private async Task<IActionResult> ChangeAsync(List<string>? userIds, EntraUserAction action)
    {
        try
        {
            return Ok(await _users.ChangeAsync(userIds, action, HttpContext.RequestAborted));
        }
        catch (EntraChangeRejectedException e)
        {
            return BadRequest(new { message = e.Message });
        }
        catch (GraphUnavailableException e)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = e.Message });
        }
    }

    [HttpGet("users")]
    public Task<IActionResult> GetUsers([FromQuery] bool refresh = false) =>
        OverviewAsync(refresh, overview => overview);

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

        return await OverviewAsync(refresh,
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

    private async Task<IActionResult> OverviewAsync<TResult>(bool refresh, Func<M365UserOverview, TResult> shape)
    {
        try
        {
            return Ok(shape(await _users.GetOverviewAsync(refresh, HttpContext.RequestAborted)));
        }
        catch (GraphUnavailableException e)
        {
            // Manglende rettighet eller lisens er ikke en feil i appen
            return StatusCode(StatusCodes.Status502BadGateway, new { message = e.Message });
        }
    }

    // Henter fra cachen (eller Graph)
    private async Task<IActionResult> CachedAsync<T>(
        string key, bool refresh, Func<CancellationToken, Task<T>> load)
    {
        var cacheKey = EntraUserService.CacheKey(TenantId, key);

        if (!refresh && _cache.TryGetValue(cacheKey, out T? cached) && cached != null)
            return Ok(cached);

        try
        {
            var value = await load(HttpContext.RequestAborted);
            _cache.Set(cacheKey, value, EntraUserService.CacheFor);
            return Ok(value);
        }
        catch (GraphUnavailableException e)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = e.Message });
        }
    }
}

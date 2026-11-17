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

    [HttpGet("groups")]
    public Task<IActionResult> GetGroups([FromQuery] bool refresh = false) =>
        CachedAsync("groups", refresh, ct => _graph.GetGroupsAsync(TenantId, ct));

    [HttpGet("groups/{groupId:guid}/members")]
    public Task<IActionResult> GetGroupMembers(Guid groupId, [FromQuery] bool refresh = false) =>
        CachedAsync($"groups/{groupId}", refresh,
            ct => _graph.GetGroupMembersAsync(TenantId, groupId.ToString(), ct));

    private string TenantId => _tenant.TenantId!;

    private async Task<IActionResult> CachedAsync<T>(
        string key, bool refresh, Func<CancellationToken, Task<T>> load)
    {
        var cacheKey = $"m365:{TenantId}:{key}";

        if (!refresh && _cache.TryGetValue(cacheKey, out T? cached))
            return Ok(cached);

        try
        {
            var value = await load(HttpContext.RequestAborted);
            _cache.Set(cacheKey, value, CacheFor);
            return Ok(value);
        }
        catch (GraphUnavailableException e)
        {
            // Manglende rettighet eller lisens er ikke en feil i appen
            return StatusCode(StatusCodes.Status502BadGateway, new { message = e.Message });
        }
    }
}

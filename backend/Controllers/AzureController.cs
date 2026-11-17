using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using backend.Data;
using backend.Models;
using backend.Services;

namespace backend.Controllers;

public record AzureSyncResult(int Added, int Updated, int Hidden, int Total);

public record AzureResourceList(
    IReadOnlyList<AzureResource> Resources,
    int Total,
    IReadOnlyList<string> Types,
    IReadOnlyList<string> Locations,
    IReadOnlyList<string> ResourceGroups,
    DateTimeOffset FetchedAt,
    // Satt når listen er tom av en grunn brukeren kan gjøre noe med
    string? Message
);

// Azure i innlogget tenant (Helpdesk.Admin): abonnementer appen har Reader på,
// og ressursene i dem fra Resource Graph.
[ApiController]
[Route("api/azure")]
[Authorize(Policy = AuthPolicies.HelpdeskAdmin)]
public class AzureController : ControllerBase
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    private readonly AppDbContext _db;
    private readonly AzureService _azure;
    private readonly ITenantContext _tenant;
    private readonly IMemoryCache _cache;

    public AzureController(AppDbContext db, AzureService azure, ITenantContext tenant, IMemoryCache cache)
    {
        _db = db;
        _azure = azure;
        _tenant = tenant;
        _cache = cache;
    }

    [HttpGet("subscriptions")]
    public async Task<ActionResult<IEnumerable<AzureSubscription>>> GetSubscriptions() =>
        await _db.AzureSubscriptions
            .AsNoTracking()
            .OrderByDescending(s => s.IsVisible)
            .ThenBy(s => s.DisplayName)
            .ToListAsync();

    // Henter abonnementene appen ser i Azure og oppdaterer listen
    [HttpPost("subscriptions/sync")]
    public async Task<IActionResult> SyncSubscriptions()
    {
        List<AzureSubscriptionInfo> found;

        try
        {
            found = await _azure.ListSubscriptionsAsync(_tenant.TenantId!, HttpContext.RequestAborted);
        }
        catch (AzureUnavailableException e)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = e.Message });
        }

        var now = DateTime.UtcNow;
        var existing = await _db.AzureSubscriptions.ToListAsync();
        var byId = existing.ToDictionary(s => s.SubscriptionId, StringComparer.OrdinalIgnoreCase);
        int added = 0, updated = 0, hidden = 0;

        foreach (var info in found)
        {
            var id = info.SubscriptionId.ToLowerInvariant();

            if (byId.TryGetValue(id, out var subscription))
            {
                subscription.DisplayName = info.DisplayName;
                subscription.State = info.State;
                subscription.IsVisible = true;
                subscription.LastSeenAt = now;
                updated++;
            }
            else
            {
                _db.AzureSubscriptions.Add(new AzureSubscription
                {
                    SubscriptionId = id,
                    DisplayName = info.DisplayName,
                    State = info.State,
                    IsVisible = true,
                    FirstSeenAt = now,
                    LastSeenAt = now,
                });
                added++;
            }
        }

        var seen = found.Select(f => f.SubscriptionId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var subscription in existing.Where(s => s.IsVisible && !seen.Contains(s.SubscriptionId)))
        {
            subscription.IsVisible = false;
            hidden++;
        }

        _db.AddAuditEvent("AzureSubscriptionSync", "azure", AuditActions.Synced, new
        {
            added, updated, hidden, total = found.Count,
        });

        await _db.SaveChangesAsync();

        return Ok(new AzureSyncResult(added, updated, hidden, found.Count));
    }

    // Ressurser i synlige abonnementer. Filter og søk gjøres her på listen fra
    // Resource Graph, slik at brukerens tekst aldri blir en del av spørringen.
    [HttpGet("resources")]
    public async Task<IActionResult> GetResources(
        [FromQuery] string? subscriptionId,
        [FromQuery] string? type,
        [FromQuery] string? location,
        [FromQuery] string? resourceGroup,
        [FromQuery] string? search,
        [FromQuery] bool refresh = false)
    {
        var subscriptionIds = await _db.AzureSubscriptions
            .AsNoTracking()
            .Where(s => s.IsVisible)
            .Select(s => s.SubscriptionId)
            .OrderBy(s => s)
            .ToListAsync();

        if (subscriptionIds.Count == 0)
        {
            return Ok(new AzureResourceList([], 0, [], [], [], DateTimeOffset.UtcNow,
                "Ingen abonnementer er synket. Gi appen rollen Reader og trykk Synk under Abonnementer."));
        }

        var cacheKey = $"azure:{_tenant.TenantId}:resources:{string.Join(',', subscriptionIds)}";

        if (refresh || !_cache.TryGetValue(cacheKey, out (List<AzureResource> Items, DateTimeOffset At) all))
        {
            try
            {
                var items = await _azure.QueryResourcesAsync(
                    _tenant.TenantId!, subscriptionIds, HttpContext.RequestAborted);
                all = (items, DateTimeOffset.UtcNow);
                _cache.Set(cacheKey, all, CacheFor);
            }
            catch (AzureUnavailableException e)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new { message = e.Message });
            }
        }

        IEnumerable<AzureResource> query = all.Items;

        if (!string.IsNullOrWhiteSpace(subscriptionId))
            query = query.Where(r => r.SubscriptionId.Equals(subscriptionId, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(type))
            query = query.Where(r => r.Type.Equals(type, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(location))
            query = query.Where(r => r.Location.Equals(location, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(resourceGroup))
            query = query.Where(r => r.ResourceGroup.Equals(resourceGroup, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(r =>
                r.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                r.ResourceGroup.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                r.Tags.Any(t =>
                    t.Key.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    t.Value.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

        static List<string> Distinct(IEnumerable<string> values) =>
            values.Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();

        return Ok(new AzureResourceList(
            query.ToList(),
            all.Items.Count,
            Distinct(all.Items.Select(r => r.Type)),
            Distinct(all.Items.Select(r => r.Location)),
            Distinct(all.Items.Select(r => r.ResourceGroup)),
            all.At,
            all.Items.Count == 0 ? "Abonnementene har ingen ressurser appen kan se." : null));
    }
}

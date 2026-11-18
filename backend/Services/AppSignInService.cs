using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using backend.Data;
using backend.Models;

namespace backend.Services;

// Registrerer hvem som bruker appen og når (AppSignIn). Kalles for hvert API-kall
// fra en innlogget bruker i en tillatt tenant, men skriver til databasen høyst én
// gang per bruker hvert 5. minutt. Feiler registreringen, logges det, og
// forespørselen går videre som vanlig.
public class AppSignInService(
    IServiceScopeFactory scopes,
    IMemoryCache cache,
    IOptions<TenantOptions> tenants,
    ILogger<AppSignInService> logger)
{
    public static readonly TimeSpan UpdateEvery = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan NewVisitAfter = TimeSpan.FromMinutes(30);

    public async Task RecordAsync(HttpContext context)
    {
        var user = context.User;
        var tenantId = user.GetTenantId()?.ToLowerInvariant();
        var objectId = user.GetObjectId();

        if (tenantId == null || objectId == null || !tenants.Value.IsAllowed(tenantId))
            return;

        var cacheKey = $"app-signin:{tenantId}:{objectId}";
        if (cache.TryGetValue(cacheKey, out _))
            return;
        cache.Set(cacheKey, true, UpdateEvery);

        try
        {
            // Eget scope og egen DbContext, så en feil her ikke henger igjen i
            // forespørselens DbContext. Tenanten leses fra samme innlogging.
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;

            var row = await db.AppSignIns.FirstOrDefaultAsync(s => s.EntraObjectId == objectId, context.RequestAborted);

            if (row == null)
            {
                row = new AppSignIn { EntraObjectId = objectId, FirstSeenAt = now, Visits = 1 };
                db.AppSignIns.Add(row);
            }
            else if (now - row.LastSeenAt > NewVisitAfter)
            {
                row.Visits++;
            }

            row.LastSeenAt = now;
            row.Name = Truncate(user.FindFirst("name")?.Value ?? user.Identity?.Name, 256);
            row.UserPrincipalName = Truncate(
                user.FindFirst("preferred_username")?.Value ?? user.FindFirst("upn")?.Value, 256);

            await db.SaveChangesAsync(context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            // Klienten avbrøt forespørselen
        }
        catch (Exception e)
        {
            // Prøver igjen etter 5 minutter (cachen over), ikke på hvert kall
            logger.LogWarning(e, "Kunne ikke registrere pålogging for {ObjectId} i tenant {TenantId}.", objectId, tenantId);
        }
    }

    private static string? Truncate(string? value, int max) =>
        value == null || value.Length <= max ? value : value[..max];
}

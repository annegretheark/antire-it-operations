using Microsoft.Identity.Web;

namespace backend.Services;

// En tenant appen kan brukes i (f.eks. test og prod)
public class AllowedTenant
{
    public string TenantId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

// Konfigurasjon: "Tenants": { "Allowed": [ { "TenantId": "...", "Name": "Prod" } ] }
public class TenantOptions
{
    public const string Section = "Tenants";

    public List<AllowedTenant> Allowed { get; set; } = new();

    public bool IsAllowed(string? tenantId) =>
        !string.IsNullOrWhiteSpace(tenantId) &&
        Allowed.Any(t => string.Equals(t.TenantId, tenantId, StringComparison.OrdinalIgnoreCase));

    public string? NameOf(string? tenantId) =>
        Allowed.FirstOrDefault(t =>
            string.Equals(t.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))?.Name;
}

// Tenanten til innlogget bruker (tid-claimet i tokenet). Alle data og
// Graph-kall i en forespørsel gjelder denne tenanten.
public interface ITenantContext
{
    string? TenantId { get; }
}

// Bakgrunnsjobber (uten innlogget bruker) setter tenanten selv med UseTenant i
// sitt eget scope; da bruker AppDbContext og tjenestene i scopet den tenanten.
public class HttpTenantContext : ITenantContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private string? _tenantOverride;

    public HttpTenantContext(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public string? TenantId =>
        _tenantOverride ??
        _httpContextAccessor.HttpContext?.User.GetTenantId()?.ToLowerInvariant();

    public void UseTenant(string tenantId) =>
        _tenantOverride = tenantId.ToLowerInvariant();
}

public static class TenantScopeExtensions
{
    // Nytt DI-scope der alt arbeid gjelder den oppgitte tenanten
    public static IServiceScope CreateTenantScope(this IServiceScopeFactory scopes, string tenantId)
    {
        var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<HttpTenantContext>().UseTenant(tenantId);
        return scope;
    }
}

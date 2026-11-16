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

public class HttpTenantContext : ITenantContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpTenantContext(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public string? TenantId =>
        _httpContextAccessor.HttpContext?.User.GetTenantId()?.ToLowerInvariant();
}

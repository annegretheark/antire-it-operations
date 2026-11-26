using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace backend.Tests;

// Starter API-et i minnet med en test-autentisering i stedet for Entra ID.
// Miljøet "Testing" gjør at user-secrets (ekte database og Graph) ikke lastes.
public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // Dummyverdier slik at GraphService kan opprettes. Testene kaller aldri Graph.
        builder.UseSetting("MicrosoftGraph:TenantId", "00000000-0000-0000-0000-000000000001");
        builder.UseSetting("MicrosoftGraph:ClientId", "00000000-0000-0000-0000-000000000002");
        builder.UseSetting("MicrosoftGraph:ClientSecret", "not-a-real-secret");

        // To tillatte testtenanter (erstatter tenantene i appsettings.json)
        builder.UseSetting("Tenants:Allowed:0:TenantId", TestAuthHandler.AllowedTenantId);
        builder.UseSetting("Tenants:Allowed:0:Name", "Test");
        builder.UseSetting("Tenants:Allowed:1:TenantId", TestAuthHandler.OtherAllowedTenantId);
        builder.UseSetting("Tenants:Allowed:1:Name", "Prod");

        // SLA-jobben kjøres direkte fra testene (SlaMonitorService.CheckTenantAsync)
        builder.UseSetting("Sla:MonitorEnabled", "false");
        builder.UseSetting("Alerts:MonitorEnabled", "false");
        builder.UseSetting("Gdpr:MonitorEnabled", "false");

        builder.ConfigureTestServices(services =>
        {
            services
                .AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.SchemeName, _ => { });

            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                options.DefaultScheme = TestAuthHandler.SchemeName;
            });
        });
    }

    // Klient som opptrer som innlogget bruker i en tillatt tenant med gitte app-roller
    public HttpClient CreateClientWithRoles(params string[] roles) =>
        CreateClientInTenant(TestAuthHandler.AllowedTenantId, roles);

    // Klient som opptrer som innlogget bruker i en bestemt tenant
    public HttpClient CreateClientInTenant(string tenantId, params string[] roles)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "test-user");
        client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, tenantId);

        if (roles.Length > 0)
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(',', roles));

        return client;
    }
}

// Autentiserer forespørsler som har X-Test-User; roller fra X-Test-Roles
public class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string UserHeader = "X-Test-User";
    public const string RolesHeader = "X-Test-Roles";
    public const string TenantHeader = "X-Test-Tenant";

    // Fast objekt-ID for testbrukeren
    public const string TestObjectId = "00000000-0000-0000-0000-0000000000aa";

    // Valgfri objekt-ID, for tester med flere personer (f.eks. separasjon av plikter)
    public const string ObjectIdHeader = "X-Test-ObjectId";

    // Tillatte tenanter i testene (settes i TestWebApplicationFactory)
    public const string AllowedTenantId = "11111111-1111-1111-1111-111111111111";
    public const string OtherAllowedTenantId = "22222222-2222-2222-2222-222222222222";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var user))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.ToString()),
            new("http://schemas.microsoft.com/identity/claims/objectidentifier",
                Request.Headers.TryGetValue(ObjectIdHeader, out var oid) ? oid.ToString() : TestObjectId),
        };

        if (Request.Headers.TryGetValue(TenantHeader, out var tenant))
            claims.Add(new Claim("http://schemas.microsoft.com/identity/claims/tenantid", tenant.ToString()));

        if (Request.Headers.TryGetValue(RolesHeader, out var roles))
        {
            claims.AddRange(roles.ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(role => new Claim(ClaimTypes.Role, role.Trim())));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

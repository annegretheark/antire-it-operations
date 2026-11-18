using System.Net.Http.Json;
using System.Text.Json;
using backend.Services;

namespace backend.Tests;

// Hvem som har brukt appen (AppSignInService og /api/appsignins)
public class AppSignInTests(SqliteWebApplicationFactory factory)
    : IClassFixture<SqliteWebApplicationFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private record SignIn(string EntraObjectId, string? Name, DateTime FirstSeenAt, DateTime LastSeenAt, int Visits);

    private async Task<List<SignIn>> ListAsync(string tenantId)
    {
        var response = await factory.CreateClientInTenant(tenantId, AppRoles.HelpdeskAdmin).GetAsync("/api/appsignins");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<SignIn>>(Json))!;
    }

    [Fact]
    public async Task Api_calls_are_recorded_once_per_user_and_tenant()
    {
        // En vanlig bruker uten roller bruker appen flere ganger
        var user = factory.CreateClientInTenant(TestAuthHandler.AllowedTenantId);
        await user.GetAsync("/api/tickets/me");
        await user.GetAsync("/api/tickets/me");
        await user.GetAsync("/api/tickets/me");

        var signIns = await ListAsync(TestAuthHandler.AllowedTenantId);
        var signIn = Assert.Single(signIns);
        Assert.Equal(TestAuthHandler.TestObjectId, signIn.EntraObjectId);
        Assert.Equal("test-user", signIn.Name);
        Assert.Equal(1, signIn.Visits);
        Assert.True(signIn.LastSeenAt > DateTime.UtcNow.AddMinutes(-1));

        // Den andre tenanten ser bare sin egen rad (fra administratorens eget kall)
        var other = await ListAsync(TestAuthHandler.OtherAllowedTenantId);
        Assert.Single(other);
    }

    [Fact]
    public async Task Health_check_and_frontend_are_not_recorded()
    {
        var before = factory.CreateDbContext(TestAuthHandler.AllowedTenantId).AppSignIns.Count();

        // Forespørsler utenfor /api registreres ikke, selv med innlogging
        var user = factory.CreateClientInTenant(TestAuthHandler.AllowedTenantId);
        await user.GetAsync("/health");

        Assert.Equal(before, factory.CreateDbContext(TestAuthHandler.AllowedTenantId).AppSignIns.Count());
    }
}

using System.Net;
using System.Net.Http.Json;
using backend.Services;

namespace backend.Tests;

// Bare tenanter i Tenants:Allowed kan bruke API-et. Tenantfilteret på data
// testes når databasetester (Testcontainers) er på plass.
public class TenantTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private const string UnknownTenantId = "99999999-9999-9999-9999-999999999999";

    [Theory]
    [InlineData("/api/tickets/me")]
    [InlineData("/api/tickets")]
    [InlineData("/api/employees")]
    [InlineData("/api/accessrequests")]
    [InlineData("/api/audit")]
    [InlineData("/api/servicecatalog")]
    [InlineData("/api/employeeresponsibilities")]
    [InlineData("/api/slatargets")]
    [InlineData("/api/assets")]
    [InlineData("/api/m365/users")]
    [InlineData("/api/azure/subscriptions")]
    [InlineData("/api/alerts")]
    [InlineData("/api/lifecycle")]
    [InlineData("/api/gdpr/processing")]
    [InlineData("/api/tickets/1/history")]
    public async Task User_from_unknown_tenant_is_forbidden(string path)
    {
        var client = factory.CreateClientInTenant(
            UnknownTenantId, AppRoles.HelpdeskAdmin, AppRoles.EmployeeSyncAdmin);

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Token_without_tenant_is_forbidden()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "test-user");

        var response = await client.GetAsync("/api/tickets/me");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(TestAuthHandler.AllowedTenantId, "Test")]
    [InlineData(TestAuthHandler.OtherAllowedTenantId, "Prod")]
    public async Task Me_reports_tenant_of_signed_in_user(string tenantId, string expectedName)
    {
        var client = factory.CreateClientInTenant(tenantId);

        var me = await client.GetFromJsonAsync<MeResponse>("/api/tickets/me");

        Assert.NotNull(me);
        Assert.Equal(tenantId, me.TenantId);
        Assert.Equal(expectedName, me.TenantName);
    }

    private sealed record MeResponse(string? TenantId, string? TenantName);
}

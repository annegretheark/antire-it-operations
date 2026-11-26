using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using backend.Controllers;
using backend.Models;
using backend.Services;

namespace backend.Tests;

// Kontostatus i Entra på Ansatte (GET /api/employees/entra-status), med falsk Graph
public class EmployeeEntraStatusTests(SqliteWebApplicationFactory factory)
    : IClassFixture<SqliteWebApplicationFactory>
{
    private const string TenantA = TestAuthHandler.AllowedTenantId;
    private const string TenantB = TestAuthHandler.OtherAllowedTenantId;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static M365User EntraUser(string id, bool enabled) =>
        new(id, "Bruker", "bruker@agk.no", "bruker@agk.no", enabled, "Member",
            null, null, DateTimeOffset.UtcNow.AddYears(-1), [], null, null);

    // Ny brukeroversikt i Graph, og tom cache slik at den hentes på nytt
    private void UseEntraUsers(params M365User[] users)
    {
        factory.Graph.UserOverview = new M365UserOverview(users, null, null, null, DateTimeOffset.UtcNow);
        var cache = factory.Services.GetRequiredService<IMemoryCache>();
        cache.Remove(EntraUserService.CacheKey(TenantA, "users"));
        cache.Remove(EntraUserService.CacheKey(TenantB, "users"));
    }

    private async Task<Employee> AddEmployeeAsync(string tenantId, string? entraObjectId)
    {
        await using var db = factory.CreateDbContext(tenantId);
        var employee = new Employee
        {
            FirstName = "Test",
            LastName = Guid.NewGuid().ToString("N")[..8],
            Email = $"{Guid.NewGuid():N}@agk.no",
            EntraObjectId = entraObjectId,
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return employee;
    }

    private async Task<EmployeeEntraStatusesDto> StatusesAsync(string tenantId = TenantA)
    {
        var response = await factory.CreateClientInTenant(tenantId, AppRoles.HelpdeskAgent)
            .GetAsync("/api/employees/entra-status");
        Assert.True(response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<EmployeeEntraStatusesDto>(Json))!;
    }

    [Fact]
    public async Task Each_employee_gets_the_status_of_its_entra_account()
    {
        var enabledId = Guid.NewGuid().ToString();
        var disabledId = Guid.NewGuid().ToString();

        var enabled = await AddEmployeeAsync(TenantA, enabledId);
        var disabled = await AddEmployeeAsync(TenantA, disabledId.ToUpperInvariant());
        var missing = await AddEmployeeAsync(TenantA, Guid.NewGuid().ToString());
        var notLinked = await AddEmployeeAsync(TenantA, null);

        UseEntraUsers(EntraUser(enabledId, true), EntraUser(disabledId, false));

        var result = await StatusesAsync();
        var byId = result.Statuses.ToDictionary(s => s.EmployeeId, s => s.Status);

        Assert.Null(result.UnavailableReason);
        Assert.Equal(EntraAccountStatuses.Enabled, byId[enabled.Id]);
        Assert.Equal(EntraAccountStatuses.Disabled, byId[disabled.Id]);
        Assert.Equal(EntraAccountStatuses.Missing, byId[missing.Id]);
        Assert.Equal(EntraAccountStatuses.NotLinked, byId[notLinked.Id]);
    }

    [Fact]
    public async Task Graph_failure_gives_unknown_and_a_reason()
    {
        var linked = await AddEmployeeAsync(TenantA, Guid.NewGuid().ToString());
        var notLinked = await AddEmployeeAsync(TenantA, null);

        UseEntraUsers();
        factory.Graph.Failure = new GraphUnavailableException("Appen mangler rettigheten User.Read.All.", 403);
        try
        {
            var result = await StatusesAsync();
            var byId = result.Statuses.ToDictionary(s => s.EmployeeId, s => s.Status);

            Assert.Contains("User.Read.All", result.UnavailableReason);
            Assert.Equal(EntraAccountStatuses.Unknown, byId[linked.Id]);
            Assert.Equal(EntraAccountStatuses.NotLinked, byId[notLinked.Id]);
        }
        finally
        {
            factory.Graph.Failure = null;
        }
    }

    [Fact]
    public async Task Only_employees_in_own_tenant_are_returned()
    {
        var other = await AddEmployeeAsync(TenantB, Guid.NewGuid().ToString());
        UseEntraUsers();

        var result = await StatusesAsync(TenantA);

        Assert.DoesNotContain(result.Statuses, s => s.EmployeeId == other.Id);
    }

    [Fact]
    public async Task Employee_without_helpdesk_role_is_forbidden()
    {
        var response = await factory.CreateClientWithRoles(AppRoles.EmployeeSyncAdmin)
            .GetAsync("/api/employees/entra-status");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}

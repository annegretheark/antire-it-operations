using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using backend.Controllers;
using backend.Models;
using backend.Services;

namespace backend.Tests;

// Offboarding: «Deaktiver konto i Entra» fra oppgaven, med personens egne rettigheter
// (falsk Graph; DeniedUsers spiller at Entra sier nei)
public class LifecycleDisableAccountTests(SqliteWebApplicationFactory factory)
    : IClassFixture<SqliteWebApplicationFactory>
{
    private const string TenantA = TestAuthHandler.AllowedTenantId;
    private const string UserToken = "personens-token";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Admin()
    {
        var client = factory.CreateClientWithRoles(AppRoles.HelpdeskAdmin);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", UserToken);
        return client;
    }

    private HttpClient Agent() => factory.CreateClientWithRoles(AppRoles.HelpdeskAgent);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    // Brukerne i Entra, og tom cache slik at de hentes på nytt
    private void UseEntraUsers(params string[] enabledIds)
    {
        factory.Graph.UserOverview = new M365UserOverview(
            enabledIds.Select(id => new M365User(id, "Per Slutt", "per@agk.no", "per@agk.no", true, "Member",
                null, null, DateTimeOffset.UtcNow.AddYears(-1), [], null, null)).ToList(),
            null, null, null, DateTimeOffset.UtcNow);
        factory.Services.GetRequiredService<IMemoryCache>().Remove(EntraUserService.CacheKey(TenantA, "users"));
    }

    // Ansatt (koblet til Entra hvis entraObjectId er satt) med en prosess av typen
    private async Task<(Employee Employee, int ProcessId)> StartAsync(string? entraObjectId, string type = "offboarding")
    {
        Employee employee;
        await using (var db = factory.CreateDbContext(TenantA))
        {
            employee = new Employee
            {
                FirstName = "Per",
                LastName = "Slutt",
                Email = $"{Guid.NewGuid():N}@agk.no",
                StartDate = DateTime.UtcNow.Date.AddYears(-2),
                EndDate = DateTime.UtcNow.Date,
                EntraObjectId = entraObjectId,
            };
            db.Employees.Add(employee);
            await db.SaveChangesAsync();
        }

        var process = await ReadAsync<LifecycleSummaryDto>(await Agent().PostAsJsonAsync(
            "/api/lifecycle", new { employeeId = employee.Id, type }));

        return (employee, process.Id);
    }

    private async Task<LifecycleTask> ActionTaskAsync(int processId)
    {
        await using var db = factory.CreateDbContext(TenantA);
        return await db.LifecycleTasks.SingleAsync(t =>
            t.ProcessId == processId && t.Action == LifecycleTaskActions.DisableEntraAccount);
    }

    [Fact]
    public async Task Default_offboarding_template_has_the_disable_action()
    {
        var templates = await ReadAsync<List<LifecycleTemplateTask>>(await Admin().GetAsync("/api/lifecycletemplates"));

        var task = Assert.Single(templates, t => t.Action == LifecycleTaskActions.DisableEntraAccount);
        Assert.Equal(LifecycleTypes.Offboarding, task.Type);
    }

    [Fact]
    public async Task Admin_disables_the_account_and_the_task_is_ticked_off()
    {
        var objectId = Guid.NewGuid().ToString();
        UseEntraUsers(objectId);
        var (_, processId) = await StartAsync(objectId);

        var before = await ReadAsync<LifecycleDetailsDto>(await Agent().GetAsync($"/api/lifecycle/{processId}"));
        Assert.Equal(EntraAccountStatuses.Enabled, before.EntraStatus);

        var details = await ReadAsync<LifecycleDetailsDto>(
            await Admin().PostAsync($"/api/lifecycle/{processId}/disable-account", null));

        Assert.Contains(factory.Graph.UserChanges, c =>
            c.UserId == objectId && c.Action == "disable" && c.UserToken == UserToken);

        var task = details.Tasks.Single(t => t.Action == LifecycleTaskActions.DisableEntraAccount);
        Assert.NotNull(task.CompletedAt);
        Assert.Equal("test-user", task.CompletedByName);
        Assert.Contains("Deaktivert i Entra", task.Note);

        await using var db = factory.CreateDbContext(TenantA);
        Assert.True(await db.AuditEvents.AnyAsync(e =>
            e.EntityType == "EntraUser" && e.EntityId == objectId && e.Action == AuditActions.UserDisabled));
    }

    [Fact]
    public async Task Task_is_not_ticked_off_when_entra_says_no()
    {
        var objectId = Guid.NewGuid().ToString();
        UseEntraUsers(objectId);
        factory.Graph.DeniedUsers.Add(objectId);
        var (_, processId) = await StartAsync(objectId);

        var response = await Admin().PostAsync($"/api/lifecycle/{processId}/disable-account", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("ikke lov", await response.Content.ReadAsStringAsync());
        Assert.Null((await ActionTaskAsync(processId)).CompletedAt);
    }

    [Fact]
    public async Task Employee_without_entra_account_is_rejected()
    {
        UseEntraUsers();
        var (_, processId) = await StartAsync(entraObjectId: null);

        var details = await ReadAsync<LifecycleDetailsDto>(await Agent().GetAsync($"/api/lifecycle/{processId}"));
        Assert.Equal(EntraAccountStatuses.NotLinked, details.EntraStatus);

        var response = await Admin().PostAsync($"/api/lifecycle/{processId}/disable-account", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null((await ActionTaskAsync(processId)).CompletedAt);
    }

    [Fact]
    public async Task Onboarding_and_cancelled_processes_are_rejected()
    {
        var objectId = Guid.NewGuid().ToString();
        UseEntraUsers(objectId);

        var (_, onboardingId) = await StartAsync(Guid.NewGuid().ToString(), "onboarding");
        Assert.Equal(HttpStatusCode.NotFound,
            (await Admin().PostAsync($"/api/lifecycle/{onboardingId}/disable-account", null)).StatusCode);

        var (_, offboardingId) = await StartAsync(objectId);
        Assert.Equal(HttpStatusCode.NoContent,
            (await Agent().PostAsync($"/api/lifecycle/{offboardingId}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Admin().PostAsync($"/api/lifecycle/{offboardingId}/disable-account", null)).StatusCode);

        Assert.DoesNotContain(factory.Graph.UserChanges, c => c.UserId == objectId);
    }

    [Fact]
    public async Task Cannot_disable_your_own_account()
    {
        UseEntraUsers(TestAuthHandler.TestObjectId);
        var (_, processId) = await StartAsync(TestAuthHandler.TestObjectId);

        var response = await Admin().PostAsync($"/api/lifecycle/{processId}/disable-account", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(factory.Graph.UserChanges, c => c.UserId == TestAuthHandler.TestObjectId);
    }

    [Fact]
    public async Task Agent_cannot_disable_the_account()
    {
        var objectId = Guid.NewGuid().ToString();
        UseEntraUsers(objectId);
        var (_, processId) = await StartAsync(objectId);

        var response = await Agent().PostAsync($"/api/lifecycle/{processId}/disable-account", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("onboarding", "disable_entra_account", HttpStatusCode.BadRequest)]
    [InlineData("offboarding", "noe_annet", HttpStatusCode.BadRequest)]
    [InlineData("offboarding", "disable_entra_account", HttpStatusCode.Created)]
    public async Task Template_action_is_validated(string type, string action, HttpStatusCode expected)
    {
        var response = await Admin().PostAsJsonAsync("/api/lifecycletemplates", new
        {
            type, title = $"Test {Guid.NewGuid():N}", offsetDays = 0, sortOrder = 99, action,
            // Inaktiv, så malen ikke gir ekstra oppgaver i andre tester
            isActive = false,
        });

        Assert.Equal(expected, response.StatusCode);
    }
}

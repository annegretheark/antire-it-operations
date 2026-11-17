using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using backend.Controllers;
using backend.Models;
using backend.Services;

namespace backend.Tests;

// Onboarding og offboarding: automatisk start, avkrysning, utstyr og tilganger
public class LifecycleTests(SqliteWebApplicationFactory factory)
    : IClassFixture<SqliteWebApplicationFactory>
{
    private const string TenantA = TestAuthHandler.AllowedTenantId;
    private const string TenantB = TestAuthHandler.OtherAllowedTenantId;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Agent(string tenantId = TenantA) =>
        factory.CreateClientInTenant(tenantId, AppRoles.HelpdeskAgent);

    private HttpClient EmployeeAdmin() => factory.CreateClientWithRoles(AppRoles.EmployeeSyncAdmin);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private async Task<Employee> CreateEmployeeAsync(DateTime startDate, string? objectId = null)
    {
        var employee = await ReadAsync<Employee>(await EmployeeAdmin().PostAsJsonAsync("/api/employees", new
        {
            firstName = "Nils", lastName = "Ny", email = $"{Guid.NewGuid():N}@t.no",
            department = "IT", jobTitle = "Konsulent", startDate = startDate.ToString("yyyy-MM-dd"),
        }));

        if (objectId != null)
        {
            await using var db = factory.CreateDbContext(TenantA);
            (await db.Employees.SingleAsync(e => e.Id == employee.Id)).EntraObjectId = objectId;
            await db.SaveChangesAsync();
        }

        return employee;
    }

    private async Task<List<LifecycleSummaryDto>> ProcessesAsync(int employeeId) =>
        (await ReadAsync<List<LifecycleSummaryDto>>(await Agent().GetAsync("/api/lifecycle")))
            .Where(p => p.EmployeeId == employeeId).ToList();

    private async Task<LifecycleDetailsDto> DetailsAsync(int id) =>
        await ReadAsync<LifecycleDetailsDto>(await Agent().GetAsync($"/api/lifecycle/{id}"));

    private async Task TerminateAsync(int employeeId, DateTime endDate) =>
        Assert.Equal(HttpStatusCode.OK, (await EmployeeAdmin().PatchAsJsonAsync(
            $"/api/employees/{employeeId}/terminate", new { endDate = endDate.ToString("yyyy-MM-dd") })).StatusCode);

    [Fact]
    public async Task New_employee_with_future_start_gets_onboarding_with_deadlines()
    {
        var start = DateTime.UtcNow.Date.AddDays(14);
        var employee = await CreateEmployeeAsync(start);

        var process = Assert.Single(await ProcessesAsync(employee.Id));
        Assert.Equal(LifecycleTypes.Onboarding, process.Type);
        Assert.Equal(LifecycleStatuses.Active, process.Status);
        Assert.Equal(start, process.ReferenceDate.Date);

        var details = await DetailsAsync(process.Id);
        var account = details.Tasks.Single(t => t.Title == "Opprett brukerkonto i Entra ID");
        Assert.Equal(start.AddDays(-5), account.DueDate.Date);
        Assert.Equal(LifecycleService.DefaultTemplates().Count(t => t.Type == LifecycleTypes.Onboarding), details.Tasks.Count);
    }

    [Fact]
    public async Task Employee_who_already_started_gets_no_onboarding()
    {
        var employee = await CreateEmployeeAsync(DateTime.UtcNow.Date.AddYears(-2));

        Assert.Empty(await ProcessesAsync(employee.Id));
    }

    [Fact]
    public async Task Checking_all_tasks_completes_the_process_and_unchecking_reopens_it()
    {
        var employee = await CreateEmployeeAsync(DateTime.UtcNow.Date.AddDays(3));
        var process = Assert.Single(await ProcessesAsync(employee.Id));
        var tasks = (await DetailsAsync(process.Id)).Tasks;

        foreach (var task in tasks)
        {
            var done = await ReadAsync<LifecycleTask>(await Agent().PatchAsJsonAsync(
                $"/api/lifecycle/tasks/{task.Id}", new { done = true, note = "OK" }));
            Assert.NotNull(done.CompletedAt);
            Assert.Equal("test-user", done.CompletedByName);
        }

        var completed = (await DetailsAsync(process.Id)).Process;
        Assert.Equal(LifecycleStatuses.Completed, completed.Status);
        Assert.Equal(tasks.Count, completed.DoneCount);

        await ReadAsync<LifecycleTask>(await Agent().PatchAsJsonAsync(
            $"/api/lifecycle/tasks/{tasks[0].Id}", new { done = false }));
        Assert.Equal(LifecycleStatuses.Active, (await DetailsAsync(process.Id)).Process.Status);
    }

    [Fact]
    public async Task Termination_starts_offboarding_with_assets_and_revocable_access()
    {
        var objectId = Guid.NewGuid().ToString();
        var groupId = Guid.NewGuid().ToString();
        var employee = await CreateEmployeeAsync(DateTime.UtcNow.Date.AddYears(-1), objectId);

        int requestId;
        await using (var db = factory.CreateDbContext(TenantA))
        {
            db.Assets.Add(new Asset { AssetTag = $"OFF-{Guid.NewGuid():N}"[..14], Type = "laptop",
                Status = AssetStatuses.InUse, AssignedToEmployeeId = employee.Id });

            var system = new ServiceCatalogItem
            {
                Name = $"Sys {Guid.NewGuid():N}"[..30], Category = "Tilgangsbestilling",
                Type = ServiceCatalogTypes.Access, EntraGroupId = groupId,
            };
            db.ServiceCatalogItems.Add(system);
            await db.SaveChangesAsync();

            var request = new AccessRequest
            {
                EmployeeId = employee.Id, EmployeeName = "Nils Ny", SystemName = system.Name, Reason = "Jobb",
                ManagerName = "Per", ServiceCatalogItemId = system.Id, Status = AccessRequestStatuses.Completed,
            };
            db.AccessRequests.Add(request);
            await db.SaveChangesAsync();
            requestId = request.Id;
        }

        await TerminateAsync(employee.Id, DateTime.UtcNow.Date.AddDays(10));

        var offboarding = Assert.Single(await ProcessesAsync(employee.Id), p => p.Type == LifecycleTypes.Offboarding);
        var details = await DetailsAsync(offboarding.Id);
        Assert.Single(details.Assets);
        var access = Assert.Single(details.Accesses);
        Assert.Equal(groupId, access.GroupId);

        var revoked = await ReadAsync<LifecycleAccessDto>(
            await Agent().PostAsync($"/api/lifecycle/{offboarding.Id}/revoke/{requestId}", null));

        Assert.Equal(AccessRequestStatuses.Revoked, revoked.Status);
        Assert.Contains(factory.Graph.RemovedMembers, m => m == (groupId, objectId));

        await using var check = factory.CreateDbContext(TenantA);
        Assert.Contains(await check.AuditEvents.Where(e => e.EntityType == nameof(AccessRequest) &&
            e.EntityId == requestId.ToString()).ToListAsync(), e => e.Action == AuditActions.AccessRevoked);

        // Samme tilgang kan ikke fjernes to ganger
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Agent().PostAsync($"/api/lifecycle/{offboarding.Id}/revoke/{requestId}", null)).StatusCode);
    }

    [Fact]
    public async Task Manual_start_reuses_active_process_and_cancel_ends_it()
    {
        var employee = await CreateEmployeeAsync(DateTime.UtcNow.Date.AddYears(-1));

        var first = await ReadAsync<LifecycleSummaryDto>(await Agent().PostAsJsonAsync("/api/lifecycle", new
        {
            employeeId = employee.Id, type = "offboarding",
        }));
        var second = await ReadAsync<LifecycleSummaryDto>(await Agent().PostAsJsonAsync("/api/lifecycle", new
        {
            employeeId = employee.Id, type = "offboarding",
        }));
        Assert.Equal(first.Id, second.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await Agent().PostAsync($"/api/lifecycle/{first.Id}/cancel", null)).StatusCode);
        Assert.Equal(LifecycleStatuses.Cancelled, (await DetailsAsync(first.Id)).Process.Status);

        var task = (await DetailsAsync(first.Id)).Tasks[0];
        Assert.Equal(HttpStatusCode.BadRequest, (await Agent().PatchAsJsonAsync(
            $"/api/lifecycle/tasks/{task.Id}", new { done = true })).StatusCode);
    }

    [Fact]
    public async Task Templates_can_be_changed_and_apply_to_new_processes()
    {
        var admin = factory.CreateClientInTenant(TenantB, AppRoles.HelpdeskAdmin);

        var created = await ReadAsync<LifecycleTemplateTask>(await admin.PostAsJsonAsync("/api/lifecycletemplates", new
        {
            type = "onboarding", title = "Bestill adgangskort", offsetDays = -7, sortOrder = 0,
        }));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/lifecycletemplates", new
        {
            type = "ukjent", title = "X", offsetDays = 0, sortOrder = 0,
        })).StatusCode);

        await using (var db = factory.CreateDbContext(TenantB))
        {
            db.Employees.Add(new Employee { FirstName = "B", LastName = "Tenant", Email = "b@t.no", StartDate = DateTime.UtcNow.AddDays(20) });
            await db.SaveChangesAsync();
        }

        int employeeId;
        await using (var db = factory.CreateDbContext(TenantB))
            employeeId = (await db.Employees.SingleAsync(e => e.Email == "b@t.no")).Id;

        var process = await ReadAsync<LifecycleSummaryDto>(await Agent(TenantB).PostAsJsonAsync("/api/lifecycle", new
        {
            employeeId, type = "onboarding",
        }));
        var details = await ReadAsync<LifecycleDetailsDto>(await Agent(TenantB).GetAsync($"/api/lifecycle/{process.Id}"));
        Assert.Contains(details.Tasks, t => t.Title == created.Title);

        // Prosesser i tenant B er ikke synlige fra tenant A
        Assert.Equal(HttpStatusCode.NotFound, (await Agent().GetAsync($"/api/lifecycle/{process.Id}")).StatusCode);
    }

    [Fact]
    public async Task Sync_starts_onboarding_for_new_employees_but_not_on_first_sync()
    {
        var userId = Guid.NewGuid().ToString();
        factory.Graph.EntraUsers[TenantA] =
        [
            new() { Id = userId, GivenName = "Synket", Surname = "Person", Mail = $"{userId}@t.no",
                Department = "IT", JobTitle = "Konsulent" },
        ];

        await CreateEmployeeAsync(DateTime.UtcNow.Date.AddYears(-3)); // tenanten har ansatte fra før
        Assert.Equal(HttpStatusCode.OK, (await EmployeeAdmin().PostAsync("/api/graph/sync-employees", null)).StatusCode);

        await using var db = factory.CreateDbContext(TenantA);
        var synced = await db.Employees.SingleAsync(e => e.EntraObjectId == userId);
        Assert.True(await db.LifecycleProcesses.AnyAsync(p => p.EmployeeId == synced.Id && p.Type == LifecycleTypes.Onboarding));
    }
}

// Første synk i en tom tenant: alle er «nye», men ingen skal få onboarding
public class FirstSyncTests(SqliteWebApplicationFactory factory)
    : IClassFixture<SqliteWebApplicationFactory>
{
    [Fact]
    public async Task First_sync_does_not_start_onboarding()
    {
        var tenant = TestAuthHandler.AllowedTenantId;
        factory.Graph.EntraUsers[tenant] =
        [
            new() { Id = Guid.NewGuid().ToString(), GivenName = "A", Surname = "En", Mail = "a@t.no", Department = "IT", JobTitle = "X" },
            new() { Id = Guid.NewGuid().ToString(), GivenName = "B", Surname = "To", Mail = "b@t.no", Department = "IT", JobTitle = "X" },
        ];

        var response = await factory.CreateClientWithRoles(AppRoles.EmployeeSyncAdmin)
            .PostAsync("/api/graph/sync-employees", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var db = factory.CreateDbContext(tenant);
        Assert.Equal(2, await db.Employees.CountAsync());
        Assert.False(await db.LifecycleProcesses.AnyAsync());
    }
}

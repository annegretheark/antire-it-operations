using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using backend.Controllers;
using backend.Models;
using backend.Services;

namespace backend.Tests;

// Tilgang via gruppe (2.4): godkjent bestilling legger den ansatte i Entra-gruppen
public class AccessGrantTests(SqliteWebApplicationFactory factory)
    : IClassFixture<SqliteWebApplicationFactory>
{
    private const string TenantA = TestAuthHandler.AllowedTenantId;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Approver() => factory.CreateClientWithRoles(AppRoles.Approver);
    private HttpClient Agent() => factory.CreateClientWithRoles(AppRoles.HelpdeskAgent);
    private HttpClient User() => factory.CreateClientWithRoles();

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Json))!;

    private async Task<Employee> EmployeeAsync(string? objectId)
    {
        await using var db = factory.CreateDbContext(TenantA);
        var employee = new Employee
        {
            FirstName = "Nora", LastName = "Tilgang", Email = $"{Guid.NewGuid():N}@t.no", EntraObjectId = objectId,
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return employee;
    }

    private async Task<ServiceCatalogItem> SystemAsync(string? groupId, bool requiresApproval = true)
    {
        var response = await factory.CreateClientWithRoles(AppRoles.HelpdeskAdmin).PostAsJsonAsync(
            "/api/servicecatalog", new
            {
                name = $"System {Guid.NewGuid():N}"[..30],
                category = "Tilgangsbestilling",
                type = "access",
                requiresApproval,
                entraGroupId = groupId,
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<ServiceCatalogItem>(response);
    }

    private async Task<AccessRequest> RequestAsync(HttpClient client, int employeeId, int systemId)
    {
        var response = await client.PostAsJsonAsync("/api/accessrequests", new
        {
            employeeId, serviceCatalogItemId = systemId, reason = "Trenger tilgang", managerName = "Per",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<AccessRequest>(response);
    }

    private async Task<AccessRequestResult> ApproveAsync(int id, string status = "Godkjent")
    {
        var response = await Approver().PatchAsJsonAsync($"/api/accessrequests/{id}/approval", new { status });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<AccessRequestResult>(response);
    }

    private async Task<List<AuditEvent>> AuditAsync(int requestId)
    {
        await using var db = factory.CreateDbContext(TenantA);
        return await db.AuditEvents
            .Where(e => e.EntityType == nameof(AccessRequest) && e.EntityId == requestId.ToString())
            .OrderBy(e => e.Id)
            .ToListAsync();
    }

    [Fact]
    public async Task Approval_adds_employee_to_group_and_completes_request()
    {
        var objectId = Guid.NewGuid().ToString();
        var groupId = Guid.NewGuid().ToString();
        var employee = await EmployeeAsync(objectId);
        var system = await SystemAsync(groupId);
        var request = await RequestAsync(User(), employee.Id, system.Id);

        Assert.Equal(AccessRequestStatuses.Pending, request.Status);
        Assert.Equal("Nora Tilgang", request.EmployeeName);

        var result = await ApproveAsync(request.Id);

        Assert.True(result.Grant!.Granted);
        Assert.Equal(AccessRequestStatuses.Completed, result.Request.Status);
        Assert.Contains(factory.Graph.AddedMembers, m => m == (TenantA, groupId, objectId));

        var audit = await AuditAsync(request.Id);
        Assert.Contains(audit, e => e.Action == AuditActions.Approved);
        Assert.Contains(audit, e => e.Action == AuditActions.Completed);
        var granted = Assert.Single(audit, e => e.Action == AuditActions.AccessGranted);
        Assert.Contains(groupId, granted.Changes);
    }

    [Fact]
    public async Task Graph_failure_keeps_approval_and_can_be_retried()
    {
        var objectId = Guid.NewGuid().ToString();
        var groupId = Guid.NewGuid().ToString();
        var employee = await EmployeeAsync(objectId);
        var system = await SystemAsync(groupId);
        var request = await RequestAsync(User(), employee.Id, system.Id);

        factory.Graph.Failure = new GraphUnavailableException(
            "Appen mangler rettigheten GroupMember.ReadWrite.All i Entra ID (admin-samtykke).", 403);
        AccessRequestResult result;
        try
        {
            result = await ApproveAsync(request.Id);
        }
        finally
        {
            factory.Graph.Failure = null;
        }

        Assert.False(result.Grant!.Granted);
        Assert.Contains("GroupMember.ReadWrite.All", result.Grant.Message);
        Assert.Equal(AccessRequestStatuses.Approved, result.Request.Status);
        Assert.Contains(await AuditAsync(request.Id), e => e.Action == AuditActions.AccessGrantFailed);

        // Nytt forsøk når rettigheten er på plass
        var retry = await ReadAsync<AccessRequestResult>(
            await Agent().PostAsync($"/api/accessrequests/{request.Id}/grant", null));
        Assert.True(retry.Grant!.Granted);
        Assert.Equal(AccessRequestStatuses.Completed, retry.Request.Status);
    }

    [Fact]
    public async Task Already_member_counts_as_granted()
    {
        var objectId = Guid.NewGuid().ToString();
        var groupId = Guid.NewGuid().ToString();
        factory.Graph.ExistingMemberships.Add((groupId, objectId));

        var request = await RequestAsync(User(), (await EmployeeAsync(objectId)).Id, (await SystemAsync(groupId)).Id);
        var result = await ApproveAsync(request.Id);

        Assert.True(result.Grant!.Granted);
        Assert.Contains("allerede medlem", result.Grant.Message);
        Assert.Equal(AccessRequestStatuses.Completed, result.Request.Status);
    }

    [Fact]
    public async Task System_without_group_is_completed_manually()
    {
        var request = await RequestAsync(User(), (await EmployeeAsync(Guid.NewGuid().ToString())).Id,
            (await SystemAsync(groupId: null)).Id);

        var result = await ApproveAsync(request.Id);

        Assert.Null(result.Grant);
        Assert.Equal(AccessRequestStatuses.Approved, result.Request.Status);

        var completed = await Agent().PatchAsync($"/api/accessrequests/{request.Id}/complete", null);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
    }

    [Fact]
    public async Task Employee_without_entra_user_is_not_granted()
    {
        var request = await RequestAsync(User(), (await EmployeeAsync(objectId: null)).Id,
            (await SystemAsync(Guid.NewGuid().ToString())).Id);

        var result = await ApproveAsync(request.Id);

        Assert.False(result.Grant!.Granted);
        Assert.Contains("ikke koblet til en bruker i Entra ID", result.Grant.Message);
        Assert.Equal(AccessRequestStatuses.Approved, result.Request.Status);
    }

    [Fact]
    public async Task Rejection_does_not_touch_entra()
    {
        var groupId = Guid.NewGuid().ToString();
        var request = await RequestAsync(User(), (await EmployeeAsync(Guid.NewGuid().ToString())).Id,
            (await SystemAsync(groupId)).Id);

        var result = await ApproveAsync(request.Id, "Avslått");

        Assert.Null(result.Grant);
        Assert.Equal(AccessRequestStatuses.Rejected, result.Request.Status);
        Assert.DoesNotContain(factory.Graph.AddedMembers, m => m.GroupId == groupId);
    }

    [Fact]
    public async Task System_without_approval_is_granted_at_once_only_for_yourself()
    {
        var groupId = Guid.NewGuid().ToString();
        var system = await SystemAsync(groupId, requiresApproval: false);

        // Den innloggede testbrukeren som ansatt
        var me = await EmployeeAsync(TestAuthHandler.TestObjectId);
        var mine = await RequestAsync(User(), me.Id, system.Id);
        Assert.Equal(AccessRequestStatuses.Completed, mine.Status);
        Assert.Contains(factory.Graph.AddedMembers, m => m == (TenantA, groupId, TestAuthHandler.TestObjectId));

        // For en annen må den fortsatt godkjennes
        var other = await EmployeeAsync(Guid.NewGuid().ToString());
        var theirs = await RequestAsync(User(), other.Id, system.Id);
        Assert.Equal(AccessRequestStatuses.Pending, theirs.Status);
    }

    [Fact]
    public async Task Request_requires_active_employee()
    {
        await using (var db = factory.CreateDbContext(TenantA))
        {
            db.Employees.Add(new Employee
            {
                FirstName = "Siri", LastName = "Sluttet", Email = "siri@t.no", Status = "Sluttet",
            });
            await db.SaveChangesAsync();
        }

        int terminatedId;
        await using (var db = factory.CreateDbContext(TenantA))
            terminatedId = (await db.Employees.FirstAsync(e => e.Email == "siri@t.no")).Id;

        var system = await SystemAsync(groupId: null);

        foreach (int? employeeId in new int?[] { null, terminatedId })
        {
            var response = await User().PostAsJsonAsync("/api/accessrequests", new
            {
                employeeId, serviceCatalogItemId = system.Id, reason = "Trenger tilgang", managerName = "Per",
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }
}

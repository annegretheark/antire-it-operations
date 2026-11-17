using System.Net;
using System.Net.Http.Json;
using System.Text;
using backend.Services;

namespace backend.Tests;

// Sikrer rollematrisen (RBAC). Testene stopper i tilgangskontrollen eller i
// validering før databasen tas i bruk, så de trenger ingen database.
//
// | Endepunkter               | Admin | Agent | Approver | EmployeeSync | Ingen rolle |
// |---------------------------|-------|-------|----------|--------------|-------------|
// | Konfigurasjon             |  ja   |  nei  |   nei    |     nei      |     nei     |
// | Behandle saker/fullføre   |  ja   |  ja   |   nei    |     nei      |     nei     |
// | Godkjenne tilgang         |  ja   |  nei  |   ja     |     nei      |     nei     |
// | Ansatte og synk           |  nei  |  nei  |   nei    |     ja       |     nei     |
public class AuthorizationTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    // Bare Helpdesk.Admin
    public static TheoryData<string, string> ConfigurationEndpoints => new()
    {
        { "GET", "/api/ticketcategorygroups" },
        { "POST", "/api/ticketcategorygroups" },
        { "DELETE", "/api/ticketcategorygroups/1" },
        { "GET", "/api/audit" },
        { "POST", "/api/servicecatalog" },
        { "PUT", "/api/servicecatalog/1" },
        { "GET", "/api/employeeresponsibilities" },
        { "POST", "/api/employeeresponsibilities" },
        { "PATCH", "/api/employeeresponsibilities/1" },
        { "DELETE", "/api/employeeresponsibilities/1" },
        { "GET", "/api/slatargets" },
        { "PUT", "/api/slatargets/normal" },
        { "GET", "/api/m365/users" },
        { "GET", "/api/m365/groups" },
        { "GET", "/api/m365/groups/00000000-0000-0000-0000-000000000001/members" },
        { "GET", "/api/azure/subscriptions" },
        { "POST", "/api/azure/subscriptions/sync" },
        { "GET", "/api/azure/resources" },
        { "POST", "/api/alerts/simulate" },
        { "GET", "/api/alertrules" },
        { "PUT", "/api/alertrules/defender" },
        { "GET", "/api/teams/settings" },
        { "PUT", "/api/teams/settings" },
        { "POST", "/api/teams/test" },
    };

    // Helpdesk.Agent eller Helpdesk.Admin
    public static TheoryData<string, string> AgentEndpoints => new()
    {
        { "PATCH", "/api/tickets/1/status" },
        { "PATCH", "/api/tickets/1/priority" },
        { "PATCH", "/api/tickets/1/assign" },
        { "PATCH", "/api/accessrequests/1/complete" },
        { "POST", "/api/assets" },
        { "PUT", "/api/assets/1" },
        { "PATCH", "/api/assets/1/assign" },
        { "POST", "/api/assets/import" },
        { "POST", "/api/accessrequests/1/grant" },
        { "GET", "/api/alerts" },
        { "GET", "/api/alerts/sources" },
    };

    // Approver eller Helpdesk.Admin
    public static TheoryData<string, string> ApproverEndpoints => new()
    {
        { "PATCH", "/api/accessrequests/1/approval" },
    };

    // Bare EmployeeSync.Admin
    public static TheoryData<string, string> EmployeeAdminEndpoints => new()
    {
        { "POST", "/api/employees" },
        { "PUT", "/api/employees/1" },
        { "PATCH", "/api/employees/1/terminate" },
        { "GET", "/api/graph/employees" },
        { "POST", "/api/graph/sync-employees" },
        { "GET", "/api/graph/employees/abc/groups" },
    };

    [Theory]
    [InlineData("GET", "/api/tickets")]
    [InlineData("GET", "/api/employees")]
    [InlineData("GET", "/api/accessrequests")]
    [InlineData("GET", "/api/tickets/me")]
    [InlineData("GET", "/api/tickets/1/history")]
    [InlineData("GET", "/api/audit")]
    [InlineData("GET", "/api/servicecatalog")]
    [InlineData("GET", "/api/assets")]
    public async Task Anonymous_requests_are_rejected(string method, string path)
    {
        var client = factory.CreateClient();

        var response = await client.SendAsync(Request(method, path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ConfigurationEndpoints))]
    [MemberData(nameof(AgentEndpoints))]
    [MemberData(nameof(ApproverEndpoints))]
    [MemberData(nameof(EmployeeAdminEndpoints))]
    public Task User_without_roles_is_forbidden(string method, string path) =>
        AssertForbidden(method, path);

    [Theory]
    [MemberData(nameof(ConfigurationEndpoints))]
    [MemberData(nameof(ApproverEndpoints))]
    [MemberData(nameof(EmployeeAdminEndpoints))]
    public Task Agent_is_forbidden_outside_ticket_handling(string method, string path) =>
        AssertForbidden(method, path, AppRoles.HelpdeskAgent);

    [Theory]
    [MemberData(nameof(ConfigurationEndpoints))]
    [MemberData(nameof(AgentEndpoints))]
    [MemberData(nameof(EmployeeAdminEndpoints))]
    public Task Approver_is_forbidden_outside_approval(string method, string path) =>
        AssertForbidden(method, path, AppRoles.Approver);

    [Theory]
    [MemberData(nameof(ConfigurationEndpoints))]
    [MemberData(nameof(AgentEndpoints))]
    [MemberData(nameof(ApproverEndpoints))]
    public Task EmployeeSync_admin_is_forbidden_in_helpdesk(string method, string path) =>
        AssertForbidden(method, path, AppRoles.EmployeeSyncAdmin);

    [Theory]
    [MemberData(nameof(EmployeeAdminEndpoints))]
    public Task Helpdesk_admin_is_forbidden_for_employee_admin(string method, string path) =>
        AssertForbidden(method, path, AppRoles.HelpdeskAdmin);

    // Positive tester: rollen slipper gjennom tilgangskontrollen og stopper i
    // validering (400) fordi forespørselen er tom. Ingen databasekall.
    [Theory]
    [InlineData(AppRoles.HelpdeskAgent, "/api/tickets/1/status")]
    [InlineData(AppRoles.HelpdeskAgent, "/api/tickets/1/priority")]
    [InlineData(AppRoles.HelpdeskAdmin, "/api/tickets/1/status")]
    [InlineData(AppRoles.HelpdeskAdmin, "/api/tickets/1/priority")]
    [InlineData(AppRoles.Approver, "/api/accessrequests/1/approval")]
    [InlineData(AppRoles.HelpdeskAdmin, "/api/accessrequests/1/approval")]
    public async Task Allowed_role_passes_authorization(string role, string path)
    {
        var client = factory.CreateClientWithRoles(role);

        var response = await client.SendAsync(Request("PATCH", path));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Me_reports_no_roles_for_plain_user()
    {
        var client = factory.CreateClientWithRoles();

        var me = await client.GetFromJsonAsync<MeResponse>("/api/tickets/me");

        Assert.NotNull(me);
        Assert.Equal(TestAuthHandler.TestObjectId, me.ObjectId);
        Assert.False(me.IsHelpdeskAdmin);
        Assert.False(me.IsHelpdeskAgent);
        Assert.False(me.IsApprover);
        Assert.False(me.IsEmployeeAdmin);
    }

    [Fact]
    public async Task Me_reports_agent_without_admin_rights()
    {
        var client = factory.CreateClientWithRoles(AppRoles.HelpdeskAgent);

        var me = await client.GetFromJsonAsync<MeResponse>("/api/tickets/me");

        Assert.NotNull(me);
        Assert.True(me.IsHelpdeskAgent);
        Assert.False(me.IsHelpdeskAdmin);
        Assert.False(me.IsApprover);
    }

    [Fact]
    public async Task Me_reports_admin_as_agent_and_approver()
    {
        var client = factory.CreateClientWithRoles(
            AppRoles.HelpdeskAdmin, AppRoles.EmployeeSyncAdmin);

        var me = await client.GetFromJsonAsync<MeResponse>("/api/tickets/me");

        Assert.NotNull(me);
        Assert.True(me.IsHelpdeskAdmin);
        Assert.True(me.IsHelpdeskAgent);
        Assert.True(me.IsApprover);
        Assert.True(me.IsEmployeeAdmin);
    }

    private async Task AssertForbidden(string method, string path, params string[] roles)
    {
        var client = factory.CreateClientWithRoles(roles);

        var response = await client.SendAsync(Request(method, path));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static HttpRequestMessage Request(string method, string path) =>
        new(new HttpMethod(method), path)
        {
            Content = method is "GET" or "DELETE"
                ? null
                : new StringContent("{}", Encoding.UTF8, "application/json"),
        };

    private sealed record MeResponse(
        string? ObjectId,
        bool IsHelpdeskAdmin,
        bool IsHelpdeskAgent,
        bool IsApprover,
        bool IsEmployeeAdmin);
}

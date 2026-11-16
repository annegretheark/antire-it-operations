using System.Net;
using System.Net.Http.Json;
using System.Text;
using backend.Services;

namespace backend.Tests;

// Sikrer tilgangsreglene fra fase 0 (G1). Disse testene stopper i tilgangskontrollen,
// før kontrollerne og databasen tas i bruk, så de trenger ingen database.
public class AuthorizationTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    public static TheoryData<string, string> HelpdeskAdminEndpoints => new()
    {
        { "PATCH", "/api/tickets/1/status" },
        { "PATCH", "/api/tickets/1/priority" },
        { "PATCH", "/api/tickets/1/assign" },
        { "PATCH", "/api/accessrequests/1/approval" },
        { "PATCH", "/api/accessrequests/1/complete" },
        { "GET", "/api/ticketcategorygroups" },
        { "POST", "/api/ticketcategorygroups" },
        { "DELETE", "/api/ticketcategorygroups/1" },
    };

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
    public async Task Anonymous_requests_are_rejected(string method, string path)
    {
        var client = factory.CreateClient();

        var response = await client.SendAsync(Request(method, path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(HelpdeskAdminEndpoints))]
    [MemberData(nameof(EmployeeAdminEndpoints))]
    public async Task User_without_roles_is_forbidden(string method, string path)
    {
        var client = factory.CreateClientWithRoles();

        var response = await client.SendAsync(Request(method, path));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(HelpdeskAdminEndpoints))]
    public async Task EmployeeSync_admin_cannot_use_helpdesk_admin_endpoints(string method, string path)
    {
        var client = factory.CreateClientWithRoles(AppRoles.EmployeeSyncAdmin);

        var response = await client.SendAsync(Request(method, path));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(EmployeeAdminEndpoints))]
    public async Task Helpdesk_admin_cannot_use_employee_admin_endpoints(string method, string path)
    {
        var client = factory.CreateClientWithRoles(AppRoles.HelpdeskAdmin);

        var response = await client.SendAsync(Request(method, path));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Me_reports_no_admin_roles_for_plain_user()
    {
        var client = factory.CreateClientWithRoles();

        var me = await client.GetFromJsonAsync<MeResponse>("/api/tickets/me");

        Assert.NotNull(me);
        Assert.Equal(TestAuthHandler.TestObjectId, me.ObjectId);
        Assert.False(me.IsHelpdeskAdmin);
        Assert.False(me.IsEmployeeAdmin);
    }

    [Fact]
    public async Task Me_reports_admin_roles_from_token()
    {
        var client = factory.CreateClientWithRoles(
            AppRoles.HelpdeskAdmin, AppRoles.EmployeeSyncAdmin);

        var me = await client.GetFromJsonAsync<MeResponse>("/api/tickets/me");

        Assert.NotNull(me);
        Assert.True(me.IsHelpdeskAdmin);
        Assert.True(me.IsEmployeeAdmin);
    }

    private static HttpRequestMessage Request(string method, string path) =>
        new(new HttpMethod(method), path)
        {
            Content = method is "GET" or "DELETE"
                ? null
                : new StringContent("{}", Encoding.UTF8, "application/json"),
        };

    private sealed record MeResponse(
        bool? IsAuthenticated,
        string? ObjectId,
        string? Name,
        bool IsHelpdeskAdmin,
        bool IsEmployeeAdmin);
}

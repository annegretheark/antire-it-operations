using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using backend.Services;

namespace backend.Tests;

// Microsoft 365-oversiktene (brukere og grupper) med falsk Graph
public class M365Tests(SqliteWebApplicationFactory factory)
    : IClassFixture<SqliteWebApplicationFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Admin() => factory.CreateClientWithRoles(AppRoles.HelpdeskAdmin);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Json))!;

    [Fact]
    public async Task Users_show_missing_data_with_reason_and_are_cached()
    {
        factory.Graph.UserOverview = new M365UserOverview(
            [
                new M365User("u1", "Ola Nordmann", "ola@test.no", "ola@test.no", true, "Member",
                    "IT", "Konsulent", DateTimeOffset.UtcNow.AddYears(-1), ["ENTERPRISEPACK"], null, null),
                new M365User("u2", "Gjest", "gjest@ext.no", null, false, "Guest",
                    null, null, null, [], null, null),
            ],
            SignInUnavailableReason: "Krever rettigheten AuditLog.Read.All og lisensen Entra ID P1.",
            MfaUnavailableReason: "Krever rettigheten AuditLog.Read.All og lisensen Entra ID P1.",
            LicensesUnavailableReason: null,
            FetchedAt: DateTimeOffset.UtcNow);

        var before = factory.Graph.UserOverviewCalls;

        var first = await ReadAsync<M365UserOverview>(await Admin().GetAsync("/api/m365/users?refresh=true"));
        var cached = await ReadAsync<M365UserOverview>(await Admin().GetAsync("/api/m365/users"));

        Assert.Equal(2, first.Users.Count);
        Assert.Contains("Entra ID P1", first.SignInUnavailableReason);
        Assert.Equal(["ENTERPRISEPACK"], first.Users[0].Licenses);
        Assert.False(first.Users[1].AccountEnabled);
        Assert.Equal(first.FetchedAt, cached.FetchedAt);
        Assert.Equal(before + 1, factory.Graph.UserOverviewCalls);
    }

    private static M365User User(
        string id, string type, DateTimeOffset? created, DateTimeOffset? lastSuccess) =>
        new(id, id, $"{id}@test.no", null, true, type, null, null, created, [], lastSuccess, null, lastSuccess);

    [Fact]
    public async Task Inactive_users_are_filtered_on_type_and_last_successful_sign_in()
    {
        var cutoff = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var old = cutoff.AddYears(-2);

        factory.Graph.UserOverview = new M365UserOverview(
            [
                User("gjest-gammel", "Guest", old, cutoff.AddDays(-1)),
                User("gjest-aktiv", "Guest", old, cutoff.AddDays(1)),
                User("gjest-aldri", "Guest", old, null),
                User("gjest-ny-aldri", "Guest", cutoff.AddDays(1), null),
                User("medlem-gammel", "Member", old, cutoff.AddDays(-30)),
            ],
            null, null, null, DateTimeOffset.UtcNow);

        var withNever = await ReadAsync<M365InactiveUsers>(await Admin().GetAsync(
            "/api/m365/inactive-users?userType=Guest&before=2026-05-01&refresh=true"));
        Assert.Equal(["gjest-aldri", "gjest-gammel"], withNever.Users.Select(u => u.Id));
        Assert.Equal(4, withNever.TotalOfType);

        var withoutNever = await ReadAsync<M365InactiveUsers>(await Admin().GetAsync(
            "/api/m365/inactive-users?userType=guest&before=2026-05-01&includeNeverSignedIn=false"));
        Assert.Equal(["gjest-gammel"], withoutNever.Users.Select(u => u.Id));

        var members = await ReadAsync<M365InactiveUsers>(await Admin().GetAsync(
            "/api/m365/inactive-users?userType=Member&before=2026-05-01"));
        Assert.Equal("medlem-gammel", Assert.Single(members.Users).Id);
    }

    [Fact]
    public async Task Inactive_users_without_sign_in_data_give_reason_and_empty_list()
    {
        factory.Graph.UserOverview = new M365UserOverview(
            [User("gjest", "Guest", DateTimeOffset.UtcNow.AddYears(-3), null)],
            "Krever rettigheten AuditLog.Read.All og lisensen Entra ID P1.", null, null, DateTimeOffset.UtcNow);

        var result = await ReadAsync<M365InactiveUsers>(await Admin().GetAsync(
            "/api/m365/inactive-users?userType=Guest&before=2026-01-01&refresh=true"));

        Assert.Empty(result.Users);
        Assert.Contains("Entra ID P1", result.SignInUnavailableReason);
    }

    [Theory]
    [InlineData("/api/m365/inactive-users?userType=Admin&before=2026-01-01")]
    [InlineData("/api/m365/inactive-users?userType=Guest")]
    [InlineData("/api/m365/inactive-users?userType=Guest&before=2999-01-01")]
    public async Task Inactive_users_reject_invalid_input(string url)
    {
        var response = await Admin().GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Groups_and_members_are_listed()
    {
        var groupId = Guid.NewGuid().ToString();
        factory.Graph.Groups.Add(new M365Group(groupId, "IT-drift", "Alle i IT", null, "security", false));
        factory.Graph.GroupMembers[groupId] = [new M365GroupMember("u1", "Ola Nordmann", "ola@test.no", "user")];

        var groups = await ReadAsync<List<M365Group>>(await Admin().GetAsync("/api/m365/groups?refresh=true"));
        Assert.Contains(groups, g => g.Id == groupId && g.Kind == "security");

        var members = await ReadAsync<List<M365GroupMember>>(
            await Admin().GetAsync($"/api/m365/groups/{groupId}/members"));
        Assert.Equal("Ola Nordmann", Assert.Single(members).DisplayName);
    }

    [Fact]
    public async Task Missing_permission_gives_readable_error()
    {
        factory.Graph.Failure = new GraphUnavailableException(
            "Appen mangler rettigheten GroupMember.Read.All i Entra ID (admin-samtykke).", 403);

        try
        {
            var response = await Admin().GetAsync("/api/m365/groups?refresh=true");

            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            var body = await ReadAsync<JsonElement>(response);
            Assert.Contains("GroupMember.Read.All", body.GetProperty("message").GetString());
        }
        finally
        {
            factory.Graph.Failure = null;
        }
    }

    [Fact]
    public async Task Group_id_must_be_a_guid()
    {
        var response = await Admin().GetAsync("/api/m365/groups/ikke-en-guid/members");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using backend.Controllers;
using backend.Models;
using backend.Services;

namespace backend.Tests;

// Deaktivere, aktivere og slette brukere i Entra fra Microsoft 365-siden, med
// personens egne rettigheter (on-behalf-of). Graph er falsk: DeniedUsers spiller
// at Entra sier nei fordi personen mangler rolle.
public class M365UserActionsTests(SqliteWebApplicationFactory factory)
    : IClassFixture<SqliteWebApplicationFactory>
{
    private const string UserToken = "personens-token";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Admin()
    {
        var client = factory.CreateClientWithRoles(AppRoles.HelpdeskAdmin);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", UserToken);
        return client;
    }

    private static M365User User(string name, string? id = null) =>
        new(id ?? Guid.NewGuid().ToString(), name, $"{name}@agk.no", $"{name}@agk.no", true, "Member",
            null, null, DateTimeOffset.UtcNow.AddYears(-1), [], null, null);

    // Ny brukeroversikt i den falske Graph-tjenesten, og i API-ets cache
    private async Task<List<M365User>> UsersAsync(params M365User[] users)
    {
        factory.Graph.UserOverview = new M365UserOverview(users, null, null, null, DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.OK, (await Admin().GetAsync("/api/m365/users?refresh=true")).StatusCode);
        return users.ToList();
    }

    private static async Task<List<UserChangeResult>> ResultsAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<List<UserChangeResult>>(Json))!;
    }

    [Fact]
    public async Task Admin_disables_several_users_with_own_token_and_it_is_logged()
    {
        var users = await UsersAsync(User("anna"), User("bjorn"), User("carl"));
        var ids = users.Take(2).Select(u => u.Id).ToList();

        var results = await ResultsAsync(await Admin().PostAsJsonAsync(
            "/api/m365/users/account-enabled", new { userIds = ids, enabled = false }));

        Assert.All(results, r => Assert.True(r.Ok));
        Assert.Equal(["anna", "bjorn"], results.Select(r => r.DisplayName));

        var changes = factory.Graph.UserChanges.Where(c => ids.Contains(c.UserId)).ToList();
        Assert.Equal(2, changes.Count);
        Assert.All(changes, c =>
        {
            Assert.Equal("disable", c.Action);
            Assert.Equal(UserToken, c.UserToken);
        });

        await using var db = factory.CreateDbContext(TestAuthHandler.AllowedTenantId);
        var events = await db.AuditEvents
            .Where(e => e.EntityType == "EntraUser" && ids.Contains(e.EntityId))
            .ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(AuditActions.UserDisabled, e.Action));
        Assert.Contains(events, e => e.Changes!.Contains("anna@agk.no"));
    }

    [Fact]
    public async Task Enable_and_delete_use_the_right_actions()
    {
        var users = await UsersAsync(User("dina"), User("erik"));

        await ResultsAsync(await Admin().PostAsJsonAsync(
            "/api/m365/users/account-enabled", new { userIds = new[] { users[0].Id }, enabled = true }));
        await ResultsAsync(await Admin().PostAsJsonAsync(
            "/api/m365/users/delete", new { userIds = new[] { users[1].Id } }));

        Assert.Contains(factory.Graph.UserChanges, c => c.UserId == users[0].Id && c.Action == "enable");
        Assert.Contains(factory.Graph.UserChanges, c => c.UserId == users[1].Id && c.Action == "delete");

        await using var db = factory.CreateDbContext(TestAuthHandler.AllowedTenantId);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.EntityId == users[0].Id && e.Action == AuditActions.UserEnabled));
        Assert.True(await db.AuditEvents.AnyAsync(e => e.EntityId == users[1].Id && e.Action == AuditActions.UserDeleted));
    }

    [Fact]
    public async Task Each_user_gets_its_own_result_when_entra_denies_some()
    {
        var users = await UsersAsync(User("frida"), User("geir"));
        factory.Graph.DeniedUsers.Add(users[1].Id);
        var unknown = Guid.NewGuid().ToString();

        var results = await ResultsAsync(await Admin().PostAsJsonAsync(
            "/api/m365/users/delete", new { userIds = new[] { users[0].Id, users[1].Id, unknown } }));

        Assert.True(results.Single(r => r.UserId == users[0].Id).Ok);

        var denied = results.Single(r => r.UserId == users[1].Id);
        Assert.False(denied.Ok);
        Assert.Contains("ikke lov", denied.Error);

        var missing = results.Single(r => r.UserId == unknown);
        Assert.False(missing.Ok);
        Assert.DoesNotContain(factory.Graph.UserChanges, c => c.UserId == unknown);

        await using var db = factory.CreateDbContext(TestAuthHandler.AllowedTenantId);
        Assert.False(await db.AuditEvents.AnyAsync(e => e.EntityId == users[1].Id));
    }

    [Fact]
    public async Task Cannot_change_yourself()
    {
        await UsersAsync(User("meg", TestAuthHandler.TestObjectId));

        var response = await Admin().PostAsJsonAsync(
            "/api/m365/users/delete", new { userIds = new[] { TestAuthHandler.TestObjectId } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(factory.Graph.UserChanges, c => c.UserId == TestAuthHandler.TestObjectId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(M365Controller.MaxUsersPerChange + 1)]
    public async Task Rejects_empty_or_too_many(int count)
    {
        var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid().ToString()).ToArray();

        var response = await Admin().PostAsJsonAsync("/api/m365/users/delete", new { userIds = ids });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task User_list_is_fetched_again_after_a_change()
    {
        var users = await UsersAsync(User("hanne"));
        var callsBefore = factory.Graph.UserOverviewCalls;

        await ResultsAsync(await Admin().PostAsJsonAsync(
            "/api/m365/users/account-enabled", new { userIds = new[] { users[0].Id }, enabled = false }));
        await Admin().GetAsync("/api/m365/users");

        Assert.Equal(callsBefore + 1, factory.Graph.UserOverviewCalls);
    }
}

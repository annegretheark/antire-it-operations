using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using backend.Controllers;
using backend.Models;
using backend.Services;

namespace backend.Tests;

// Change and Risk-agenten og AI i driftsrapporten, C8 (ENDRING_OG_RISIKO.md). Den falske
// modellen tar vare på hver forespørsel, så testene kan se nøyaktig hva som ble sendt.
// Testene i klassen kjører etter hverandre.
public class AiAgentTests(AiWebApplicationFactory factory) : IClassFixture<AiWebApplicationFactory>
{
    private const string TenantA = TestAuthHandler.AllowedTenantId;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private record Person(string ObjectId, int EmployeeId, HttpClient Client);

    private async Task<Person> PersonAsync(string first, string last, params string[] roles)
    {
        var oid = Guid.NewGuid().ToString();
        await using var db = factory.CreateDbContext(TenantA);
        var employee = new Employee { FirstName = first, LastName = last, Email = $"{first.ToLower()}@firma.test", EntraObjectId = oid };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        foreach (var role in roles)
            db.GovernanceRoleAssignments.Add(new GovernanceRoleAssignment { Role = role, EmployeeId = employee.Id });
        await db.SaveChangesAsync();

        var client = factory.CreateClientInTenant(TenantA, AppRoles.HelpdeskAgent);
        client.DefaultRequestHeaders.Add(TestAuthHandler.ObjectIdHeader, oid);
        return new Person(oid, employee.Id, client);
    }

    // Endring med personopplysninger og ID-er gjemt i friteksten
    private async Task<ChangeDetailsDto> ChangeWithSecretsAsync(Person requester, Person other)
    {
        int serviceId, subscriptionId;
        await using (var db = factory.CreateDbContext(TenantA))
        {
            var service = new ServiceCatalogItem { Name = $"Lønnssystemet Hemmelig {Guid.NewGuid():N}", Category = "Feil" };
            var subscription = new AzureSubscription { SubscriptionId = Guid.NewGuid().ToString(), DisplayName = "Abonnement-Hemmelig", State = "Enabled" };
            db.AddRange(service, subscription);
            await db.SaveChangesAsync();
            (serviceId, subscriptionId) = (service.Id, subscription.Id);
        }

        var response = await requester.Client.PostAsJsonAsync("/api/changes", new
        {
            title = $"Flytt database for {other.ObjectId}",
            description = $"Avtalt med Kari Hemmeligsen (kari.hemmeligsen@firma.test). Ressurs /subscriptions/{Guid.NewGuid()}/resourceGroups/rg-hemmelig/providers/x",
            type = ChangeTypes.Normal,
            environment = ChangeEnvironments.Test,
            implementationPlan = "Kari Hemmeligsen kjører skriptet",
            rollbackPlan = "Gjenopprett fra backup",
            serviceIds = new[] { serviceId },
            azureScopes = new[] { new { azureSubscriptionId = subscriptionId, resourceGroup = "rg-hemmelig" } },
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ChangeDetailsDto>(Json))!;
    }

    [Fact]
    public async Task Request_to_the_model_holds_no_names_emails_ids_or_resource_ids()
    {
        var kari = await PersonAsync("Kari", "Hemmeligsen", GovernanceRoles.ChangeRequester);
        var other = await PersonAsync("Ola", "Annen");
        var change = await ChangeWithSecretsAsync(kari, other);

        factory.Ai.Response = """{ "text": "Vurder backup", "fields": { "rollbackPlan": "Ny plan" } }""";
        var response = await kari.Client.PostAsJsonAsync($"/api/ai/changes/{change.Change.Id}/assist", new { task = "rollback_plan" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var input = factory.Ai.Requests.Last().Input;
        foreach (var secret in new[] { "Kari", "Hemmeligsen", "@firma.test", other.ObjectId, kari.ObjectId, "/subscriptions/",
                     "rg-hemmelig", "Abonnement-Hemmelig", "Lønnssystemet", change.Change.RequestedByName })
        {
            Assert.DoesNotContain(secret, input, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("[person]", input);
        Assert.Contains("[e-post]", input);
        Assert.Contains("[ressurs]", input);
        Assert.Contains("[id]", input);
    }

    [Fact]
    public async Task Suggestion_only_holds_allowed_fields_and_changes_nothing()
    {
        var requester = await PersonAsync("Siri", "Forslag", GovernanceRoles.ChangeRequester);
        var change = await ChangeWithSecretsAsync(requester, requester);

        factory.Ai.Response = """
            { "text": "Forslag", "fields": { "likelihood": 3, "testPlan": "Test i test", "status": "approved",
              "approve": true, "scopeHash": "x" } }
            """;
        var suggestion = await (await requester.Client.PostAsJsonAsync($"/api/ai/changes/{change.Change.Id}/assist",
            new { task = "risk_classification" })).Content.ReadFromJsonAsync<AiSuggestion>(Json);

        Assert.Equal("3", suggestion!.Fields["likelihood"]);
        Assert.Equal("Test i test", suggestion.Fields["testPlan"]);
        Assert.DoesNotContain("status", suggestion.Fields.Keys);
        Assert.DoesNotContain("approve", suggestion.Fields.Keys);
        Assert.DoesNotContain("scopeHash", suggestion.Fields.Keys);

        // Endringen er uendret, og ingen beslutninger er laget
        await using var db = factory.CreateDbContext(TenantA);
        var stored = await db.ChangeRequests.SingleAsync(c => c.Id == change.Change.Id);
        Assert.Equal(ChangeStatuses.Draft, stored.Status);
        Assert.Equal(change.Change.ScopeHash, stored.ScopeHash);
        Assert.Null(stored.TestPlan);
        Assert.False(await db.ChangeApprovals.AnyAsync(a => a.ChangeRequestId == change.Change.Id));

        // Bare oppgave og nummer logges, ikke innholdet
        var logged = await db.AuditEvents.Where(e => e.Action == AuditActions.AiSuggestionRequested && e.EntityId == change.Change.Id.ToString())
            .Select(e => e.Changes).SingleAsync();
        Assert.Contains("risk_classification", logged);
        Assert.DoesNotContain("Hemmeligsen", logged);
    }

    [Fact]
    public async Task Failure_invalid_json_and_rate_limit_give_a_clear_message()
    {
        var requester = await PersonAsync("Per", "Grense", GovernanceRoles.ChangeRequester);
        var change = await ChangeWithSecretsAsync(requester, requester);
        var url = $"/api/ai/changes/{change.Change.Id}/assist";

        factory.Ai.Failure = new HttpRequestException("timeout");
        try
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await requester.Client.PostAsJsonAsync(url, new { task = "gdpr" })).StatusCode);
        }
        finally
        {
            factory.Ai.Failure = null;
        }

        factory.Ai.Response = "ikke json";
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await requester.Client.PostAsJsonAsync(url, new { task = "gdpr" })).StatusCode);

        factory.Ai.Response = """{ "text": "ok", "fields": {} }""";
        Assert.Equal(HttpStatusCode.BadRequest, (await requester.Client.PostAsJsonAsync(url, new { task = "approve_change" })).StatusCode);

        // Grensen er 5 per time i testene; to er brukt over
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await requester.Client.PostAsJsonAsync(url, new { task = "documentation" })).StatusCode);
        var limited = await requester.Client.PostAsJsonAsync(url, new { task = "documentation" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, limited.StatusCode);
        Assert.Contains("denne timen", await limited.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Access_follows_the_change_rules()
    {
        var requester = await PersonAsync("Eva", "Eier", GovernanceRoles.ChangeRequester);
        var change = await ChangeWithSecretsAsync(requester, requester);

        var outsider = factory.CreateClientInTenant(TenantA);
        outsider.DefaultRequestHeaders.Add(TestAuthHandler.ObjectIdHeader, Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync($"/api/ai/changes/{change.Change.Id}/assist", new { task = "gdpr" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync("/api/ai/changes/draft", new { text = "x" })).StatusCode);

        var otherTenant = factory.CreateClientInTenant(TestAuthHandler.OtherAllowedTenantId, AppRoles.HelpdeskAgent);
        Assert.Equal(HttpStatusCode.NotFound, (await otherTenant.PostAsJsonAsync($"/api/ai/changes/{change.Change.Id}/assist", new { task = "gdpr" })).StatusCode);
    }

    [Fact]
    public async Task Draft_and_management_summary_are_scrubbed()
    {
        var agent = await PersonAsync("Lise", "Leder", GovernanceRoles.ChangeManager);

        factory.Ai.Response = """{ "text": "Utkast", "fields": { "title": "Bytt brannmur" } }""";
        var draft = await (await agent.Client.PostAsJsonAsync("/api/ai/changes/draft",
            new { text = "Lise Leder vil bytte brannmur, kontakt lise@firma.test" })).Content.ReadFromJsonAsync<AiSuggestion>(Json);
        Assert.Equal("Bytt brannmur", draft!.Fields["title"]);
        var input = factory.Ai.Requests.Last().Input;
        Assert.DoesNotContain("Lise", input);
        Assert.DoesNotContain("@firma.test", input);

        factory.Ai.Response = """{ "text": "Oppsummering", "fields": {} }""";
        Assert.Equal(HttpStatusCode.OK, (await agent.Client.PostAsync("/api/ai/risks/summary", null)).StatusCode);
    }

    [Fact]
    public async Task Ops_report_gets_ai_summary_from_figures_only_and_is_saved_when_ai_fails()
    {
        using var scope = factory.Services.GetRequiredService<IServiceScopeFactory>().CreateTenantScope(TenantA);
        var reports = scope.ServiceProvider.GetRequiredService<OpsReportService>();

        factory.Ai.Response = """{ "oppsummering": "Rolig uke.", "tiltak": ["Følg opp SLA", "Rydd gjester"] }""";
        var (ok, _) = await reports.GenerateAsync(
            new DateTime(2025, 1, 5, 23, 0, 0, DateTimeKind.Utc), new DateTime(2025, 1, 12, 23, 0, 0, DateTimeKind.Utc), DateTime.UtcNow);
        Assert.Equal(OpsReportAiStatuses.Ok, ok.AiStatus);
        Assert.Equal("Rolig uke.", ok.AiSummary);
        Assert.Contains("Rydd gjester", ok.AiActions);

        // Til modellen: bare nøkkeltallene (ingen navn, e-post eller titler)
        var input = factory.Ai.Requests.Last().Input;
        Assert.Contains("tickets", input);
        Assert.DoesNotContain("@", input);

        factory.Ai.Failure = new HttpRequestException("nede");
        try
        {
            var (failed, created) = await reports.GenerateAsync(
                new DateTime(2025, 1, 12, 23, 0, 0, DateTimeKind.Utc), new DateTime(2025, 1, 19, 23, 0, 0, DateTimeKind.Utc), DateTime.UtcNow);
            Assert.True(created);
            Assert.Equal(OpsReportAiStatuses.Failed, failed.AiStatus);
            Assert.Null(failed.AiSummary);
        }
        finally
        {
            factory.Ai.Failure = null;
        }
    }
}

// AI av (standard): tydelig melding og driftsrapport med «disabled»
public class AiDisabledTests(SqliteWebApplicationFactory factory) : IClassFixture<SqliteWebApplicationFactory>
{
    [Fact]
    public async Task Ai_is_off_by_default()
    {
        var client = factory.CreateClientWithRoles(AppRoles.HelpdeskAgent);

        var status = await client.GetFromJsonAsync<AiStatusDto>("/api/ai/status", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.False(status!.Enabled);

        var response = await client.PostAsJsonAsync("/api/ai/changes/draft", new { text = "Bytt sertifikat" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("ikke slått på", await response.Content.ReadAsStringAsync());
    }
}

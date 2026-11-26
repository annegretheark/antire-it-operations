using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using backend.Controllers;
using backend.Models;
using backend.Services;

namespace backend.Tests;

// Teams-kort og påminnelser for endringer og risiko, C5 (ENDRING_OG_RISIKO.md).
// Kortene havner i den eksisterende køen (TeamsNotifications) og sendes til den
// falske Teams-tjenesten. Testene i klassen kjører etter hverandre og deler database.
public class GovernanceTeamsTests(SqliteWebApplicationFactory factory)
    : IClassFixture<SqliteWebApplicationFactory>
{
    private const string TenantA = TestAuthHandler.AllowedTenantId;
    private const string WebhookUrl = "https://prod-01.westeurope.logic.azure.com:443/workflows/abc/triggers/manual/paths/invoke?sig=hemmelig123";

    // Tekst som aldri skal havne i et kort
    private const string Secret = "HEMMELIG-INNHOLD";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private record Person(string ObjectId, int EmployeeId, HttpClient Client);

    private HttpClient Admin() => factory.CreateClientWithRoles(AppRoles.HelpdeskAdmin);

    private async Task SetupAsync(bool teamsCards = true)
    {
        Assert.Equal(HttpStatusCode.NoContent, (await Admin().PutAsJsonAsync("/api/teams/settings", new
        {
            webhookUrl = WebhookUrl, notifyCriticalManualTickets = false, removeWebhook = false,
        })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Admin().PutAsJsonAsync("/api/governance/settings", new
        {
            teamsNotificationsEnabled = teamsCards, reminderDaysBefore = 3, retentionMonths = (int?)null,
        })).StatusCode);
    }

    private async Task<Person> PersonAsync(string name, params string[] roles)
    {
        var oid = Guid.NewGuid().ToString();
        await using var db = factory.CreateDbContext(TenantA);
        var employee = new Employee { FirstName = "Test", LastName = name, Email = $"{Guid.NewGuid():N}@test.invalid", EntraObjectId = oid };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        foreach (var role in roles)
            db.GovernanceRoleAssignments.Add(new GovernanceRoleAssignment { Role = role, EmployeeId = employee.Id });
        await db.SaveChangesAsync();

        var client = factory.CreateClientInTenant(TenantA, AppRoles.HelpdeskAgent);
        client.DefaultRequestHeaders.Add(TestAuthHandler.ObjectIdHeader, oid);
        return new Person(oid, employee.Id, client);
    }

    private async Task<int> ServiceAsync()
    {
        await using var db = factory.CreateDbContext(TenantA);
        var item = new ServiceCatalogItem { Name = $"Teamssystem {Guid.NewGuid():N}", Category = "Feil" };
        db.ServiceCatalogItems.Add(item);
        await db.SaveChangesAsync();
        return item.Id;
    }

    private static async Task<ChangeDetailsDto> ReadAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ChangeDetailsDto>(Json))!;
    }

    private static Task<ChangeDetailsDto> MoveAsync(Person p, int id, string to, string? note = null) =>
        p.Client.PostAsJsonAsync($"/api/changes/{id}/transitions", new { to, note }).ContinueWith(t => ReadAsync(t.Result)).Unwrap();

    private record Team(Person Requester, Person Manager, Person Technical, Person Owner, int ServiceId);

    private async Task<Team> TeamAsync() => new(
        await PersonAsync("Requester", GovernanceRoles.ChangeRequester),
        await PersonAsync("Manager", GovernanceRoles.ChangeManager),
        await PersonAsync("Technical", GovernanceRoles.TechnicalOwner),
        await PersonAsync("Owner", GovernanceRoles.SystemOwner),
        await ServiceAsync());

    // Normal produksjonsendring frem til «venter på godkjenning», med hemmelig tekst i alle fritekstfelt
    private async Task<ChangeDetailsDto> PendingAsync(Team team, DateTime? start = null)
    {
        var created = await ReadAsync(await team.Requester.Client.PostAsJsonAsync("/api/changes", new
        {
            title = "Bytt sertifikat", description = Secret, justification = Secret, type = ChangeTypes.Normal,
            environment = ChangeEnvironments.Production, likelihood = 2, impact = 2, riskAnalysis = Secret,
            impactAnalysis = Secret, securityImpact = Secret, gdprImpact = Secret, backupStatus = BackupStatuses.Verified,
            implementationPlan = Secret, testPlan = Secret, testResults = Secret, rollbackPlan = Secret,
            plannedStart = start ?? DateTime.UtcNow.AddDays(10), plannedEnd = (start ?? DateTime.UtcNow.AddDays(10)).AddHours(1),
            serviceIds = new[] { team.ServiceId },
        }));
        var id = created.Change.Id;
        await MoveAsync(team.Requester, id, ChangeStatuses.Submitted);
        await MoveAsync(team.Manager, id, ChangeStatuses.RiskAssessment);
        await MoveAsync(team.Technical, id, ChangeStatuses.TechnicalReview);
        await MoveAsync(team.Technical, id, ChangeStatuses.Testing);
        return await MoveAsync(team.Technical, id, ChangeStatuses.PendingApproval);
    }

    private static Task<HttpResponseMessage> DecideAsync(Person p, ChangeDetailsDto d, string role, string decision = ApprovalDecisions.Approved, string? comment = null) =>
        p.Client.PostAsJsonAsync($"/api/changes/{d.Change.Id}/decisions", new { role, decision, comment, expectedScopeHash = d.Change.ScopeHash });

    private async Task<List<TeamsNotification>> CardsAsync(string number)
    {
        await using var db = factory.CreateDbContext(TenantA);
        return await db.TeamsNotifications.Where(n => n.Payload.Contains(number)).OrderBy(n => n.Id).ToListAsync();
    }

    [Fact]
    public async Task Pending_approval_and_status_cards_are_queued_without_free_text()
    {
        await SetupAsync();
        var team = await TeamAsync();

        var pending = await PendingAsync(team);
        var number = pending.Change.Number;

        var card = Assert.Single(await CardsAsync(number), n => n.Kind == GovernanceNotifier.KindPendingApproval);
        Assert.Contains($"?endring={pending.Change.Id}", card.Payload);
        Assert.Contains("historikk=1", card.Payload);
        Assert.Contains("Change", card.Payload); // manglende roller (change_manager, system_owner)
        Assert.Equal(TeamsNotificationStatuses.Sent, card.Status);

        await ReadAsync(await DecideAsync(team.Manager, pending, GovernanceRoles.ChangeManager, ApprovalDecisions.InfoRequested, Secret));
        await ReadAsync(await DecideAsync(team.Owner, pending, GovernanceRoles.SystemOwner, ApprovalDecisions.Rejected, Secret));

        var cards = await CardsAsync(number);
        Assert.Contains(cards, n => n.Kind == GovernanceNotifier.KindInfoRequested);
        Assert.Contains(cards, n => n.Kind == GovernanceNotifier.KindStatus && n.Payload.Contains("Avvist"));

        // Ingen beskrivelser, planer, analyser eller begrunnelser i noen kort
        Assert.All(cards, n => Assert.DoesNotContain(Secret, n.Payload));
        Assert.DoesNotContain(factory.Teams.Sent, m => m.Payload.Contains(Secret));
    }

    [Fact]
    public async Task No_cards_when_the_setting_is_off()
    {
        await SetupAsync(teamsCards: false);
        try
        {
            var team = await TeamAsync();
            var pending = await PendingAsync(team);
            Assert.Empty(await CardsAsync(pending.Change.Number));
        }
        finally
        {
            await SetupAsync();
        }
    }

    [Fact]
    public async Task Teams_failure_does_not_stop_the_change()
    {
        await SetupAsync();
        var team = await TeamAsync();

        factory.Teams.Failure = new TeamsDeliveryException("Teams svarer ikke");
        try
        {
            var pending = await PendingAsync(team);
            Assert.Equal(ChangeStatuses.PendingApproval, pending.Change.Status);

            var card = Assert.Single(await CardsAsync(pending.Change.Number), n => n.Kind == GovernanceNotifier.KindPendingApproval);
            Assert.Equal(TeamsNotificationStatuses.Pending, card.Status);
            Assert.NotNull(card.LastError);
        }
        finally
        {
            factory.Teams.Failure = null;
        }
    }

    [Fact]
    public async Task Reminders_are_sent_once_even_when_the_job_runs_again()
    {
        await SetupAsync();
        var team = await TeamAsync();
        var monitor = factory.Services.GetRequiredService<GovernanceMonitorService>();

        // Godkjent produksjonsendring som starter om to dager
        var pending = await PendingAsync(team, DateTime.UtcNow.AddDays(2));
        await ReadAsync(await DecideAsync(team.Manager, pending, GovernanceRoles.ChangeManager));
        await ReadAsync(await DecideAsync(team.Owner, pending, GovernanceRoles.SystemOwner));

        // Kritisk risiko uten eier, og en akseptert risiko som utløper om 10 dager
        var riskOwner = await PersonAsync("RiskOwner", GovernanceRoles.RiskOwner);
        var unowned = await (await riskOwner.Client.PostAsJsonAsync("/api/risks", new
        {
            title = "Kritisk uten eier", description = Secret, category = RiskCategories.InformationSecurity,
            likelihood = 5, consequence = 4,
        })).Content.ReadFromJsonAsync<RiskDetailsDto>(Json);

        var accepted = await (await riskOwner.Client.PostAsJsonAsync("/api/risks", new
        {
            title = "Akseptert", description = Secret, category = RiskCategories.TechnicalDebt,
            likelihood = 2, consequence = 2, strategy = RiskStrategies.Accept, ownerEmployeeId = riskOwner.EmployeeId,
        })).Content.ReadFromJsonAsync<RiskDetailsDto>(Json);
        Assert.True((await riskOwner.Client.PostAsJsonAsync($"/api/risks/{accepted!.Risk.Id}/transitions",
            new { to = RiskStatuses.Assessed })).IsSuccessStatusCode);
        Assert.True((await riskOwner.Client.PostAsJsonAsync($"/api/risks/{accepted.Risk.Id}/acceptances", new
        {
            justification = "Lav risiko", validUntil = DateTime.UtcNow.AddDays(10), expectedScore = 4,
        })).IsSuccessStatusCode);

        await monitor.CheckTenantAsync(TenantA, DateTime.UtcNow);
        await monitor.CheckTenantAsync(TenantA, DateTime.UtcNow.AddMinutes(5));

        Assert.Single(await CardsAsync(pending.Change.Number), n => n.Kind == GovernanceNotifier.KindUpcoming);
        Assert.Single(await CardsAsync(unowned!.Risk.Number), n => n.Kind == GovernanceNotifier.KindRiskUntreated);
        Assert.Single(await CardsAsync(accepted.Risk.Number), n => n.Kind == GovernanceNotifier.KindAcceptanceExpiring);

        var risks = await CardsAsync(unowned.Risk.Number);
        Assert.Contains($"?risiko={unowned.Risk.Id}", risks[0].Payload);
        Assert.DoesNotContain(Secret, risks[0].Payload);
    }

    [Fact]
    public async Task Settings_require_helpdesk_admin()
    {
        var agent = factory.CreateClientWithRoles(AppRoles.HelpdeskAgent);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/governance/settings")).StatusCode);

        var invalid = await Admin().PutAsJsonAsync("/api/governance/settings", new { teamsNotificationsEnabled = true, reminderDaysBefore = 99 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
}

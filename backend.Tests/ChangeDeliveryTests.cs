using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using backend.Controllers;
using backend.Models;
using backend.Services;

namespace backend.Tests;

// Simulert GitHub og Terraform, C6 (ENDRING_OG_RISIKO.md): kobling av commit og plan
// gjør godkjenningen ugyldig, sjekken før Apply sier ja bare når alt stemmer, og
// produksjon avvises alltid. Testene i klassen kjører etter hverandre.
public class ChangeDeliveryTests(SqliteWebApplicationFactory factory)
    : IClassFixture<SqliteWebApplicationFactory>
{
    private const string TenantA = TestAuthHandler.AllowedTenantId;
    private const string Sha = "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private record Person(string ObjectId, int EmployeeId, HttpClient Client);

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

    private record Team(Person Requester, Person Manager, Person Technical, Person Owner, Person Implementer, int ServiceId);

    private async Task<Team> TeamAsync()
    {
        int serviceId;
        await using (var db = factory.CreateDbContext(TenantA))
        {
            var item = new ServiceCatalogItem { Name = $"Leveransesystem {Guid.NewGuid():N}", Category = "Feil" };
            db.ServiceCatalogItems.Add(item);
            await db.SaveChangesAsync();
            serviceId = item.Id;
        }

        return new(
            await PersonAsync("Requester", GovernanceRoles.ChangeRequester),
            await PersonAsync("Manager", GovernanceRoles.ChangeManager),
            await PersonAsync("Technical", GovernanceRoles.TechnicalOwner),
            await PersonAsync("Owner", GovernanceRoles.SystemOwner),
            await PersonAsync("Implementer", GovernanceRoles.Implementer),
            serviceId);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private static Task<ChangeDetailsDto> MoveAsync(Person p, int id, string to, string? note = null) =>
        p.Client.PostAsJsonAsync($"/api/changes/{id}/transitions", new { to, note })
            .ContinueWith(t => ReadAsync<ChangeDetailsDto>(t.Result)).Unwrap();

    private static Task<HttpResponseMessage> DecideAsync(Person p, ChangeDetailsDto d, string role) =>
        p.Client.PostAsJsonAsync($"/api/changes/{d.Change.Id}/decisions",
            new { role, decision = ApprovalDecisions.Approved, expectedScopeHash = d.Change.ScopeHash });

    private async Task SetDeliveryAsync(bool enabled)
    {
        var admin = factory.CreateClientWithRoles(AppRoles.HelpdeskAdmin);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/governance/settings", new
        {
            teamsNotificationsEnabled = true, reminderDaysBefore = 3, retentionMonths = (int?)null, simulatedDeliveryEnabled = enabled,
        })).StatusCode);
    }

    // Endring med koblet commit og plan, godkjent og under implementering
    private async Task<(ChangeDetailsDto Change, SimulatedPlan Plan)> InImplementationAsync(
        Team team, string environment = ChangeEnvironments.Test)
    {
        var created = await ReadAsync<ChangeDetailsDto>(await team.Requester.Client.PostAsJsonAsync("/api/changes", new
        {
            title = "Oppdater nettverk", description = "Syntetisk", justification = "Test", type = ChangeTypes.Normal,
            environment, likelihood = 2, impact = 2, riskAnalysis = "x", impactAnalysis = "x", securityImpact = "x",
            gdprImpact = "x", backupStatus = BackupStatuses.Verified, implementationPlan = "Endre NSG", testPlan = "x",
            testResults = "x", rollbackPlan = "x", plannedStart = DateTime.UtcNow.AddDays(1),
            plannedEnd = DateTime.UtcNow.AddDays(1).AddHours(1), serviceIds = new[] { team.ServiceId },
        }));
        var id = created.Change.Id;

        await ReadAsync<ChangeDetailsDto>(await team.Requester.Client.PostAsJsonAsync($"/api/changes/{id}/delivery/link",
            new { pullRequestUrl = "https://github.com/example/infra/pull/42", commitSha = Sha }));
        var plan = await ReadAsync<SimulatedPlanDto>(await team.Requester.Client.PostAsync($"/api/changes/{id}/delivery/plan", null));

        await MoveAsync(team.Requester, id, ChangeStatuses.Submitted);
        await MoveAsync(team.Manager, id, ChangeStatuses.RiskAssessment);
        await MoveAsync(team.Technical, id, ChangeStatuses.TechnicalReview);
        await MoveAsync(team.Technical, id, ChangeStatuses.Testing);
        var pending = await MoveAsync(team.Technical, id, ChangeStatuses.PendingApproval);

        var approved = await ReadAsync<ChangeDetailsDto>(await DecideAsync(team.Manager, pending, GovernanceRoles.ChangeManager));
        if (environment == ChangeEnvironments.Production)
            approved = await ReadAsync<ChangeDetailsDto>(await DecideAsync(team.Owner, pending, GovernanceRoles.SystemOwner));
        Assert.Equal(ChangeStatuses.Approved, approved.Change.Status);

        await MoveAsync(team.Manager, id, ChangeStatuses.Scheduled);
        var implementing = await MoveAsync(team.Implementer, id, ChangeStatuses.Implementation);
        return (implementing, plan.Plan);
    }

    private static async Task<DeploymentGateResult> GateAsync(Person p, string number, string environment, string? sha, string? planHash) =>
        await ReadAsync<DeploymentGateResult>(await p.Client.GetAsync(
            $"/api/changes/{number}/deployment-gate?environment={environment}&commitSha={sha}&planHash={planHash}"));

    [Fact]
    public async Task Gate_allows_only_the_approved_commit_and_plan_in_the_right_environment()
    {
        await SetDeliveryAsync(true);
        var team = await TeamAsync();
        var (change, plan) = await InImplementationAsync(team);
        var number = change.Change.Number;

        Assert.Contains("SIMULERT", plan.Text);
        Assert.Equal(64, plan.Hash.Length);

        Assert.True((await GateAsync(team.Implementer, number, ChangeEnvironments.Test, Sha, plan.Hash)).Allowed);
        Assert.False((await GateAsync(team.Implementer, number, ChangeEnvironments.Test, "0000000", plan.Hash)).Allowed);
        Assert.False((await GateAsync(team.Implementer, number, ChangeEnvironments.Test, Sha, "feil")).Allowed);
        Assert.False((await GateAsync(team.Implementer, number, ChangeEnvironments.Development, Sha, plan.Hash)).Allowed);

        var production = await GateAsync(team.Implementer, number, ChangeEnvironments.Production, Sha, plan.Hash);
        Assert.False(production.Allowed);
        Assert.Contains("Produksjon", production.Reason);

        // Simulert Apply: bare Implementer
        var apply = await ReadAsync<DeploymentGateResult>(await team.Implementer.Client.PostAsync(
            $"/api/changes/{change.Change.Id}/delivery/simulate-apply", null));
        Assert.True(apply.Allowed);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await team.Requester.Client.PostAsync($"/api/changes/{change.Change.Id}/delivery/simulate-apply", null)).StatusCode);

        await using var db = factory.CreateDbContext(TenantA);
        var checks = await db.AuditEvents.CountAsync(e =>
            e.EntityType == nameof(ChangeRequest) && e.EntityId == change.Change.Id.ToString() && e.Action == AuditActions.ChangeGateChecked);
        Assert.True(checks >= 6);
        Assert.True(await db.AuditEvents.AnyAsync(e =>
            e.EntityId == change.Change.Id.ToString() && e.Action == AuditActions.ChangeSimulatedApply));
    }

    [Fact]
    public async Task Production_is_always_refused_even_when_everything_is_approved()
    {
        await SetDeliveryAsync(true);
        var team = await TeamAsync();
        var (change, plan) = await InImplementationAsync(team, ChangeEnvironments.Production);

        var gate = await GateAsync(team.Implementer, change.Change.Number, ChangeEnvironments.Production, Sha, plan.Hash);
        Assert.False(gate.Allowed);

        Assert.Equal(HttpStatusCode.Forbidden, (await team.Implementer.Client.PostAsync(
            $"/api/changes/{change.Change.Id}/delivery/simulate-apply", null)).StatusCode);
    }

    [Fact]
    public async Task New_commit_or_plan_after_approval_invalidates_the_approval()
    {
        await SetDeliveryAsync(true);
        var team = await TeamAsync();
        var created = await ReadAsync<ChangeDetailsDto>(await team.Requester.Client.PostAsJsonAsync("/api/changes", new
        {
            title = "Ny commit", description = "x", justification = "x", type = ChangeTypes.Normal,
            environment = ChangeEnvironments.Test, likelihood = 1, impact = 1, riskAnalysis = "x", securityImpact = "x",
            gdprImpact = "x", implementationPlan = "x", testPlan = "x", testResults = "x", rollbackPlan = "x",
            plannedStart = DateTime.UtcNow.AddDays(1), plannedEnd = DateTime.UtcNow.AddDays(1).AddHours(1),
            serviceIds = new[] { team.ServiceId },
        }));
        var id = created.Change.Id;
        await MoveAsync(team.Requester, id, ChangeStatuses.Submitted);
        await MoveAsync(team.Manager, id, ChangeStatuses.RiskAssessment);
        await MoveAsync(team.Technical, id, ChangeStatuses.TechnicalReview);
        await MoveAsync(team.Technical, id, ChangeStatuses.Testing);
        var pending = await MoveAsync(team.Technical, id, ChangeStatuses.PendingApproval);
        var approved = await ReadAsync<ChangeDetailsDto>(await DecideAsync(team.Manager, pending, GovernanceRoles.ChangeManager));
        Assert.Equal(ChangeStatuses.Approved, approved.Change.Status);

        var linked = await ReadAsync<ChangeDetailsDto>(await team.Requester.Client.PostAsJsonAsync($"/api/changes/{id}/delivery/link",
            new { commitSha = Sha }));
        Assert.Equal(ChangeStatuses.PendingApproval, linked.Change.Status);
        Assert.All(linked.Approvals, a => Assert.False(a.Valid));

        var reapproved = await ReadAsync<ChangeDetailsDto>(await DecideAsync(team.Manager, linked, GovernanceRoles.ChangeManager));
        Assert.Equal(ChangeStatuses.Approved, reapproved.Change.Status);

        var planned = await ReadAsync<SimulatedPlanDto>(await team.Technical.Client.PostAsync($"/api/changes/{id}/delivery/plan", null));
        Assert.Equal(ChangeStatuses.PendingApproval, planned.Details.Change.Status);
    }

    [Theory]
    [InlineData("http://github.com/x/y/pull/1", null)]
    [InlineData("https://gitlab.com/x/y/merge_requests/1", null)]
    [InlineData(null, "xyz123")]
    [InlineData(null, "123")]
    public async Task Invalid_pull_request_url_or_sha_is_refused(string? url, string? sha)
    {
        await SetDeliveryAsync(true);
        var team = await TeamAsync();
        var created = await ReadAsync<ChangeDetailsDto>(await team.Requester.Client.PostAsJsonAsync("/api/changes", new
        {
            title = "Ugyldig", description = "x", type = ChangeTypes.Normal, serviceIds = new[] { team.ServiceId },
        }));

        var response = await team.Requester.Client.PostAsJsonAsync($"/api/changes/{created.Change.Id}/delivery/link",
            new { pullRequestUrl = url, commitSha = sha });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Delivery_can_be_turned_off_and_only_the_mock_exists()
    {
        Assert.IsType<MockDeliveryProvider>(factory.Services.GetRequiredService<IChangeDeliveryProvider>());

        var team = await TeamAsync();
        var created = await ReadAsync<ChangeDetailsDto>(await team.Requester.Client.PostAsJsonAsync("/api/changes", new
        {
            title = "Av", description = "x", type = ChangeTypes.Normal, serviceIds = new[] { team.ServiceId },
        }));

        await SetDeliveryAsync(false);
        try
        {
            Assert.Equal(HttpStatusCode.BadRequest,
                (await team.Requester.Client.PostAsync($"/api/changes/{created.Change.Id}/delivery/plan", null)).StatusCode);
        }
        finally
        {
            await SetDeliveryAsync(true);
        }
    }

    [Fact]
    public async Task Gate_requires_access_and_hides_other_tenants()
    {
        await SetDeliveryAsync(true);
        var team = await TeamAsync();
        var created = await ReadAsync<ChangeDetailsDto>(await team.Requester.Client.PostAsJsonAsync("/api/changes", new
        {
            title = "Skjult", description = "x", type = ChangeTypes.Normal, serviceIds = new[] { team.ServiceId },
        }));
        var url = $"/api/changes/{created.Change.Number}/deployment-gate?environment=test";

        var outsider = factory.CreateClientInTenant(TenantA);
        outsider.DefaultRequestHeaders.Add(TestAuthHandler.ObjectIdHeader, Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(url)).StatusCode);

        var otherTenant = factory.CreateClientInTenant(TestAuthHandler.OtherAllowedTenantId, AppRoles.HelpdeskAgent);
        Assert.Equal(HttpStatusCode.NotFound, (await otherTenant.GetAsync(url)).StatusCode);
    }
}

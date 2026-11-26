using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using backend.Controllers;
using backend.Models;
using backend.Services;

namespace backend.Tests;

// Change Management C2 (ENDRING_OG_RISIKO.md): arbeidsflyt, godkjenning låst til
// innholdet, separasjon av plikter, standardmaler og hasteendringer. Hver test lager
// egne syntetiske personer med egne objekt-ID-er og roller.
public class ChangeWorkflowTests(SqliteWebApplicationFactory factory)
    : IClassFixture<SqliteWebApplicationFactory>
{
    private const string TenantA = TestAuthHandler.AllowedTenantId;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private record Person(string ObjectId, int EmployeeId, HttpClient Client);

    // Syntetisk person (ansatt med Entra-ID) med endringsroller; logger inn med Helpdesk.Agent
    private async Task<Person> PersonAsync(string name, params string[] roles) =>
        await PersonInAsync(name, [AppRoles.HelpdeskAgent], roles);

    private async Task<Person> PersonInAsync(string name, string[] appRoles, string[] roles)
    {
        var oid = Guid.NewGuid().ToString();
        await using var db = factory.CreateDbContext(TenantA);

        var employee = new Employee
        {
            FirstName = "Test", LastName = name, Email = $"{Guid.NewGuid():N}@test.invalid", EntraObjectId = oid,
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        foreach (var role in roles)
            db.GovernanceRoleAssignments.Add(new GovernanceRoleAssignment { Role = role, EmployeeId = employee.Id });
        await db.SaveChangesAsync();

        return new Person(oid, employee.Id, ClientFor(oid, $"Test {name}", appRoles));
    }

    private HttpClient ClientFor(string oid, string name, string[] appRoles, string tenant = TenantA)
    {
        var client = factory.CreateClientInTenant(tenant, appRoles);
        client.DefaultRequestHeaders.Remove(TestAuthHandler.UserHeader);
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, name);
        client.DefaultRequestHeaders.Add(TestAuthHandler.ObjectIdHeader, oid);
        return client;
    }

    private async Task<int> ServiceAsync()
    {
        await using var db = factory.CreateDbContext(TenantA);
        var item = new ServiceCatalogItem { Name = $"Testsystem {Guid.NewGuid():N}", Category = "Feil" };
        db.ServiceCatalogItems.Add(item);
        await db.SaveChangesAsync();
        return item.Id;
    }

    private static async Task<ChangeDetailsDto> ReadAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ChangeDetailsDto>(Json))!;
    }

    // En komplett normalendring (alle felt som trengs gjennom hele løpet)
    private static object NormalInput(int serviceId, string environment = ChangeEnvironments.Production,
        string rollback = "Gjenopprett forrige versjon", DateTime? end = null) => new
    {
        title = "Oppgrader syntetisk testserver",
        description = "Syntetisk endring for test",
        justification = "Sikkerhetsoppdatering",
        type = ChangeTypes.Normal,
        environment,
        likelihood = 2,
        impact = 3,
        riskAnalysis = "Lav risiko",
        impactAnalysis = "Kort nedetid",
        securityImpact = "Ingen",
        gdprImpact = "Ingen personopplysninger",
        backupStatus = BackupStatuses.Verified,
        implementationPlan = "Installer oppdateringen",
        testPlan = "Kjør røyktest",
        testResults = "Røyktest grønn i test",
        rollbackPlan = rollback,
        plannedStart = DateTime.UtcNow.AddDays(2),
        plannedEnd = end ?? DateTime.UtcNow.AddDays(2).AddHours(2),
        serviceIds = new[] { serviceId },
    };

    private static async Task<ChangeDetailsDto> MoveAsync(Person person, int id, string to, string? note = null) =>
        await ReadAsync(await person.Client.PostAsJsonAsync($"/api/changes/{id}/transitions", new { to, note }));

    private static Task<HttpResponseMessage> DecideAsync(
        Person person, ChangeDetailsDto details, string role, string decision = ApprovalDecisions.Approved, string? comment = null) =>
        person.Client.PostAsJsonAsync($"/api/changes/{details.Change.Id}/decisions", new
        {
            role, decision, comment, expectedScopeHash = details.Change.ScopeHash,
        });

    private record Team(Person Requester, Person ChangeManager, Person TechnicalOwner, Person SystemOwner,
        Person Implementer, Person Reviewer, int ServiceId);

    private async Task<Team> TeamAsync() => new(
        await PersonAsync("Requester", GovernanceRoles.ChangeRequester),
        await PersonAsync("ChangeManager", GovernanceRoles.ChangeManager),
        await PersonAsync("TechnicalOwner", GovernanceRoles.TechnicalOwner),
        await PersonAsync("SystemOwner", GovernanceRoles.SystemOwner),
        await PersonAsync("Implementer", GovernanceRoles.Implementer),
        await PersonAsync("Reviewer", GovernanceRoles.Reviewer),
        await ServiceAsync());

    // Normalendring i produksjon frem til «venter på godkjenning»
    private async Task<ChangeDetailsDto> ToPendingApprovalAsync(Team team, object? input = null)
    {
        var created = await ReadAsync(await team.Requester.Client.PostAsJsonAsync(
            "/api/changes", input ?? NormalInput(team.ServiceId)));
        var id = created.Change.Id;

        await MoveAsync(team.Requester, id, ChangeStatuses.Submitted);
        await MoveAsync(team.ChangeManager, id, ChangeStatuses.RiskAssessment);
        await MoveAsync(team.TechnicalOwner, id, ChangeStatuses.TechnicalReview);
        await MoveAsync(team.TechnicalOwner, id, ChangeStatuses.Testing);
        return await MoveAsync(team.TechnicalOwner, id, ChangeStatuses.PendingApproval);
    }

    private async Task<ChangeDetailsDto> ToApprovedAsync(Team team)
    {
        var pending = await ToPendingApprovalAsync(team);
        await ReadAsync(await DecideAsync(team.ChangeManager, pending, GovernanceRoles.ChangeManager));
        return await ReadAsync(await DecideAsync(team.SystemOwner, pending, GovernanceRoles.SystemOwner));
    }

    [Fact]
    public async Task Normal_production_change_goes_through_the_whole_flow()
    {
        var team = await TeamAsync();

        var created = await ReadAsync(await team.Requester.Client.PostAsJsonAsync("/api/changes", NormalInput(team.ServiceId)));
        Assert.Matches(@"^CHG-\d{6}$", created.Change.Number);
        Assert.Equal(ChangeStatuses.Draft, created.Change.Status);
        Assert.Contains(ChangeStatuses.Submitted, created.AllowedTransitions);

        var pending = await ToPendingApprovalAsync(team);
        Assert.Equal(ChangeStatuses.PendingApproval, pending.Change.Status);
        Assert.All(pending.Requirements, r => Assert.False(r.Satisfied));

        var afterFirst = await ReadAsync(await DecideAsync(team.ChangeManager, pending, GovernanceRoles.ChangeManager));
        Assert.Equal(ChangeStatuses.PendingApproval, afterFirst.Change.Status);

        var approved = await ReadAsync(await DecideAsync(team.SystemOwner, pending, GovernanceRoles.SystemOwner));
        Assert.Equal(ChangeStatuses.Approved, approved.Change.Status);
        Assert.All(approved.Requirements, r => Assert.True(r.Satisfied));

        var id = approved.Change.Id;
        await MoveAsync(team.ChangeManager, id, ChangeStatuses.Scheduled);
        await MoveAsync(team.Implementer, id, ChangeStatuses.Implementation);
        await MoveAsync(team.Implementer, id, ChangeStatuses.Verification, "Oppdatert uten feil");
        await MoveAsync(team.Reviewer, id, ChangeStatuses.Completed);
        var closed = await MoveAsync(team.ChangeManager, id, ChangeStatuses.Closed);

        Assert.Equal(ChangeStatuses.Closed, closed.Change.Status);
        Assert.Equal(team.Implementer.EmployeeId, closed.Change.ImplementerEmployeeId);
        Assert.NotNull(closed.Change.ClosedAt);

        var history = await team.ChangeManager.Client.GetFromJsonAsync<List<AuditEvent>>($"/api/changes/{id}/history", Json);
        Assert.Contains(history!, e => e.Action == AuditActions.ChangeApproved);
        Assert.Contains(history!, e => e.Action == AuditActions.ChangeStatusChanged);
    }

    [Fact]
    public async Task Requester_cannot_approve_own_change()
    {
        var team = await TeamAsync();
        var requesterManager = await PersonAsync("RequesterManager", GovernanceRoles.ChangeRequester, GovernanceRoles.ChangeManager);
        var pending = await ToPendingApprovalAsync(team with { Requester = requesterManager });

        var response = await DecideAsync(requesterManager, pending, GovernanceRoles.ChangeManager);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Assigned_implementer_cannot_approve_and_earlier_approval_stops_counting()
    {
        var team = await TeamAsync();
        var implementerManager = await PersonAsync("ImplementerManager", GovernanceRoles.ChangeManager, GovernanceRoles.Implementer);

        var input = NormalInput(team.ServiceId);
        var pending = await ToPendingApprovalAsync(team, input);

        // Godkjenner som Change Manager før hen blir implementør
        await ReadAsync(await DecideAsync(implementerManager, pending, GovernanceRoles.ChangeManager));

        // Settes som implementør (ikke en del av hashen): godkjenningen teller ikke lenger
        var json = JsonSerializer.SerializeToNode(input, Json)!.AsObject();
        json["implementerEmployeeId"] = implementerManager.EmployeeId;
        var updated = await ReadAsync(await team.Requester.Client.PutAsJsonAsync($"/api/changes/{pending.Change.Id}", json));
        Assert.False(updated.Requirements.Single(r => r.Role == GovernanceRoles.ChangeManager).Satisfied);

        var again = await DecideAsync(implementerManager, updated, GovernanceRoles.ChangeManager);
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
    }

    [Fact]
    public async Task One_person_counts_for_only_one_rule()
    {
        var team = await TeamAsync();
        var both = await PersonAsync("ManagerAndOwner", GovernanceRoles.ChangeManager, GovernanceRoles.SystemOwner);
        var pending = await ToPendingApprovalAsync(team);

        await ReadAsync(await DecideAsync(both, pending, GovernanceRoles.ChangeManager));
        var second = await DecideAsync(both, pending, GovernanceRoles.SystemOwner);

        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
    }

    [Fact]
    public async Task Decision_on_content_the_approver_has_not_seen_is_rejected()
    {
        var team = await TeamAsync();
        var pending = await ToPendingApprovalAsync(team);

        var response = await team.ChangeManager.Client.PostAsJsonAsync($"/api/changes/{pending.Change.Id}/decisions", new
        {
            role = GovernanceRoles.ChangeManager, decision = ApprovalDecisions.Approved, expectedScopeHash = "gammel-hash",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Changing_approved_content_requires_new_approval_but_moving_the_time_does_not()
    {
        var team = await TeamAsync();
        var approved = await ToApprovedAsync(team);
        var id = approved.Change.Id;

        // Nytt tidspunkt: fortsatt godkjent
        var moved = await ReadAsync(await team.Requester.Client.PutAsJsonAsync($"/api/changes/{id}",
            NormalInput(team.ServiceId, end: DateTime.UtcNow.AddDays(2).AddHours(5))));
        Assert.Equal(ChangeStatuses.Approved, moved.Change.Status);

        await MoveAsync(team.ChangeManager, id, ChangeStatuses.Scheduled);

        // Ny rollback-plan: tilbake til godkjenning, gamle godkjenninger gjelder ikke
        var changed = await ReadAsync(await team.Requester.Client.PutAsJsonAsync($"/api/changes/{id}",
            NormalInput(team.ServiceId, rollback: "Ny rollback-plan")));
        Assert.Equal(ChangeStatuses.PendingApproval, changed.Change.Status);
        Assert.All(changed.Approvals, a => Assert.False(a.Valid));
        Assert.All(changed.Requirements, r => Assert.False(r.Satisfied));

        // Kan ikke implementeres
        var implement = await team.Implementer.Client.PostAsJsonAsync($"/api/changes/{id}/transitions",
            new { to = ChangeStatuses.Implementation });
        Assert.Equal(HttpStatusCode.BadRequest, implement.StatusCode);

        await using var db = factory.CreateDbContext(TenantA);
        Assert.True(await db.AuditEvents.AnyAsync(e =>
            e.EntityType == nameof(ChangeRequest) && e.EntityId == id.ToString() && e.Action == AuditActions.ApprovalInvalidated));
    }

    [Fact]
    public async Task Wrong_role_and_invalid_transitions_are_refused()
    {
        var team = await TeamAsync();
        var created = await ReadAsync(await team.Requester.Client.PostAsJsonAsync("/api/changes", NormalInput(team.ServiceId)));
        var id = created.Change.Id;

        var skip = await team.Requester.Client.PostAsJsonAsync($"/api/changes/{id}/transitions", new { to = ChangeStatuses.Approved });
        Assert.Equal(HttpStatusCode.BadRequest, skip.StatusCode);

        await MoveAsync(team.Requester, id, ChangeStatuses.Submitted);

        var wrongRole = await team.Requester.Client.PostAsJsonAsync($"/api/changes/{id}/transitions",
            new { to = ChangeStatuses.RiskAssessment });
        Assert.Equal(HttpStatusCode.Forbidden, wrongRole.StatusCode);
    }

    [Fact]
    public async Task Submit_requires_the_needed_fields()
    {
        var team = await TeamAsync();
        var created = await ReadAsync(await team.Requester.Client.PostAsJsonAsync("/api/changes", new
        {
            title = "Uferdig", description = "Mangler planer", type = ChangeTypes.Normal,
            environment = ChangeEnvironments.Production, serviceIds = new[] { team.ServiceId },
        }));

        var response = await team.Requester.Client.PostAsJsonAsync($"/api/changes/{created.Change.Id}/transitions",
            new { to = ChangeStatuses.Submitted });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("implementeringsplan", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Rejection_and_info_request_are_recorded()
    {
        var team = await TeamAsync();
        var pending = await ToPendingApprovalAsync(team);

        var info = await ReadAsync(await DecideAsync(team.ChangeManager, pending, GovernanceRoles.ChangeManager,
            ApprovalDecisions.InfoRequested, "Mangler vedlikeholdsvindu"));
        Assert.True(info.WaitingForInfo);
        Assert.Equal(ChangeStatuses.PendingApproval, info.Change.Status);

        var noReason = await DecideAsync(team.SystemOwner, pending, GovernanceRoles.SystemOwner, ApprovalDecisions.Rejected);
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        var rejected = await ReadAsync(await DecideAsync(team.SystemOwner, pending, GovernanceRoles.SystemOwner,
            ApprovalDecisions.Rejected, "For høy risiko"));
        Assert.Equal(ChangeStatuses.Rejected, rejected.Change.Status);
    }

    [Fact]
    public async Task Standard_change_follows_its_preapproved_template()
    {
        var team = await TeamAsync();
        var admin = await PersonInAsync("Admin", [AppRoles.HelpdeskAdmin], []);

        var template = await (await admin.Client.PostAsJsonAsync("/api/changetemplates", new
        {
            name = $"Patch testserver {Guid.NewGuid():N}",
            implementationPlan = "Installer månedlig patch",
            testPlan = "Sjekk tjenesten svarer",
            rollbackPlan = "Avinstaller patch",
            allowedEnvironments = new[] { ChangeEnvironments.Development, ChangeEnvironments.Test },
            maxRiskScore = 6,
        })).Content.ReadFromJsonAsync<ChangeTemplate>(Json);

        object StandardInput(string environment, int likelihood) => new
        {
            title = "Månedlig patch", description = "Syntetisk", type = ChangeTypes.Standard, templateId = template!.Id,
            environment, likelihood, impact = 2, serviceIds = new[] { team.ServiceId },
            plannedStart = DateTime.UtcNow.AddDays(1), plannedEnd = DateTime.UtcNow.AddDays(1).AddHours(1),
        };

        // Ikke forhåndsgodkjent ennå
        var notApproved = await team.Requester.Client.PostAsJsonAsync("/api/changes", StandardInput(ChangeEnvironments.Test, 1));
        Assert.Equal(HttpStatusCode.BadRequest, notApproved.StatusCode);

        Assert.Equal(HttpStatusCode.OK,
            (await team.ChangeManager.Client.PostAsync($"/api/changetemplates/{template!.Id}/approve", null)).StatusCode);

        // Innenfor malen: rett til planlagt, og kan implementeres
        var ok = await ReadAsync(await team.Requester.Client.PostAsJsonAsync("/api/changes", StandardInput(ChangeEnvironments.Test, 1)));
        Assert.Equal("Installer månedlig patch", ok.Change.ImplementationPlan);
        var scheduled = await MoveAsync(team.Requester, ok.Change.Id, ChangeStatuses.Submitted);
        Assert.Equal(ChangeStatuses.Scheduled, scheduled.Change.Status);
        await MoveAsync(team.Implementer, ok.Change.Id, ChangeStatuses.Implementation);

        // Produksjon er ikke tillatt i malen
        var production = await ReadAsync(await team.Requester.Client.PostAsJsonAsync("/api/changes",
            StandardInput(ChangeEnvironments.Production, 1)));
        var refused = await team.Requester.Client.PostAsJsonAsync($"/api/changes/{production.Change.Id}/transitions",
            new { to = ChangeStatuses.Submitted });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        // For høy risiko (4 × 2 = 8 > 6)
        var risky = await ReadAsync(await team.Requester.Client.PostAsJsonAsync("/api/changes", StandardInput(ChangeEnvironments.Test, 4)));
        Assert.Equal(HttpStatusCode.BadRequest, (await team.Requester.Client.PostAsJsonAsync(
            $"/api/changes/{risky.Change.Id}/transitions", new { to = ChangeStatuses.Submitted })).StatusCode);

        // Planene kan ikke endres
        var edit = await team.Requester.Client.PutAsJsonAsync($"/api/changes/{risky.Change.Id}", new
        {
            title = "Månedlig patch", description = "Syntetisk", environment = ChangeEnvironments.Test,
            rollbackPlan = "Egen plan", serviceIds = new[] { team.ServiceId },
        });
        Assert.Equal(HttpStatusCode.BadRequest, edit.StatusCode);
    }

    [Fact]
    public async Task Emergency_change_needs_fast_approval_and_a_post_review_before_closing()
    {
        var team = await TeamAsync();
        var approver = await PersonAsync("Approver", GovernanceRoles.Approver);

        var created = await ReadAsync(await team.Requester.Client.PostAsJsonAsync("/api/changes", new
        {
            title = "Steng åpen port", description = "Syntetisk hastesak", justification = "Aktiv trussel",
            type = ChangeTypes.Emergency, environment = ChangeEnvironments.Production,
            implementationPlan = "Endre NSG-regel", rollbackPlan = "Gjenopprett regel",
            backupStatus = BackupStatuses.NotApplicable, serviceIds = new[] { team.ServiceId },
        }));
        var id = created.Change.Id;
        Assert.True(created.Change.PostReviewRequired);

        var pending = await MoveAsync(team.Requester, id, ChangeStatuses.Submitted);
        Assert.Equal(ChangeStatuses.PendingApproval, pending.Change.Status);

        await ReadAsync(await DecideAsync(team.ChangeManager, pending, GovernanceRoles.ChangeManager));
        var approved = await ReadAsync(await DecideAsync(approver, pending, GovernanceRoles.Approver));
        Assert.Equal(ChangeStatuses.Approved, approved.Change.Status);

        await MoveAsync(team.Implementer, id, ChangeStatuses.Implementation);
        await MoveAsync(team.Implementer, id, ChangeStatuses.Verification, "Porten er stengt");
        await MoveAsync(team.Reviewer, id, ChangeStatuses.Completed);

        var tooEarly = await team.ChangeManager.Client.PostAsJsonAsync($"/api/changes/{id}/transitions", new { to = ChangeStatuses.Closed });
        Assert.Equal(HttpStatusCode.BadRequest, tooEarly.StatusCode);

        // Implementøren kan ikke gjøre etterkontrollen, selv med rollen
        var implementerReviewer = await PersonAsync("ImplementerReviewer", GovernanceRoles.Reviewer);
        await using (var db = factory.CreateDbContext(TenantA))
        {
            var change = await db.ChangeRequests.SingleAsync(c => c.Id == id);
            Assert.Equal(team.Implementer.EmployeeId, change.ImplementerEmployeeId);
        }
        var byImplementer = await ClientFor(team.Implementer.ObjectId, "Test Implementer", [AppRoles.HelpdeskAgent])
            .PostAsJsonAsync($"/api/changes/{id}/post-review", new { notes = "Egen kontroll" });
        Assert.Equal(HttpStatusCode.Forbidden, byImplementer.StatusCode);

        var reviewed = await ReadAsync(await implementerReviewer.Client.PostAsJsonAsync($"/api/changes/{id}/post-review",
            new { notes = "Riktig håndtert, ingen avvik" }));
        Assert.NotNull(reviewed.Change.PostReviewedAt);

        var closed = await MoveAsync(team.ChangeManager, id, ChangeStatuses.Closed);
        Assert.Equal(ChangeStatuses.Closed, closed.Change.Status);
    }

    [Fact]
    public async Task Role_can_come_from_an_entra_group()
    {
        var team = await TeamAsync();
        var groupId = Guid.NewGuid().ToString();
        var member = await PersonAsync("GroupManager");

        await using (var db = factory.CreateDbContext(TenantA))
        {
            db.GovernanceRoleAssignments.Add(new GovernanceRoleAssignment { Role = GovernanceRoles.ChangeManager, EntraGroupId = groupId });
            await db.SaveChangesAsync();
        }
        factory.GroupMembers.Add(member.ObjectId);

        var me = await member.Client.GetFromJsonAsync<MyGovernanceRolesDto>("/api/governance/me", Json);
        Assert.Contains(me!.Roles, r => r.Role == GovernanceRoles.ChangeManager);

        var pending = await ToPendingApprovalAsync(team);
        await ReadAsync(await DecideAsync(member, pending, GovernanceRoles.ChangeManager));
    }

    [Fact]
    public async Task Admin_cannot_assign_roles_to_themselves()
    {
        var admin = await PersonInAsync("SelfAdmin", [AppRoles.HelpdeskAdmin], []);

        var self = await admin.Client.PostAsJsonAsync("/api/governance/roles",
            new { role = GovernanceRoles.ChangeManager, employeeId = admin.EmployeeId });
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);

        var other = await PersonAsync("Other");
        var ok = await admin.Client.PostAsJsonAsync("/api/governance/roles",
            new { role = GovernanceRoles.ChangeManager, employeeId = other.EmployeeId });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);

        var both = await admin.Client.PostAsJsonAsync("/api/governance/roles",
            new { role = GovernanceRoles.ChangeManager, employeeId = other.EmployeeId, entraGroupId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
    }

    [Fact]
    public async Task Changes_are_hidden_from_other_tenants_and_from_users_without_access()
    {
        var team = await TeamAsync();
        var created = await ReadAsync(await team.Requester.Client.PostAsJsonAsync("/api/changes", NormalInput(team.ServiceId)));

        var otherTenant = ClientFor(team.ChangeManager.ObjectId, "Annen tenant", [AppRoles.HelpdeskAgent],
            TestAuthHandler.OtherAllowedTenantId);
        Assert.Equal(HttpStatusCode.NotFound, (await otherTenant.GetAsync($"/api/changes/{created.Change.Id}")).StatusCode);

        // Uten Helpdesk.Agent og uten endringsrolle: ser ikke andres endringer og kan ikke opprette
        var outsider = ClientFor(Guid.NewGuid().ToString(), "Utenforstående", []);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/changes/{created.Change.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await outsider.PostAsJsonAsync("/api/changes", NormalInput(team.ServiceId))).StatusCode);
        Assert.Empty((await outsider.GetFromJsonAsync<List<ChangeSummaryDto>>("/api/changes", Json))!);
    }
}

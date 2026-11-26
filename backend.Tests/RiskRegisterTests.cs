using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using backend.Controllers;
using backend.Models;
using backend.Services;

namespace backend.Tests;

// Risikoregisteret, C3 (ENDRING_OG_RISIKO.md): nivåer, statuser, tilgang, aksept som
// ny hendelse, aksept som faller bort, koblinger, matrise og bakgrunnsjobben.
// Hver test lager egne syntetiske personer.
public class RiskRegisterTests(SqliteWebApplicationFactory factory)
    : IClassFixture<SqliteWebApplicationFactory>
{
    private const string TenantA = TestAuthHandler.AllowedTenantId;
    private const string TenantB = TestAuthHandler.OtherAllowedTenantId;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private record Person(string ObjectId, int EmployeeId, HttpClient Client);

    private async Task<Person> PersonAsync(string name, string[] appRoles, params string[] roles)
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

    private Task<Person> AgentAsync(string name, params string[] roles) =>
        PersonAsync(name, [AppRoles.HelpdeskAgent], roles);

    private HttpClient ClientFor(string oid, string name, string[] appRoles, string tenant = TenantA)
    {
        var client = factory.CreateClientInTenant(tenant, appRoles);
        client.DefaultRequestHeaders.Remove(TestAuthHandler.UserHeader);
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, name);
        client.DefaultRequestHeaders.Add(TestAuthHandler.ObjectIdHeader, oid);
        return client;
    }

    private static async Task<RiskDetailsDto> ReadAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<RiskDetailsDto>(Json))!;
    }

    private static object Input(int likelihood, int consequence, int? owner = null, string? strategy = null,
        int? actionOwner = null, DateTime? due = null, DateTime? review = null) => new
    {
        title = "Backup er ikke testet",
        description = "Syntetisk risiko for test",
        category = RiskCategories.BackupAndDisasterRecovery,
        likelihood,
        consequence,
        existingControls = "Nattlig backup",
        recommendedActions = "Test gjenoppretting kvartalsvis",
        strategy,
        ownerEmployeeId = owner,
        actionOwnerEmployeeId = actionOwner,
        dueDate = due,
        nextReviewAt = review,
    };

    private static async Task<RiskDetailsDto> MoveAsync(Person person, int id, string to, string? note = null) =>
        await ReadAsync(await person.Client.PostAsJsonAsync($"/api/risks/{id}/transitions", new { to, note }));

    private static Task<HttpResponseMessage> AcceptAsync(Person person, RiskDetailsDto risk, DateTime? until = null, int? score = null) =>
        person.Client.PostAsJsonAsync($"/api/risks/{risk.Risk.Id}/acceptances", new
        {
            justification = "Akseptert av eier for testen",
            validUntil = until ?? DateTime.UtcNow.AddMonths(6),
            expectedScore = score ?? risk.Risk.Score,
        });

    // Risiko registrert av «registrar», eid av «owner» (Risk Owner), vurdert, strategi accept
    private async Task<(Person Owner, RiskDetailsDto Risk)> AcceptableRiskAsync(int likelihood, int consequence, Person? registrar = null)
    {
        var owner = await AgentAsync("Owner", GovernanceRoles.RiskOwner);
        registrar ??= await AgentAsync("Registrar");

        var created = await ReadAsync(await registrar.Client.PostAsJsonAsync("/api/risks",
            Input(likelihood, consequence, owner.EmployeeId, RiskStrategies.Accept)));
        var assessed = await MoveAsync(owner, created.Risk.Id, RiskStatuses.Assessed);
        return (owner, assessed);
    }

    [Theory]
    [InlineData(1, RiskLevels.Low)]
    [InlineData(4, RiskLevels.Low)]
    [InlineData(5, RiskLevels.Medium)]
    [InlineData(9, RiskLevels.Medium)]
    [InlineData(10, RiskLevels.High)]
    [InlineData(14, RiskLevels.High)]
    [InlineData(15, RiskLevels.Critical)]
    [InlineData(25, RiskLevels.Critical)]
    public void Levels_follow_the_score(int score, string level) => Assert.Equal(level, RiskLevels.For(score));

    [Fact]
    public async Task Register_and_treat_a_risk()
    {
        var owner = await AgentAsync("Owner", GovernanceRoles.RiskOwner);
        var actionOwner = await AgentAsync("ActionOwner");
        var registrar = await AgentAsync("Registrar");

        var created = await ReadAsync(await registrar.Client.PostAsJsonAsync("/api/risks", Input(4, 3)));
        Assert.Matches(@"^RISK-\d{6}$", created.Risk.Number);
        Assert.Equal(12, created.Risk.Score);
        Assert.Equal(RiskLevels.High, created.Level);
        Assert.Equal(RiskStatuses.Identified, created.Risk.Status);

        // Uten eier kan den ikke vurderes
        var noOwner = await owner.Client.PostAsJsonAsync($"/api/risks/{created.Risk.Id}/transitions", new { to = RiskStatuses.Assessed });
        Assert.Equal(HttpStatusCode.BadRequest, noOwner.StatusCode);

        var id = created.Risk.Id;
        await ReadAsync(await owner.Client.PutAsJsonAsync($"/api/risks/{id}",
            Input(4, 3, owner.EmployeeId, RiskStrategies.Mitigate, actionOwner.EmployeeId, DateTime.UtcNow.AddDays(30))));
        await MoveAsync(owner, id, RiskStatuses.Assessed);
        await MoveAsync(owner, id, RiskStatuses.InTreatment);

        // Tiltaksansvarlig kan melde tiltaket som gjort, men ikke lukke
        var monitoring = await MoveAsync(actionOwner, id, RiskStatuses.Monitoring);
        Assert.Equal(RiskStatuses.Monitoring, monitoring.Risk.Status);
        var actionOwnerClose = await actionOwner.Client.PostAsJsonAsync($"/api/risks/{id}/transitions",
            new { to = RiskStatuses.Closed, note = "Ferdig" });
        Assert.Equal(HttpStatusCode.Forbidden, actionOwnerClose.StatusCode);

        var noNote = await owner.Client.PostAsJsonAsync($"/api/risks/{id}/transitions", new { to = RiskStatuses.Closed });
        Assert.Equal(HttpStatusCode.BadRequest, noNote.StatusCode);

        var closed = await MoveAsync(owner, id, RiskStatuses.Closed, "Tiltaket virker");
        Assert.Equal(RiskStatuses.Closed, closed.Risk.Status);

        // «accepted» kan ikke settes som vanlig status
        var direct = await owner.Client.PostAsJsonAsync($"/api/risks/{id}/transitions", new { to = RiskStatuses.Accepted });
        Assert.Equal(HttpStatusCode.BadRequest, direct.StatusCode);
    }

    [Fact]
    public async Task Only_owner_or_risk_roles_can_edit()
    {
        var registrar = await AgentAsync("Registrar");
        var other = await AgentAsync("Other");
        var created = await ReadAsync(await registrar.Client.PostAsJsonAsync("/api/risks", Input(2, 2)));

        var response = await other.Client.PutAsJsonAsync($"/api/risks/{created.Risk.Id}", Input(5, 5));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var security = await AgentAsync("Security", GovernanceRoles.SecurityOfficer);
        await ReadAsync(await security.Client.PutAsJsonAsync($"/api/risks/{created.Risk.Id}", Input(3, 2)));
    }

    [Fact]
    public async Task Designated_owner_with_role_accepts_and_it_is_a_new_event()
    {
        var (owner, risk) = await AcceptableRiskAsync(3, 3);

        // Annen Risk Owner som ikke er utpekt eier
        var otherOwner = await AgentAsync("OtherOwner", GovernanceRoles.RiskOwner);
        Assert.Equal(HttpStatusCode.Forbidden, (await AcceptAsync(otherOwner, risk)).StatusCode);

        // Feil score
        Assert.Equal(HttpStatusCode.Conflict, (await AcceptAsync(owner, risk, score: 1)).StatusCode);

        // For lang gyldighet
        Assert.Equal(HttpStatusCode.BadRequest,
            (await AcceptAsync(owner, risk, until: DateTime.UtcNow.AddMonths(13))).StatusCode);

        var accepted = await ReadAsync(await AcceptAsync(owner, risk));
        Assert.Equal(RiskStatuses.Accepted, accepted.Risk.Status);
        Assert.True(accepted.AcceptanceActive);

        var revoked = await ReadAsync(await owner.Client.PostAsJsonAsync(
            $"/api/risks/{risk.Risk.Id}/acceptances/revoke", new { justification = "Ny informasjon" }));
        Assert.Equal(RiskStatuses.Assessed, revoked.Risk.Status);
        Assert.False(revoked.AcceptanceActive);

        await using var db = factory.CreateDbContext(TenantA);
        var rows = await db.RiskAcceptances.Where(a => a.RiskId == risk.Risk.Id).OrderBy(a => a.Id).Select(a => a.Kind).ToListAsync();
        Assert.Equal([RiskAcceptanceKinds.Accepted, RiskAcceptanceKinds.Revoked], rows);
    }

    [Fact]
    public async Task Owner_without_risk_owner_role_cannot_accept()
    {
        var owner = await AgentAsync("PlainOwner");
        var editor = await AgentAsync("Editor", GovernanceRoles.RiskOwner);
        var created = await ReadAsync(await editor.Client.PostAsJsonAsync("/api/risks",
            Input(2, 2, owner.EmployeeId, RiskStrategies.Accept)));
        var assessed = await MoveAsync(editor, created.Risk.Id, RiskStatuses.Assessed);

        Assert.Equal(HttpStatusCode.Forbidden, (await AcceptAsync(owner, assessed)).StatusCode);
    }

    [Fact]
    public async Task Critical_risk_cannot_be_accepted_by_the_one_who_registered_it()
    {
        var owner = await AgentAsync("SelfOwner", GovernanceRoles.RiskOwner);
        var (_, critical) = await AcceptableRiskAsync(5, 4, registrar: owner);

        // AcceptableRiskAsync lager en ny eier; gjør registranten til eier
        var mine = await ReadAsync(await owner.Client.PutAsJsonAsync($"/api/risks/{critical.Risk.Id}",
            Input(5, 4, owner.EmployeeId, RiskStrategies.Accept)));
        Assert.Equal(RiskLevels.Critical, mine.Level);

        Assert.Equal(HttpStatusCode.Forbidden, (await AcceptAsync(owner, mine)).StatusCode);

        // En høy (ikke kritisk) risiko kan registranten akseptere
        var high = await ReadAsync(await owner.Client.PutAsJsonAsync($"/api/risks/{critical.Risk.Id}",
            Input(4, 3, owner.EmployeeId, RiskStrategies.Accept)));
        await ReadAsync(await AcceptAsync(owner, high));
    }

    [Fact]
    public async Task Acceptance_stops_counting_when_the_score_increases()
    {
        var (owner, risk) = await AcceptableRiskAsync(2, 3);
        await ReadAsync(await AcceptAsync(owner, risk));

        var raised = await ReadAsync(await owner.Client.PutAsJsonAsync($"/api/risks/{risk.Risk.Id}",
            Input(4, 3, owner.EmployeeId, RiskStrategies.Accept)));

        Assert.Equal(RiskStatuses.Assessed, raised.Risk.Status);
        Assert.False(raised.AcceptanceActive);
        Assert.Contains("Scoren", raised.AcceptanceInactiveReason);

        await using var db = factory.CreateDbContext(TenantA);
        Assert.True(await db.AuditEvents.AnyAsync(e =>
            e.EntityType == nameof(Risk) && e.EntityId == risk.Risk.Id.ToString() && e.Action == AuditActions.RiskAcceptanceVoided));
    }

    [Fact]
    public async Task Job_expires_acceptance_and_logs_overdue_and_review_once()
    {
        var (owner, risk) = await AcceptableRiskAsync(2, 2);
        await ReadAsync(await AcceptAsync(owner, risk, until: DateTime.UtcNow.AddMonths(1)));

        var actionOwner = await AgentAsync("ActionOwner2");
        var overdue = await ReadAsync(await owner.Client.PostAsJsonAsync("/api/risks",
            Input(3, 3, owner.EmployeeId, RiskStrategies.Mitigate, actionOwner.EmployeeId,
                DateTime.UtcNow.AddDays(10), DateTime.UtcNow.AddDays(20))));

        var monitor = factory.Services.GetRequiredService<GovernanceMonitorService>();
        var later = DateTime.UtcNow.AddMonths(2);

        await monitor.CheckTenantAsync(TenantA, later);
        await monitor.CheckTenantAsync(TenantA, later.AddHours(1));

        await using var db = factory.CreateDbContext(TenantA);
        var expired = await db.Risks.SingleAsync(r => r.Id == risk.Risk.Id);
        Assert.Equal(RiskStatuses.Assessed, expired.Status);

        async Task<int> CountAsync(int riskId, string action) => await db.AuditEvents.CountAsync(e =>
            e.EntityType == nameof(Risk) && e.EntityId == riskId.ToString() && e.Action == action);

        Assert.Equal(1, await CountAsync(risk.Risk.Id, AuditActions.RiskAcceptanceExpired));
        Assert.Equal(1, await CountAsync(overdue.Risk.Id, AuditActions.RiskActionOverdue));
        Assert.Equal(1, await CountAsync(overdue.Risk.Id, AuditActions.RiskReviewDue));
        Assert.True(await db.AuditEvents.AnyAsync(e =>
            e.EntityId == risk.Risk.Id.ToString() && e.Action == AuditActions.RiskAcceptanceExpired && e.ActorObjectId == "system"));
    }

    [Fact]
    public async Task Links_must_be_in_the_same_tenant_and_show_on_the_change()
    {
        var owner = await AgentAsync("LinkOwner", GovernanceRoles.RiskOwner);
        var created = await ReadAsync(await owner.Client.PostAsJsonAsync("/api/risks", Input(2, 2, owner.EmployeeId)));

        int ticketA, ticketB, serviceId, changeId;
        await using (var db = factory.CreateDbContext(TenantA))
        {
            var ticket = new Ticket { Description = "Syntetisk sak" };
            var service = new ServiceCatalogItem { Name = $"Risikosystem {Guid.NewGuid():N}", Category = "Feil" };
            var change = new ChangeRequest
            {
                Number = $"CHG-T{Random.Shared.Next(100000, 999999)}", Title = "Koblet", Description = "x",
                RequestedByObjectId = owner.ObjectId, RequestedByName = "x",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.AddRange(ticket, service, change);
            await db.SaveChangesAsync();
            (ticketA, serviceId, changeId) = (ticket.Id, service.Id, change.Id);
        }
        await using (var db = factory.CreateDbContext(TenantB))
        {
            var ticket = new Ticket { Description = "Annen tenant" };
            db.Tickets.Add(ticket);
            await db.SaveChangesAsync();
            ticketB = ticket.Id;
        }

        var foreign = await owner.Client.PutAsJsonAsync($"/api/risks/{created.Risk.Id}/links",
            new { ticketIds = new[] { ticketB } });
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);

        var links = await (await owner.Client.PutAsJsonAsync($"/api/risks/{created.Risk.Id}/links", new
        {
            changeIds = new[] { changeId }, serviceIds = new[] { serviceId }, ticketIds = new[] { ticketA },
        })).Content.ReadFromJsonAsync<RiskLinksInput>(Json);
        Assert.Equal([changeId], links!.ChangeIds);
        Assert.Equal([ticketA], links.TicketIds);

        var changeDetails = await (await owner.Client.GetAsync($"/api/changes/{changeId}")).Content.ReadFromJsonAsync<ChangeDetailsDto>(Json);
        Assert.Contains(created.Risk.Id, changeDetails!.LinkedRiskIds!);
    }

    [Fact]
    public async Task Matrix_counts_open_risks_by_likelihood_and_consequence()
    {
        var owner = await AgentAsync("MatrixOwner", GovernanceRoles.RiskOwner);
        var before = await owner.Client.GetFromJsonAsync<RiskMatrixDto>("/api/risks/matrix", Json);

        await ReadAsync(await owner.Client.PostAsJsonAsync("/api/risks", Input(5, 1)));
        await ReadAsync(await owner.Client.PostAsJsonAsync("/api/risks", Input(5, 1)));
        var closedRisk = await ReadAsync(await owner.Client.PostAsJsonAsync("/api/risks", Input(5, 1)));
        await MoveAsync(owner, closedRisk.Risk.Id, RiskStatuses.Closed, "Feilregistrert");

        var after = await owner.Client.GetFromJsonAsync<RiskMatrixDto>("/api/risks/matrix", Json);
        Assert.Equal(before!.Inherent[4][0] + 2, after!.Inherent[4][0]);
        Assert.Equal(before.OpenRisks + 2, after.OpenRisks);
    }

    [Fact]
    public async Task Risks_are_hidden_from_other_tenants_and_users_without_access()
    {
        var registrar = await AgentAsync("HiddenRegistrar");
        var created = await ReadAsync(await registrar.Client.PostAsJsonAsync("/api/risks", Input(2, 2)));

        var otherTenant = ClientFor(registrar.ObjectId, "Annen tenant", [AppRoles.HelpdeskAgent], TenantB);
        Assert.Equal(HttpStatusCode.NotFound, (await otherTenant.GetAsync($"/api/risks/{created.Risk.Id}")).StatusCode);

        var outsider = ClientFor(Guid.NewGuid().ToString(), "Utenforstående", []);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync("/api/risks")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync("/api/risks", Input(1, 1))).StatusCode);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using backend.Data;
using backend.Models;
using backend.Services;

namespace backend.Tests;

// Dashboard for endringer og risiko, C7 (ENDRING_OG_RISIKO.md). Kjente data legges i
// tenant B; tenant A brukes bare til tilgang og tenant-skille.
public class GovernanceDashboardTests(SqliteWebApplicationFactory factory)
    : IClassFixture<SqliteWebApplicationFactory>
{
    private const string TenantA = TestAuthHandler.AllowedTenantId;
    private const string TenantB = TestAuthHandler.OtherAllowedTenantId;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static ChangeRequest Change(string status, string environment, DateTime? start = null, string type = ChangeTypes.Normal) => new()
    {
        Number = $"CHG-D{Guid.NewGuid():N}"[..20],
        Title = $"Dashboard {status}",
        Description = "Syntetisk",
        Type = type,
        Status = status,
        Environment = environment,
        PlannedStart = start,
        PlannedEnd = start?.AddHours(1),
        PostReviewRequired = type == ChangeTypes.Emergency,
        RequestedByObjectId = "x",
        RequestedByName = "x",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static void StatusEvent(AppDbContext db, ChangeRequest change, string to) =>
        db.AddAuditEvent(nameof(ChangeRequest), change.Id.ToString(), AuditActions.ChangeStatusChanged,
            new { change.Number, from = "implementation", to, note = (string?)null });

    private async Task<GovernanceDashboardDto> DashboardAsync(string tenant)
    {
        var response = await factory.CreateClientInTenant(tenant, AppRoles.HelpdeskAgent).GetAsync("/api/governance/dashboard");
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<GovernanceDashboardDto>(Json))!;
    }

    [Fact]
    public async Task Figures_match_known_data()
    {
        var now = DateTime.UtcNow;
        int pendingId, ownerId;

        await using (var db = factory.CreateDbContext(TenantB))
        {
            var owner = new Employee { FirstName = "Risiko", LastName = "Eier", Email = "eier@test.invalid", EntraObjectId = Guid.NewGuid().ToString() };
            db.Employees.Add(owner);

            var pending = Change(ChangeStatuses.PendingApproval, ChangeEnvironments.Production, now.AddDays(5));
            var later = Change(ChangeStatuses.Scheduled, ChangeEnvironments.Production, now.AddDays(20));
            var succeeded = Change(ChangeStatuses.Closed, ChangeEnvironments.Test);
            var failed = Change(ChangeStatuses.Failed, ChangeEnvironments.Test);
            var rolledBack = Change(ChangeStatuses.RolledBack, ChangeEnvironments.Test, type: ChangeTypes.Emergency);
            var noReview = Change(ChangeStatuses.Completed, ChangeEnvironments.Production, type: ChangeTypes.Emergency);
            var draft = Change(ChangeStatuses.Draft, ChangeEnvironments.Production, now.AddDays(2));
            db.ChangeRequests.AddRange(pending, later, succeeded, failed, rolledBack, noReview, draft);
            await db.SaveChangesAsync();
            (pendingId, ownerId) = (pending.Id, owner.Id);

            StatusEvent(db, succeeded, ChangeStatuses.Completed);
            StatusEvent(db, failed, ChangeStatuses.Failed);
            StatusEvent(db, rolledBack, ChangeStatuses.RolledBack);
            StatusEvent(db, noReview, ChangeStatuses.Completed);

            Risk NewRisk(int l, int c, string status, int? ownerEmployee = null, string? strategy = null, DateTime? due = null) => new()
            {
                Number = $"RISK-D{Guid.NewGuid():N}"[..20], Title = "Dashboardrisiko", Description = "Syntetisk",
                Likelihood = l, Consequence = c, Status = status, OwnerEmployeeId = ownerEmployee, Strategy = strategy,
                DueDate = due, CreatedAt = now, UpdatedAt = now,
            };

            var critical = NewRisk(5, 4, RiskStatuses.Identified);
            var awaiting = NewRisk(2, 2, RiskStatuses.Assessed, owner.Id, RiskStrategies.Accept);
            var accepted = NewRisk(2, 3, RiskStatuses.Accepted, owner.Id, RiskStrategies.Accept);
            var overdue = NewRisk(3, 3, RiskStatuses.InTreatment, owner.Id, RiskStrategies.Mitigate, now.AddDays(-2));
            var closed = NewRisk(5, 5, RiskStatuses.Closed);
            db.Risks.AddRange(critical, awaiting, accepted, overdue, closed);
            db.GovernanceRoleAssignments.Add(new GovernanceRoleAssignment { Role = GovernanceRoles.RiskOwner, EmployeeId = owner.Id });
            await db.SaveChangesAsync();

            db.RiskAcceptances.Add(new RiskAcceptance
            {
                RiskId = accepted.Id, ActorObjectId = "x", ActorName = "x", DecidedAt = now,
                ScoreAtDecision = accepted.Score, ValidUntil = now.AddDays(10),
            });
            await db.SaveChangesAsync();
        }

        var d = await DashboardAsync(TenantB);

        Assert.Equal(6, d.OpenChanges);
        Assert.Equal(1, d.PendingApproval);
        Assert.Equal([pendingId], d.UpcomingProduction.Select(u => u.Id));
        Assert.Equal(2, d.Successful90Days);
        Assert.Equal(1, d.Failed90Days);
        Assert.Equal(1, d.RolledBack90Days);
        Assert.Equal(1, d.MissingPostReview);

        Assert.Equal(4, d.OpenRisks);
        Assert.Equal(1, d.CriticalRisks);
        Assert.Equal(1, d.UnownedRisks);
        Assert.Equal(1, d.AwaitingAcceptance);
        Assert.Equal(1, d.AcceptedRisks);
        Assert.Equal(1, d.OverdueActions);
        Assert.Equal(1, d.AcceptanceExpiringSoon);
        Assert.Equal(1, d.Matrix[4][3]);
        Assert.Equal(0, d.Matrix[4][4]); // lukket risiko telles ikke

        var ownerLoad = Assert.Single(d.RiskOwners);
        Assert.Equal(ownerId, ownerLoad.EmployeeId);
        Assert.Equal(3, ownerLoad.OpenRisks);

        // Påkrevd i godkjenningsreglene, men ikke tildelt noen
        var manager = d.Roles.Single(r => r.Role == GovernanceRoles.ChangeManager);
        Assert.True(manager.RequiredByRules);
        Assert.Equal(0, manager.People + manager.Groups);
        Assert.Equal(1, d.Roles.Single(r => r.Role == GovernanceRoles.RiskOwner).People);
    }

    [Fact]
    public async Task Dashboard_requires_access_and_shows_only_own_tenant()
    {
        var outsider = factory.CreateClientInTenant(TenantA);
        outsider.DefaultRequestHeaders.Add(TestAuthHandler.ObjectIdHeader, Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync("/api/governance/dashboard")).StatusCode);

        // Tenant A har ingen endringer eller risikoer i denne testklassen
        var d = await DashboardAsync(TenantA);
        Assert.Equal(0, d.OpenChanges);
        Assert.Equal(0, d.OpenRisks);
    }
}

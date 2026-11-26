using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using backend.Data;
using backend.Models;
using backend.Services;

namespace backend.Tests;

// Change Management og Risk Management, C1 (ENDRING_OG_RISIKO.md): datamodellen,
// løpenumre, beregnede risikoscorer, kontroller i databasen, beslutninger som ikke
// kan endres, tenant-filter og standardoppsett. Hver test bruker egne tenant-ID-er.
public class GovernanceModelTests(SqliteWebApplicationFactory factory)
    : IClassFixture<SqliteWebApplicationFactory>
{
    private static string NewTenant() => Guid.NewGuid().ToString();

    private IServiceScope Scope(string tenantId) =>
        factory.Services.GetRequiredService<IServiceScopeFactory>().CreateTenantScope(tenantId);

    private async Task<ChangeRequest> AddChangeAsync(string tenantId, Action<ChangeRequest>? setup = null)
    {
        using var scope = Scope(tenantId);
        var numbers = scope.ServiceProvider.GetRequiredService<GovernanceNumberService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var change = new ChangeRequest
        {
            Number = await numbers.NextChangeNumberAsync(),
            Title = "Oppgrader testserver",
            Description = "Syntetisk testendring",
            RequestedByObjectId = "00000000-0000-0000-0000-000000000001",
            RequestedByName = "Test Requester",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        setup?.Invoke(change);

        db.ChangeRequests.Add(change);
        await db.SaveChangesAsync();
        return change;
    }

    private async Task<Risk> AddRiskAsync(string tenantId, Action<Risk>? setup = null)
    {
        using var scope = Scope(tenantId);
        var numbers = scope.ServiceProvider.GetRequiredService<GovernanceNumberService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var risk = new Risk
        {
            Number = await numbers.NextRiskNumberAsync(),
            Title = "Backup ikke testet",
            Description = "Syntetisk testrisiko",
            Category = RiskCategories.BackupAndDisasterRecovery,
            Likelihood = 3,
            Consequence = 4,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        setup?.Invoke(risk);

        db.Risks.Add(risk);
        await db.SaveChangesAsync();
        return risk;
    }

    [Fact]
    public async Task Numbers_have_the_right_format_and_increase()
    {
        var tenant = NewTenant();

        var first = await AddChangeAsync(tenant);
        var second = await AddChangeAsync(tenant);
        var risk = await AddRiskAsync(tenant);

        Assert.Matches(new Regex(@"^CHG-\d{6}$"), first.Number);
        Assert.Matches(new Regex(@"^RISK-\d{6}$"), risk.Number);
        Assert.True(string.CompareOrdinal(second.Number, first.Number) > 0);
        Assert.Equal("CHG-000042", GovernanceNumberService.Format(GovernanceNumberService.ChangePrefix, 42));
        Assert.Equal("RISK-1234567", GovernanceNumberService.Format(GovernanceNumberService.RiskPrefix, 1234567));
    }

    [Fact]
    public async Task Number_is_unique_within_a_tenant_but_not_across_tenants()
    {
        var tenantA = NewTenant();
        var tenantB = NewTenant();

        var change = await AddChangeAsync(tenantA);

        // Samme nummer i en annen tenant er greit
        await AddChangeAsync(tenantB, c => c.Number = change.Number);

        // Samme nummer i samme tenant stoppes av den unike indeksen
        await Assert.ThrowsAsync<DbUpdateException>(() => AddChangeAsync(tenantA, c => c.Number = change.Number));
    }

    [Fact]
    public async Task Risk_scores_are_computed_by_the_database()
    {
        var tenant = NewTenant();
        var risk = await AddRiskAsync(tenant, r => { r.Likelihood = 4; r.Consequence = 5; });

        Assert.Equal(20, risk.Score);
        Assert.Null(risk.ResidualScore);

        await using (var db = factory.CreateDbContext(tenant))
        {
            var stored = await db.Risks.SingleAsync(r => r.Id == risk.Id);
            stored.ResidualLikelihood = 2;
            stored.ResidualConsequence = 3;
            await db.SaveChangesAsync();
        }

        await using (var db = factory.CreateDbContext(tenant))
        {
            var stored = await db.Risks.SingleAsync(r => r.Id == risk.Id);
            Assert.Equal(20, stored.Score);
            Assert.Equal(6, stored.ResidualScore);
            Assert.Equal(RiskScore.Of(4, 5), stored.Score);
        }

        var unassessed = await AddChangeAsync(tenant);
        var assessed = await AddChangeAsync(tenant, c => { c.Likelihood = 3; c.Impact = 2; });
        Assert.Null(unassessed.RiskScore);
        Assert.Equal(6, assessed.RiskScore);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(6, 3)]
    [InlineData(3, 0)]
    [InlineData(3, 6)]
    public async Task Likelihood_and_consequence_must_be_1_to_5(int likelihood, int consequence)
    {
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            AddRiskAsync(NewTenant(), r => { r.Likelihood = likelihood; r.Consequence = consequence; }));
    }

    [Fact]
    public async Task Change_risk_must_be_1_to_5()
    {
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            AddChangeAsync(NewTenant(), c => { c.Likelihood = 6; c.Impact = 1; }));
    }

    [Fact]
    public async Task Role_assignment_needs_exactly_one_holder()
    {
        var tenant = NewTenant();

        await using (var db = factory.CreateDbContext(tenant))
        {
            db.GovernanceRoleAssignments.Add(new GovernanceRoleAssignment
            {
                Role = GovernanceRoles.ChangeManager,
                EntraGroupId = Guid.NewGuid().ToString(),
            });
            await db.SaveChangesAsync();
        }

        await using (var db = factory.CreateDbContext(tenant))
        {
            db.GovernanceRoleAssignments.Add(new GovernanceRoleAssignment { Role = GovernanceRoles.ChangeManager });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task Risk_incident_needs_exactly_one_of_ticket_and_alert()
    {
        var tenant = NewTenant();
        var risk = await AddRiskAsync(tenant);

        await using var db = factory.CreateDbContext(tenant);
        db.RiskIncidents.Add(new RiskIncident { RiskId = risk.Id });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Approvals_cannot_be_changed_or_deleted_only_added()
    {
        var tenant = NewTenant();
        var change = await AddChangeAsync(tenant, c => c.ScopeHash = "hash-1");

        int approvalId;
        await using (var db = factory.CreateDbContext(tenant))
        {
            var approval = new ChangeApproval
            {
                ChangeRequestId = change.Id,
                Role = GovernanceRoles.ChangeManager,
                Decision = ApprovalDecisions.Approved,
                ApproverObjectId = "00000000-0000-0000-0000-000000000002",
                ApproverName = "Test Change Manager",
                DecidedAt = DateTime.UtcNow,
                ScopeHash = "hash-1",
            };
            db.ChangeApprovals.Add(approval);
            await db.SaveChangesAsync();
            approvalId = approval.Id;
        }

        await using (var db = factory.CreateDbContext(tenant))
        {
            var approval = await db.ChangeApprovals.SingleAsync(a => a.Id == approvalId);
            approval.Decision = ApprovalDecisions.Rejected;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        await using (var db = factory.CreateDbContext(tenant))
        {
            db.ChangeApprovals.Remove(await db.ChangeApprovals.SingleAsync(a => a.Id == approvalId));
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        // En ny beslutning legges til som en ny rad
        await using (var db = factory.CreateDbContext(tenant))
        {
            db.ChangeApprovals.Add(new ChangeApproval
            {
                ChangeRequestId = change.Id,
                Role = GovernanceRoles.ChangeManager,
                Decision = ApprovalDecisions.Rejected,
                ApproverObjectId = "00000000-0000-0000-0000-000000000002",
                ApproverName = "Test Change Manager",
                DecidedAt = DateTime.UtcNow,
                ScopeHash = "hash-1",
            });
            await db.SaveChangesAsync();
        }

        await using (var db = factory.CreateDbContext(tenant))
        {
            var decisions = await db.ChangeApprovals
                .Where(a => a.ChangeRequestId == change.Id)
                .OrderBy(a => a.Id)
                .Select(a => a.Decision)
                .ToListAsync();
            Assert.Equal([ApprovalDecisions.Approved, ApprovalDecisions.Rejected], decisions);
        }
    }

    [Fact]
    public async Task Risk_acceptance_cannot_be_changed_or_deleted()
    {
        var tenant = NewTenant();
        var risk = await AddRiskAsync(tenant);

        int acceptanceId;
        await using (var db = factory.CreateDbContext(tenant))
        {
            var acceptance = new RiskAcceptance
            {
                RiskId = risk.Id,
                ActorObjectId = "00000000-0000-0000-0000-000000000003",
                ActorName = "Test Risk Owner",
                DecidedAt = DateTime.UtcNow,
                ScoreAtDecision = risk.Score,
                ValidUntil = DateTime.UtcNow.AddMonths(6),
            };
            db.RiskAcceptances.Add(acceptance);
            await db.SaveChangesAsync();
            acceptanceId = acceptance.Id;
        }

        await using (var db = factory.CreateDbContext(tenant))
        {
            var acceptance = await db.RiskAcceptances.SingleAsync(a => a.Id == acceptanceId);
            acceptance.ValidUntil = DateTime.UtcNow.AddYears(5);
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        await using (var db = factory.CreateDbContext(tenant))
        {
            db.RiskAcceptances.Remove(await db.RiskAcceptances.SingleAsync(a => a.Id == acceptanceId));
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task Tenant_filter_hides_change_and_risk_data_from_other_tenants()
    {
        var tenantA = NewTenant();
        var tenantB = NewTenant();

        var change = await AddChangeAsync(tenantA, c => c.ScopeHash = "hash-1");
        var risk = await AddRiskAsync(tenantA);

        await using (var db = factory.CreateDbContext(tenantA))
        {
            db.RiskChanges.Add(new RiskChange { RiskId = risk.Id, ChangeRequestId = change.Id });
            db.ChangeApprovals.Add(new ChangeApproval
            {
                ChangeRequestId = change.Id, Role = GovernanceRoles.ChangeManager,
                Decision = ApprovalDecisions.Approved, ApproverObjectId = "x", ApproverName = "x",
                DecidedAt = DateTime.UtcNow, ScopeHash = "hash-1",
            });
            db.RiskAcceptances.Add(new RiskAcceptance
            {
                RiskId = risk.Id, ActorObjectId = "x", ActorName = "x",
                DecidedAt = DateTime.UtcNow, ScoreAtDecision = risk.Score,
            });
            db.GovernanceRoleAssignments.Add(new GovernanceRoleAssignment
            {
                Role = GovernanceRoles.RiskOwner, EntraGroupId = Guid.NewGuid().ToString(),
            });
            await db.SaveChangesAsync();
        }

        using (var scope = Scope(tenantA))
            await scope.ServiceProvider.GetRequiredService<GovernanceSetupService>().EnsureDefaultsAsync();

        await using var other = factory.CreateDbContext(tenantB);
        Assert.Empty(await other.ChangeRequests.ToListAsync());
        Assert.Empty(await other.Risks.ToListAsync());
        Assert.Empty(await other.RiskChanges.ToListAsync());
        Assert.Empty(await other.ChangeApprovals.ToListAsync());
        Assert.Empty(await other.RiskAcceptances.ToListAsync());
        Assert.Empty(await other.GovernanceRoleAssignments.ToListAsync());
        Assert.Empty(await other.ApprovalRules.ToListAsync());
        Assert.Empty(await other.RaciEntries.ToListAsync());
        Assert.Empty(await other.GovernanceSettings.ToListAsync());

        await using var own = factory.CreateDbContext(tenantA);
        Assert.Single(await own.ChangeRequests.ToListAsync());
        Assert.Single(await own.Risks.ToListAsync());
    }

    [Fact]
    public async Task Defaults_are_created_once_with_one_accountable_per_activity()
    {
        var tenant = NewTenant();

        using (var scope = Scope(tenant))
        {
            var setup = scope.ServiceProvider.GetRequiredService<GovernanceSetupService>();
            await setup.EnsureDefaultsAsync();
            await setup.EnsureDefaultsAsync();
        }

        await using var db = factory.CreateDbContext(tenant);
        var rules = await db.ApprovalRules.ToListAsync();
        var raci = await db.RaciEntries.ToListAsync();

        Assert.Equal(GovernanceSetupService.DefaultApprovalRules().Count, rules.Count);
        Assert.Equal(GovernanceSetupService.DefaultRaci().Count, raci.Count);
        Assert.Single(await db.GovernanceSettings.ToListAsync());

        // Produksjonsendringer krever Change Manager og System Owner
        var production = rules
            .Where(r => r.ChangeType == ChangeTypes.Normal && r.Environment == ChangeEnvironments.Production)
            .Select(r => r.RequiredRole)
            .ToList();
        Assert.Contains(GovernanceRoles.ChangeManager, production);
        Assert.Contains(GovernanceRoles.SystemOwner, production);

        // Hver aktivitet har nøyaktig én Accountable, og bare gyldige roller og bokstaver
        foreach (var activity in GovernanceActivities.All)
            Assert.Single(raci, r => r.Activity == activity && r.Raci == RaciLetters.Accountable);
        Assert.All(raci, r =>
        {
            Assert.Contains(r.Role, GovernanceRoles.All);
            Assert.Contains(r.Raci, RaciLetters.All);
        });
    }

    [Fact]
    public async Task Changes_and_risks_are_written_to_the_audit_log()
    {
        var tenant = NewTenant();
        var change = await AddChangeAsync(tenant);
        var risk = await AddRiskAsync(tenant);

        await using var db = factory.CreateDbContext(tenant);
        Assert.True(await db.AuditEvents.AnyAsync(e =>
            e.EntityType == nameof(ChangeRequest) && e.EntityId == change.Id.ToString() && e.Action == AuditActions.Created));
        Assert.True(await db.AuditEvents.AnyAsync(e =>
            e.EntityType == nameof(Risk) && e.EntityId == risk.Id.ToString() && e.Action == AuditActions.Created));
    }
}

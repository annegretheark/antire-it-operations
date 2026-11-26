using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;

namespace backend.Services;

public record UpcomingChangeDto(int Id, string Number, string Title, DateTime? PlannedStart, string Status);

public record RoleCoverageDto(string Role, int People, int Groups, bool RequiredByRules);

public record RiskOwnerLoadDto(int EmployeeId, string Name, int OpenRisks, int CriticalRisks);

public record GovernanceDashboardDto(
    // Endringer
    int OpenChanges,
    int PendingApproval,
    IReadOnlyList<UpcomingChangeDto> UpcomingProduction,
    int Successful90Days,
    int Failed90Days,
    int RolledBack90Days,
    int MissingPostReview,
    // Risiko
    int OpenRisks,
    int CriticalRisks,
    int UnownedRisks,
    int AwaitingAcceptance,
    int AcceptedRisks,
    int OverdueActions,
    int AcceptanceExpiringSoon,
    int[][] Matrix,
    int[][] ResidualMatrix,
    // Ansvar og eierskap
    IReadOnlyList<RoleCoverageDto> Roles,
    IReadOnlyList<RiskOwnerLoadDto> RiskOwners,
    DateTime GeneratedAt
);

// Nøkkeltall for endringer og risiko på dashboardet (ENDRING_OG_RISIKO.md, C7).
// Alt beregnes her fra databasen for tenanten i scopet.
public class GovernanceDashboardService
{
    public static readonly TimeSpan History = TimeSpan.FromDays(90);
    public static readonly TimeSpan UpcomingWindow = TimeSpan.FromDays(14);
    public static readonly TimeSpan ExpiringWindow = TimeSpan.FromDays(14);

    private static readonly string[] ClosedChange = [ChangeStatuses.Closed, ChangeStatuses.Rejected, ChangeStatuses.Cancelled];
    private static readonly string[] OpenForActions = [RiskStatuses.Identified, RiskStatuses.Assessed, RiskStatuses.InTreatment];

    private readonly AppDbContext _db;
    private readonly GovernanceSetupService _setup;

    public GovernanceDashboardService(AppDbContext db, GovernanceSetupService setup)
    {
        _db = db;
        _setup = setup;
    }

    public async Task<GovernanceDashboardDto> BuildAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        await _setup.EnsureDefaultsAsync(ct);
        var since = nowUtc - History;

        // ---------- Endringer ----------
        var changes = await _db.ChangeRequests.AsNoTracking()
            .Select(c => new { c.Id, c.Number, c.Title, c.Status, c.Environment, c.PlannedStart, c.ClosedAt, c.UpdatedAt,
                c.PostReviewRequired, c.PostReviewedAt, c.ImplementationResult })
            .ToListAsync(ct);

        var statusEvents = await _db.AuditEvents.AsNoTracking()
            .Where(e => e.EntityType == nameof(ChangeRequest) && e.Action == AuditActions.ChangeStatusChanged && e.OccurredAt >= since)
            .Select(e => new { e.EntityId, e.Changes })
            .ToListAsync(ct);

        // Hvilke endringer som har vært feilet eller rullet tilbake (i perioden), fra revisjonsloggen
        bool HadStatus(int id, string status) => statusEvents.Any(e =>
            e.EntityId == id.ToString() && e.Changes != null && e.Changes.Contains($"\"to\":\"{status}\""));

        var upcoming = changes
            .Where(c => c.Environment == ChangeEnvironments.Production && !ClosedChange.Contains(c.Status) &&
                c.Status != ChangeStatuses.Draft && c.PlannedStart >= nowUtc && c.PlannedStart <= nowUtc + UpcomingWindow)
            .OrderBy(c => c.PlannedStart)
            .Select(c => new UpcomingChangeDto(c.Id, c.Number, c.Title, c.PlannedStart, c.Status))
            .ToList();

        var failedOrRolledBack = changes.Where(c => HadStatus(c.Id, ChangeStatuses.Failed) || HadStatus(c.Id, ChangeStatuses.RolledBack))
            .Select(c => c.Id).ToHashSet();

        // Vellykket: gjennomført i perioden, uten feil eller rollback
        var successful = changes.Count(c => HadStatus(c.Id, ChangeStatuses.Completed) && !failedOrRolledBack.Contains(c.Id));

        // ---------- Risiko ----------
        var risks = await _db.Risks.AsNoTracking().Include(r => r.Acceptances)
            .Where(r => r.Status != RiskStatuses.Closed)
            .ToListAsync(ct);

        int[][] Grid() => Enumerable.Range(0, 5).Select(_ => new int[5]).ToArray();
        var matrix = Grid();
        var residual = Grid();
        foreach (var r in risks)
        {
            matrix[r.Likelihood - 1][r.Consequence - 1]++;
            if (r.ResidualLikelihood is { } rl && r.ResidualConsequence is { } rc)
                residual[rl - 1][rc - 1]++;
        }

        var states = risks.ToDictionary(r => r.Id, r => RiskRegisterService.AcceptanceState(r, nowUtc));

        // ---------- Ansvar og eierskap ----------
        var assignments = await _db.GovernanceRoleAssignments.AsNoTracking().ToListAsync(ct);
        var required = await _db.ApprovalRules.AsNoTracking().Select(r => r.RequiredRole).Distinct().ToListAsync(ct);

        var roles = GovernanceRoles.All
            .Select(role => new RoleCoverageDto(
                role,
                assignments.Where(a => a.Role == role && a.EmployeeId != null).Select(a => a.EmployeeId).Distinct().Count(),
                assignments.Count(a => a.Role == role && a.EntraGroupId != null),
                required.Contains(role)))
            .ToList();

        var ownerIds = risks.Where(r => r.OwnerEmployeeId != null).Select(r => r.OwnerEmployeeId!.Value).Distinct().ToList();
        var names = await _db.Employees.AsNoTracking()
            .Where(e => ownerIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => (e.FirstName + " " + e.LastName).Trim(), ct);

        var owners = risks.Where(r => r.OwnerEmployeeId != null)
            .GroupBy(r => r.OwnerEmployeeId!.Value)
            .Select(g => new RiskOwnerLoadDto(g.Key, names.GetValueOrDefault(g.Key, $"#{g.Key}"), g.Count(),
                g.Count(r => RiskLevels.For(r.Score) == RiskLevels.Critical)))
            .OrderByDescending(o => o.CriticalRisks).ThenByDescending(o => o.OpenRisks)
            .ToList();

        return new GovernanceDashboardDto(
            OpenChanges: changes.Count(c => !ClosedChange.Contains(c.Status)),
            PendingApproval: changes.Count(c => c.Status == ChangeStatuses.PendingApproval),
            UpcomingProduction: upcoming,
            Successful90Days: successful,
            Failed90Days: changes.Count(c => HadStatus(c.Id, ChangeStatuses.Failed)),
            RolledBack90Days: changes.Count(c => HadStatus(c.Id, ChangeStatuses.RolledBack)),
            MissingPostReview: changes.Count(c => c.PostReviewRequired && c.PostReviewedAt == null && c.Status == ChangeStatuses.Completed),
            OpenRisks: risks.Count,
            CriticalRisks: risks.Count(r => RiskLevels.For(r.Score) == RiskLevels.Critical),
            UnownedRisks: risks.Count(r => r.OwnerEmployeeId == null),
            AwaitingAcceptance: risks.Count(r => r.Strategy == RiskStrategies.Accept &&
                (r.Status == RiskStatuses.Assessed || r.Status == RiskStatuses.Monitoring)),
            AcceptedRisks: risks.Count(r => r.Status == RiskStatuses.Accepted && states[r.Id].Active),
            OverdueActions: risks.Count(r => r.DueDate < nowUtc && OpenForActions.Contains(r.Status)),
            AcceptanceExpiringSoon: risks.Count(r => r.Status == RiskStatuses.Accepted && states[r.Id].Active &&
                states[r.Id].Latest?.ValidUntil is { } until && until - nowUtc <= ExpiringWindow),
            Matrix: matrix,
            ResidualMatrix: residual,
            Roles: roles,
            RiskOwners: owners,
            GeneratedAt: nowUtc);
    }
}

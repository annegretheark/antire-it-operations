using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;

namespace backend.Services;

// Standardoppsett for endringer og risiko per tenant: godkjenningsregler, RACI og
// innstillinger. Lages første gang det trengs (som malene for onboarding), og kan
// deretter endres. Alt kan konfigureres; dette er bare et utgangspunkt.
public class GovernanceSetupService
{
    private readonly AppDbContext _db;
    private readonly ILogger<GovernanceSetupService> _logger;

    public GovernanceSetupService(AppDbContext db, ILogger<GovernanceSetupService> logger)
    {
        _db = db;
        _logger = logger;
    }

    // Normal i produksjon: Change Manager og System Owner. Normal i test: Change Manager.
    // Normal i utvikling: Technical Owner. Emergency (alle miljøer): Change Manager og
    // Approver (hurtiggodkjenning). Standard: ingen utover den forhåndsgodkjente malen.
    public static IReadOnlyList<ApprovalRule> DefaultApprovalRules() =>
    [
        Rule(ChangeTypes.Normal, ChangeEnvironments.Production, GovernanceRoles.ChangeManager, 1),
        Rule(ChangeTypes.Normal, ChangeEnvironments.Production, GovernanceRoles.SystemOwner, 2),
        Rule(ChangeTypes.Normal, ChangeEnvironments.Test, GovernanceRoles.ChangeManager, 1),
        Rule(ChangeTypes.Normal, ChangeEnvironments.Development, GovernanceRoles.TechnicalOwner, 1),
        Rule(ChangeTypes.Emergency, ApprovalRule.AnyEnvironment, GovernanceRoles.ChangeManager, 1),
        Rule(ChangeTypes.Emergency, ApprovalRule.AnyEnvironment, GovernanceRoles.Approver, 2),
    ];

    // Utgangspunkt for RACI: aktivitet → rolle → R, A, C eller I
    public static IReadOnlyList<RaciEntry> DefaultRaci()
    {
        const string R = RaciLetters.Responsible, A = RaciLetters.Accountable,
            C = RaciLetters.Consulted, I = RaciLetters.Informed;

        var matrix = new Dictionary<string, (string Role, string Raci)[]>
        {
            [GovernanceActivities.SubmitChange] =
            [
                (GovernanceRoles.ChangeRequester, R), (GovernanceRoles.ChangeManager, A),
                (GovernanceRoles.TechnicalOwner, C), (GovernanceRoles.SystemOwner, I),
            ],
            [GovernanceActivities.AssessChangeRisk] =
            [
                (GovernanceRoles.TechnicalOwner, R), (GovernanceRoles.ChangeManager, A),
                (GovernanceRoles.RiskOwner, C), (GovernanceRoles.SecurityOfficer, C),
                (GovernanceRoles.GdprResponsible, C),
            ],
            [GovernanceActivities.TechnicalReview] =
            [
                (GovernanceRoles.TechnicalOwner, R), (GovernanceRoles.ChangeManager, A),
                (GovernanceRoles.SystemOwner, C), (GovernanceRoles.Implementer, I),
            ],
            [GovernanceActivities.ApproveProductionChange] =
            [
                (GovernanceRoles.Approver, R), (GovernanceRoles.ChangeManager, A),
                (GovernanceRoles.SystemOwner, R), (GovernanceRoles.SecurityOfficer, C),
                (GovernanceRoles.ChangeRequester, I), (GovernanceRoles.Implementer, I),
            ],
            [GovernanceActivities.ImplementChange] =
            [
                (GovernanceRoles.Implementer, R), (GovernanceRoles.TechnicalOwner, A),
                (GovernanceRoles.ChangeManager, I), (GovernanceRoles.SystemOwner, I),
            ],
            [GovernanceActivities.VerifyChange] =
            [
                (GovernanceRoles.Reviewer, R), (GovernanceRoles.ChangeManager, A),
                (GovernanceRoles.ChangeRequester, I), (GovernanceRoles.SystemOwner, I),
            ],
            [GovernanceActivities.EmergencyPostReview] =
            [
                (GovernanceRoles.Reviewer, R), (GovernanceRoles.ChangeManager, A),
                (GovernanceRoles.SecurityOfficer, C), (GovernanceRoles.SystemOwner, I),
            ],
            [GovernanceActivities.AcceptRisk] =
            [
                (GovernanceRoles.RiskOwner, A), (GovernanceRoles.SecurityOfficer, C),
                (GovernanceRoles.GdprResponsible, C), (GovernanceRoles.ChangeManager, I),
            ],
            [GovernanceActivities.TreatRisk] =
            [
                (GovernanceRoles.TechnicalOwner, R), (GovernanceRoles.RiskOwner, A),
                (GovernanceRoles.SecurityOfficer, C), (GovernanceRoles.SystemOwner, I),
            ],
        };

        return matrix
            .SelectMany(activity => activity.Value.Select(cell =>
                new RaciEntry { Activity = activity.Key, Role = cell.Role, Raci = cell.Raci }))
            .ToList();
    }

    // Lager standardoppsettet for tenanten i scopet hvis det mangler. Kan kalles flere
    // ganger og fra flere forespørsler samtidig.
    public async Task EnsureDefaultsAsync(CancellationToken cancellationToken = default)
    {
        await SeedAsync(_db.ApprovalRules, DefaultApprovalRules, "godkjenningsregler", cancellationToken);
        await SeedAsync(_db.RaciEntries, DefaultRaci, "RACI", cancellationToken);
        await SeedAsync(_db.GovernanceSettings, () => [new GovernanceSettings()], "innstillinger", cancellationToken);
    }

    private async Task SeedAsync<T>(
        DbSet<T> set, Func<IReadOnlyList<T>> defaults, string what, CancellationToken cancellationToken)
        where T : class
    {
        if (await set.AnyAsync(cancellationToken))
            return;

        set.AddRange(defaults());

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e)
        {
            // En annen forespørsel rakk å lage oppsettet (unike indekser)
            _logger.LogInformation(e, "Standardoppsettet ({What}) for endringer fantes allerede.", what);

            foreach (var entry in _db.ChangeTracker.Entries<T>().ToList())
                entry.State = EntityState.Detached;
        }
    }

    private static ApprovalRule Rule(string type, string environment, string role, int order) =>
        new() { ChangeType = type, Environment = environment, RequiredRole = role, MinApprovals = 1, SortOrder = order };
}

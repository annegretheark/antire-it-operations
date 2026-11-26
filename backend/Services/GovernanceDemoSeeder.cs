using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;

namespace backend.Services;

public record DemoSeedResult(int ChangesCreated, int RisksCreated, IReadOnlyList<string> Skipped);

public record DemoRemoveResult(int Deleted, int Closed);

// Syntetiske demodata for endringer og risiko (ENDRING_OG_RISIKO.md, C9).
//
// Virker bare når Demo:Enabled = true og innlogget tenant er Demo:TenantId
// (standard utviklingstenanten 3af5c92b). Av som standard, og aldri satt i Azure.
// Alt er oppdiktet og merket «[DEMO]». Dataene lages gjennom de vanlige tjenestene,
// som de syntetiske testbrukerne fra C2, slik at revisjonslogg, hasher, roller og
// godkjenninger blir ekte.
public class GovernanceDemoSeeder
{
    public const string Prefix = "[DEMO] ";
    public const string DevTenantId = "3af5c92b-38f9-48ce-8746-82bb4c113032";

    // Testbrukerne (e-post starter slik) og rollene de får i demoen
    private static readonly (string Alias, string[] Roles)[] Actors =
    [
        ("test.requester", [GovernanceRoles.ChangeRequester]),
        ("test.changemanager", [GovernanceRoles.ChangeManager, GovernanceRoles.TechnicalOwner, GovernanceRoles.RiskOwner]),
        ("test.systemowner", [GovernanceRoles.SystemOwner, GovernanceRoles.Approver, GovernanceRoles.SecurityOfficer]),
        ("test.implementer", [GovernanceRoles.Implementer]),
        ("test.reviewer", [GovernanceRoles.Reviewer]),
    ];

    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _configuration;
    private readonly ITenantContext _tenant;

    public GovernanceDemoSeeder(IServiceScopeFactory scopes, IConfiguration configuration, ITenantContext tenant)
    {
        _scopes = scopes;
        _configuration = configuration;
        _tenant = tenant;
    }

    // Bare med Demo:Enabled og i utviklingstenanten
    public bool Allowed =>
        _configuration.GetValue("Demo:Enabled", false) &&
        string.Equals(_tenant.TenantId, _configuration["Demo:TenantId"] ?? DevTenantId, StringComparison.OrdinalIgnoreCase);

    private record Actor(string ObjectId, int EmployeeId, string Name);

    // Et eget scope der tjenestene opptrer som testbrukeren
    private IServiceScope As(Actor actor)
    {
        var scope = _scopes.CreateTenantScope(_tenant.TenantId!);
        scope.ServiceProvider.GetRequiredService<HttpCurrentUser>().UseIdentity(actor.ObjectId, actor.Name);
        return scope;
    }

    public async Task<DemoSeedResult> SeedAsync(CancellationToken ct = default)
    {
        if (!Allowed)
            throw GovernanceException.NotFound();

        using var setupScope = _scopes.CreateTenantScope(_tenant.TenantId!);
        var db = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Testbrukerne må være synket inn som ansatte (med Entra-ID)
        var actors = new Dictionary<string, Actor>();
        var missing = new List<string>();
        foreach (var (alias, roles) in Actors)
        {
            var employee = await db.Employees.FirstOrDefaultAsync(
                e => e.Email.ToLower().StartsWith(alias + "@") && e.EntraObjectId != null && e.AnonymizedAt == null, ct);
            if (employee == null)
            {
                missing.Add(alias);
                continue;
            }

            actors[alias] = new Actor(employee.EntraObjectId!, employee.Id, $"{employee.FirstName} {employee.LastName}".Trim());

            foreach (var role in roles)
            {
                if (!await db.GovernanceRoleAssignments.AnyAsync(a => a.Role == role && a.EmployeeId == employee.Id && a.ServiceCatalogItemId == null, ct))
                    db.GovernanceRoleAssignments.Add(new GovernanceRoleAssignment { Role = role, EmployeeId = employee.Id });
            }
        }

        if (missing.Count > 0)
        {
            throw GovernanceException.BadRequest(
                $"Testbrukerne mangler som ansatte: {string.Join(", ", missing)}. Kjør scripts/New-GovernanceTestUsers.ps1 og Synk fra Entra under Ansatte.");
        }

        await db.SaveChangesAsync(ct);
        await setupScope.ServiceProvider.GetRequiredService<GovernanceSetupService>().EnsureDefaultsAsync(ct);

        var serviceId = await DemoServiceAsync(db, ct);
        var templateId = await DemoTemplateAsync(db, actors["test.changemanager"], ct);

        var existingChanges = await db.ChangeRequests.Where(c => c.Title.StartsWith(Prefix)).Select(c => c.Title).ToListAsync(ct);
        var existingRisks = await db.Risks.Where(r => r.Title.StartsWith(Prefix)).Select(r => r.Title).ToListAsync(ct);
        var skipped = new List<string>();

        var changes = 0;
        var ids = new Dictionary<string, int>();

        foreach (var demo in DemoChanges())
        {
            if (existingChanges.Contains(Prefix + demo.Title))
            {
                skipped.Add(Prefix + demo.Title);
                continue;
            }

            ids[demo.Title] = await CreateChangeAsync(demo, actors, serviceId, templateId, ct);
            changes++;
        }

        var risks = 0;
        foreach (var demo in DemoRisks())
        {
            if (existingRisks.Contains(Prefix + demo.Title))
            {
                skipped.Add(Prefix + demo.Title);
                continue;
            }

            await CreateRiskAsync(demo, actors, serviceId, demo.LinkTo != null && ids.TryGetValue(demo.LinkTo, out var linked) ? linked : null, ct);
            risks++;
        }

        return new DemoSeedResult(changes, risks, skipped);
    }

    // ---------- Endringer ----------

    private record DemoChange(string Title, string Type, string Environment, string Goal, string? Rejection = null);

    private static IEnumerable<DemoChange> DemoChanges() =>
    [
        new("Oppgrader lastbalanserer i produksjon", ChangeTypes.Normal, ChangeEnvironments.Production, ChangeStatuses.Draft),
        new("Ny regel for passordpolicy", ChangeTypes.Normal, ChangeEnvironments.Production, ChangeStatuses.PendingApproval),
        new("Flytt logglagring til ny konto", ChangeTypes.Normal, ChangeEnvironments.Test, ChangeStatuses.Approved),
        new("Oppdater operativsystem på appserver", ChangeTypes.Normal, ChangeEnvironments.Production, ChangeStatuses.Scheduled),
        new("Innfør ny overvåkingsregel", ChangeTypes.Normal, ChangeEnvironments.Test, ChangeStatuses.Implementation),
        new("Bytt DNS-leverandør for testdomene", ChangeTypes.Normal, ChangeEnvironments.Test, ChangeStatuses.Closed),
        new("Åpne ny port mot internett", ChangeTypes.Normal, ChangeEnvironments.Production, ChangeStatuses.Rejected,
            "Avvist: åpningen er ikke begrunnet godt nok."),
        new("Oppgrader biblioteker i API-et", ChangeTypes.Normal, ChangeEnvironments.Test, ChangeStatuses.RolledBack),
        new("Steng sårbar tjeneste umiddelbart", ChangeTypes.Emergency, ChangeEnvironments.Production, ChangeStatuses.Completed),
        new("Månedlig patching av testservere", ChangeTypes.Standard, ChangeEnvironments.Test, ChangeStatuses.Scheduled),
    ];

    private async Task<int> CreateChangeAsync(
        DemoChange demo, Dictionary<string, Actor> a, int serviceId, int templateId, CancellationToken ct)
    {
        var start = DateTime.UtcNow.Date.AddDays(demo.Goal is ChangeStatuses.Scheduled or ChangeStatuses.Approved ? 4 : 10).AddHours(18);
        var input = new ChangeInput(
            Title: Prefix + demo.Title,
            Description: $"Syntetisk demoendring: {demo.Title.ToLowerInvariant()}.",
            Justification: "Demodata for prototypen.",
            ExpectedBenefit: "Viser hvordan prosessen virker.",
            Type: demo.Type,
            Priority: demo.Type == ChangeTypes.Emergency ? TicketPriorities.Critical : TicketPriorities.Normal,
            Environment: demo.Environment,
            Likelihood: 2,
            Impact: demo.Environment == ChangeEnvironments.Production ? 3 : 2,
            RiskAnalysis: "Lav til middels risiko; kan rulles tilbake.",
            ImpactAnalysis: "Kort nedetid i vedlikeholdsvinduet.",
            SecurityImpact: "Ingen svekkelse av sikkerheten.",
            GdprImpact: "Ingen personopplysninger berøres.",
            ProcessingActivityId: null,
            CostChangePerMonth: null,
            CostChangeNote: null,
            BackupStatus: BackupStatuses.Verified,
            BackupNote: null,
            ImplementationPlan: "1. Varsle brukere. 2. Gjennomfør endringen. 3. Kontroller tjenesten.",
            TestPlan: "Røyktest av tjenesten etter endringen.",
            TestResults: "Røyktest grønn i testmiljøet.",
            RollbackPlan: "Gjenopprett forrige konfigurasjon fra kildekontroll.",
            PlannedStart: demo.Type == ChangeTypes.Emergency ? null : start,
            PlannedEnd: demo.Type == ChangeTypes.Emergency ? null : start.AddHours(1),
            ImplementerEmployeeId: null,
            ServiceIds: [serviceId],
            AzureScopes: [],
            TemplateId: demo.Type == ChangeTypes.Standard ? templateId : null);

        int id;
        using (var scope = As(a["test.requester"]))
            id = (await scope.ServiceProvider.GetRequiredService<ChangeWorkflowService>().CreateAsync(input, isAgent: true, ct)).Id;

        if (demo.Goal == ChangeStatuses.Draft)
            return id;

        await MoveAsync(a["test.requester"], id, ChangeStatuses.Submitted, ct);

        if (demo.Type == ChangeTypes.Standard)
            return id; // går rett til planlagt gjennom malen

        if (demo.Type == ChangeTypes.Normal)
        {
            await MoveAsync(a["test.changemanager"], id, ChangeStatuses.RiskAssessment, ct);
            await MoveAsync(a["test.changemanager"], id, ChangeStatuses.TechnicalReview, ct);
            await MoveAsync(a["test.changemanager"], id, ChangeStatuses.Testing, ct);
            await MoveAsync(a["test.changemanager"], id, ChangeStatuses.PendingApproval, ct);
        }

        if (demo.Goal == ChangeStatuses.PendingApproval)
            return id;

        if (demo.Goal == ChangeStatuses.Rejected)
        {
            await DecideAsync(a["test.systemowner"], id, GovernanceRoles.SystemOwner, ApprovalDecisions.Rejected, demo.Rejection, ct);
            return id;
        }

        // Godkjenning etter reglene for type og miljø (forskjellige personer)
        var requirements = await RequirementsAsync(a["test.changemanager"], id, ct);
        foreach (var role in requirements)
        {
            var approver = role switch
            {
                GovernanceRoles.ChangeManager or GovernanceRoles.TechnicalOwner => a["test.changemanager"],
                _ => a["test.systemowner"],
            };
            await DecideAsync(approver, id, role, ApprovalDecisions.Approved, null, ct);
        }

        if (demo.Goal == ChangeStatuses.Approved)
            return id;

        if (demo.Type == ChangeTypes.Normal)
            await MoveAsync(a["test.changemanager"], id, ChangeStatuses.Scheduled, ct);
        if (demo.Goal == ChangeStatuses.Scheduled)
            return id;

        await MoveAsync(a["test.implementer"], id, ChangeStatuses.Implementation, ct);
        if (demo.Goal == ChangeStatuses.Implementation)
            return id;

        if (demo.Goal == ChangeStatuses.RolledBack)
        {
            await MoveAsync(a["test.implementer"], id, ChangeStatuses.RolledBack, ct, "Feil i ny versjon; forrige versjon er gjenopprettet.");
            return id;
        }

        await MoveAsync(a["test.implementer"], id, ChangeStatuses.Verification, ct, "Gjennomført uten avvik.");
        await MoveAsync(a["test.reviewer"], id, ChangeStatuses.Completed, ct);
        if (demo.Goal == ChangeStatuses.Completed)
            return id; // hasteendringen står uten etterkontroll

        await MoveAsync(a["test.changemanager"], id, ChangeStatuses.Closed, ct);
        return id;
    }

    private async Task MoveAsync(Actor actor, int id, string to, CancellationToken ct, string? note = null)
    {
        using var scope = As(actor);
        await scope.ServiceProvider.GetRequiredService<ChangeWorkflowService>().TransitionAsync(id, to, note, ct);
    }

    private async Task<List<string>> RequirementsAsync(Actor actor, int id, CancellationToken ct)
    {
        using var scope = As(actor);
        var workflow = scope.ServiceProvider.GetRequiredService<ChangeWorkflowService>();
        return (await workflow.RequirementsAsync(await workflow.LoadAsync(id, ct), ct)).Select(r => r.Role).ToList();
    }

    private async Task DecideAsync(Actor actor, int id, string role, string decision, string? comment, CancellationToken ct)
    {
        using var scope = As(actor);
        var workflow = scope.ServiceProvider.GetRequiredService<ChangeWorkflowService>();
        var change = await workflow.LoadAsync(id, ct);
        await workflow.DecideAsync(id, role, decision, comment, change.ScopeHash, ct);
    }

    // ---------- Risikoer ----------

    private record DemoRisk(string Title, string Category, int L, int C, string Goal, string? Strategy = null,
        bool Owner = true, int? DueInDays = null, int? AcceptDays = null, int? ResidualL = null, int? ResidualC = null,
        string? LinkTo = null);

    private static IEnumerable<DemoRisk> DemoRisks() =>
    [
        new("Kritisk sårbarhet i eksponert tjeneste", RiskCategories.InformationSecurity, 5, 4, RiskStatuses.Identified, Owner: false),
        new("Gjenoppretting av backup er ikke testet", RiskCategories.BackupAndDisasterRecovery, 4, 4, RiskStatuses.InTreatment,
            RiskStrategies.Mitigate, DueInDays: -5, LinkTo: "Oppgrader biblioteker i API-et"),
        new("Behandlingsprotokollen er ufullstendig", RiskCategories.GdprAndCompliance, 3, 3, RiskStatuses.Accepted,
            RiskStrategies.Accept, AcceptDays: 10),
        new("For mange globale administratorer", RiskCategories.IdentityAndAccessManagement, 3, 4, RiskStatuses.Assessed,
            RiskStrategies.Mitigate, LinkTo: "Ny regel for passordpolicy"),
        new("Ubrukte ressurser øker kostnadene", RiskCategories.FinancialAndFinOps, 2, 3, RiskStatuses.Monitoring,
            RiskStrategies.Mitigate, DueInDays: 30),
        new("Manuelle rutiner for driftsoppgaver", RiskCategories.OperationalRisk, 2, 2, RiskStatuses.Identified),
        new("Utdatert rammeverk i eldre tjeneste", RiskCategories.TechnicalDebt, 3, 2, RiskStatuses.InTreatment,
            RiskStrategies.Mitigate, DueInDays: 45),
        new("Én region uten reserve", RiskCategories.Availability, 4, 3, RiskStatuses.Assessed, RiskStrategies.Mitigate,
            ResidualL: 2, ResidualC: 2),
        new("Uklart eierskap til delte tjenester", RiskCategories.Governance, 1, 3, RiskStatuses.Closed, RiskStrategies.Mitigate),
        new("Avhengighet av én leverandør", RiskCategories.SupplierRisk, 2, 4, RiskStatuses.Identified),
    ];

    private async Task CreateRiskAsync(DemoRisk demo, Dictionary<string, Actor> a, int serviceId, int? changeId, CancellationToken ct)
    {
        var owner = a["test.changemanager"];
        var input = new RiskInput(
            Title: Prefix + demo.Title,
            Description: $"Syntetisk demorisiko: {demo.Title.ToLowerInvariant()}.",
            Category: demo.Category,
            Likelihood: demo.L,
            Consequence: demo.C,
            ExistingControls: "Delvis dekket av dagens rutiner.",
            RecommendedActions: "Gjennomgå og lukk gapet innen fristen.",
            ResidualLikelihood: demo.ResidualL,
            ResidualConsequence: demo.ResidualC,
            Strategy: demo.Strategy,
            OwnerEmployeeId: demo.Owner ? owner.EmployeeId : null,
            ActionOwnerEmployeeId: demo.DueInDays != null ? a["test.implementer"].EmployeeId : null,
            DueDate: demo.DueInDays is { } due ? DateTime.UtcNow.Date.AddDays(due) : null,
            NextReviewAt: DateTime.UtcNow.Date.AddMonths(3));

        using var scope = As(owner);
        var register = scope.ServiceProvider.GetRequiredService<RiskRegisterService>();
        var risk = await register.CreateAsync(input, isAgent: true, ct);

        await register.SetLinksAsync(risk.Id, new RiskLinksInput(changeId is { } c ? [c] : [], [serviceId], [], []), ct);

        var path = demo.Goal switch
        {
            RiskStatuses.Assessed => new[] { RiskStatuses.Assessed },
            RiskStatuses.InTreatment => [RiskStatuses.Assessed, RiskStatuses.InTreatment],
            RiskStatuses.Monitoring => [RiskStatuses.Assessed, RiskStatuses.InTreatment, RiskStatuses.Monitoring],
            RiskStatuses.Accepted => [RiskStatuses.Assessed],
            RiskStatuses.Closed => [RiskStatuses.Assessed, RiskStatuses.Closed],
            _ => [],
        };
        foreach (var to in path)
            await register.TransitionAsync(risk.Id, to, to == RiskStatuses.Closed ? "Avklart i demoen." : null, ct);

        if (demo.AcceptDays is { } days)
        {
            var current = await register.LoadAsync(risk.Id, ct);
            await register.AcceptAsync(risk.Id, "Akseptert i demoen; lav score og planlagt gjennomgang.",
                DateTime.UtcNow.AddDays(days), current.Score, ct);
        }
    }

    // ---------- Tjeneste og mal ----------

    private static async Task<int> DemoServiceAsync(AppDbContext db, CancellationToken ct)
    {
        const string name = "[DEMO] Demotjeneste";
        var existing = await db.ServiceCatalogItems.FirstOrDefaultAsync(s => s.Name == name, ct);
        if (existing != null)
            return existing.Id;

        var item = new ServiceCatalogItem { Name = name, Category = "Demo", Description = "Syntetisk tjeneste for demodata." };
        db.ServiceCatalogItems.Add(item);
        await db.SaveChangesAsync(ct);
        return item.Id;
    }

    private static async Task<int> DemoTemplateAsync(AppDbContext db, Actor manager, CancellationToken ct)
    {
        const string name = "[DEMO] Månedlig patching";
        var existing = await db.ChangeTemplates.FirstOrDefaultAsync(t => t.Name == name, ct);
        if (existing != null)
            return existing.Id;

        var template = new ChangeTemplate
        {
            Name = name,
            Description = "Forhåndsgodkjent demomal.",
            ImplementationPlan = "Installer månedlige oppdateringer på testservere.",
            TestPlan = "Kontroller at tjenestene svarer.",
            RollbackPlan = "Avinstaller oppdateringene.",
            AllowedEnvironments = $"{ChangeEnvironments.Development},{ChangeEnvironments.Test}",
            MaxRiskScore = 6,
            ApprovedByObjectId = manager.ObjectId,
            ApprovedByName = manager.Name,
            ApprovedAt = DateTime.UtcNow,
            NextReviewAt = DateTime.UtcNow.AddMonths(6),
        };
        db.ChangeTemplates.Add(template);
        await db.SaveChangesAsync(ct);
        return template.Id;
    }

    // ---------- Fjerne ----------

    // Sletter demorader uten beslutninger; resten avbrytes eller lukkes (godkjenninger og
    // aksepter kan ikke slettes). Revisjonsloggen beholdes.
    public async Task<DemoRemoveResult> RemoveAsync(CancellationToken ct = default)
    {
        if (!Allowed)
            throw GovernanceException.NotFound();

        using var scope = _scopes.CreateTenantScope(_tenant.TenantId!);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        int deleted = 0, closed = 0;

        var risks = await db.Risks.Include(r => r.Acceptances).Where(r => r.Title.StartsWith(Prefix)).ToListAsync(ct);
        foreach (var risk in risks)
        {
            if (risk.Acceptances.Count == 0)
            {
                db.Risks.Remove(risk);
                deleted++;
            }
            else if (risk.Status != RiskStatuses.Closed)
            {
                risk.Status = RiskStatuses.Closed;
                risk.ClosedAt = DateTime.UtcNow;
                closed++;
            }
        }

        var changes = await db.ChangeRequests.Include(c => c.Approvals).Where(c => c.Title.StartsWith(Prefix)).ToListAsync(ct);
        foreach (var change in changes)
        {
            if (change.Approvals.Count == 0)
            {
                db.ChangeRequests.Remove(change);
                deleted++;
            }
            else if (change.Status is not (ChangeStatuses.Closed or ChangeStatuses.Cancelled or ChangeStatuses.Rejected))
            {
                change.Status = ChangeStatuses.Cancelled;
                change.ClosedAt = DateTime.UtcNow;
                closed++;
            }
        }

        await db.SaveChangesAsync(ct);

        foreach (var template in await db.ChangeTemplates.Where(t => t.Name.StartsWith(Prefix)).ToListAsync(ct))
        {
            if (await db.ChangeRequests.AnyAsync(c => c.TemplateId == template.Id, ct))
                template.IsActive = false;
            else
                db.ChangeTemplates.Remove(template);
        }

        await db.SaveChangesAsync(ct);
        return new DemoRemoveResult(deleted, closed);
    }
}

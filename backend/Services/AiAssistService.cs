using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;

namespace backend.Services;

// Forslag fra AI: en tekst og eventuelt felt som kan fylles inn i skjemaet.
// Brukeren lagrer selv gjennom det vanlige API-et; ingenting her endrer data.
public record AiSuggestion(string Task, string Text, IReadOnlyDictionary<string, string?> Fields, bool WrittenByAi = true);

// Change and Risk-agenten (ENDRING_OG_RISIKO.md, C8). Bare forslag: agenten har ingen
// verktøy, ingen skrivetilgang og ingen roller. Til modellen sendes bare tekstfeltene i
// endringer og risikoer (vasket) og kategorier; aldri navn, e-post, ID-er eller
// abonnementsnavn (CLAUDE.md, kapittel AI).
public class AiAssistService
{
    public static readonly string[] ChangeTasks =
    [
        "risk_classification", "impact_analysis", "security_measures", "gdpr", "test_plan",
        "rollback_plan", "backup_gaps", "cost_optimization", "documentation",
    ];

    // Felt et forslag kan fylle inn i skjemaet (alt annet fra modellen ignoreres)
    private static readonly string[] AllowedFields =
    [
        "title", "description", "justification", "expectedBenefit", "likelihood", "impact", "riskAnalysis",
        "impactAnalysis", "securityImpact", "gdprImpact", "implementationPlan", "testPlan", "rollbackPlan",
        "backupNote", "costChangeNote",
    ];

    private readonly AppDbContext _db;
    private readonly AiService _ai;
    private readonly ICurrentUser _currentUser;

    public AiAssistService(AppDbContext db, AiService ai, ICurrentUser currentUser)
    {
        _db = db;
        _ai = ai;
        _currentUser = currentUser;
    }

    // Navnene på alle ansatte i tenanten (fullt navn, fornavn og etternavn), til vasking
    private async Task<List<string>> PersonNamesAsync(CancellationToken ct)
    {
        var people = await _db.Employees.AsNoTracking().Select(e => new { e.FirstName, e.LastName }).ToListAsync(ct);
        return people
            .SelectMany(p => new[] { $"{p.FirstName} {p.LastName}".Trim(), p.FirstName, p.LastName })
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Det som sendes om en endring. Personer omtales med rollen sin; tjenester bare med kategori.
    public async Task<object> ChangePayloadAsync(ChangeRequest change, CancellationToken ct)
    {
        var names = await PersonNamesAsync(ct);
        string? S(string? text) => AiTextScrubber.Scrub(text, names);

        var serviceIds = change.Services.Select(s => s.ServiceCatalogItemId).ToList();
        var categories = await _db.ServiceCatalogItems.AsNoTracking()
            .Where(s => serviceIds.Contains(s.Id)).Select(s => s.Category).Distinct().ToListAsync(ct);

        return new
        {
            type = change.Type,
            environment = change.Environment,
            priority = change.Priority,
            title = S(change.Title),
            description = S(change.Description),
            justification = S(change.Justification),
            expectedBenefit = S(change.ExpectedBenefit),
            likelihood = change.Likelihood,
            impact = change.Impact,
            riskAnalysis = S(change.RiskAnalysis),
            impactAnalysis = S(change.ImpactAnalysis),
            securityImpact = S(change.SecurityImpact),
            gdprImpact = S(change.GdprImpact),
            backupStatus = change.BackupStatus,
            implementationPlan = S(change.ImplementationPlan),
            testPlan = S(change.TestPlan),
            testResults = S(change.TestResults),
            rollbackPlan = S(change.RollbackPlan),
            costChangePerMonth = change.CostChangePerMonth,
            serviceCategories = categories,
            affectedServices = serviceIds.Count,
            affectedAzureSubscriptions = change.AzureScopes.Select(a => a.AzureSubscriptionId).Distinct().Count(),
            people = new { requester = "innmelder", implementer = change.ImplementerEmployeeId == null ? null : "implementør" },
        };
    }

    public async Task<AiSuggestion> AssistChangeAsync(ChangeRequest change, string? task, CancellationToken ct)
    {
        if (task == null || !ChangeTasks.Contains(task))
            throw GovernanceException.BadRequest("Ukjent oppgave for AI.");

        var input = new { task, change = await ChangePayloadAsync(change, ct) };
        var result = await _ai.CompleteAsync("change-assist", input, _currentUser.ObjectId, ct);
        await LogAsync(nameof(ChangeRequest), change.Id.ToString(), task, change.Number, ct);
        return ToSuggestion(task, result);
    }

    public async Task<AiSuggestion> DraftChangeAsync(string? text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4000)
            throw GovernanceException.BadRequest("Beskriv endringen kort (maks 4000 tegn).");

        var input = new { task = "draft", text = AiTextScrubber.Scrub(text, await PersonNamesAsync(ct)) };
        var result = await _ai.CompleteAsync("change-assist", input, _currentUser.ObjectId, ct);
        await LogAsync(nameof(ChangeRequest), "-", "draft", null, ct);
        return ToSuggestion("draft", result);
    }

    // Oppsummering for ledelsen: tall fra dashboardet og åpne høye/kritiske risikoer
    // (titler vasket, kategorier, score, status og strategi)
    public async Task<AiSuggestion> SummarizeRisksAsync(GovernanceDashboardDto dashboard, CancellationToken ct)
    {
        var names = await PersonNamesAsync(ct);
        var top = await _db.Risks.AsNoTracking()
            .Where(r => r.Status != RiskStatuses.Closed && r.Score >= 10)
            .OrderByDescending(r => r.Score).Take(15)
            .Select(r => new { r.Title, r.Category, r.Score, r.Status, r.Strategy, HasOwner = r.OwnerEmployeeId != null })
            .ToListAsync(ct);

        var input = new
        {
            task = "management_summary",
            figures = new
            {
                dashboard.OpenRisks, dashboard.CriticalRisks, dashboard.UnownedRisks, dashboard.AwaitingAcceptance,
                dashboard.AcceptedRisks, dashboard.OverdueActions, dashboard.AcceptanceExpiringSoon,
                dashboard.OpenChanges, dashboard.PendingApproval, dashboard.Failed90Days, dashboard.RolledBack90Days,
            },
            highAndCriticalRisks = top.Select(r => new
            {
                title = AiTextScrubber.Scrub(r.Title, names), r.Category, r.Score, r.Status, r.Strategy, r.HasOwner,
            }),
        };

        var result = await _ai.CompleteAsync("risk-summary", input, _currentUser.ObjectId, ct);
        await LogAsync(nameof(Risk), "-", "management_summary", null, ct);
        return ToSuggestion("management_summary", result);
    }

    // Bare tekst og tillatte felt tas med; svaret behandles aldri som en kommando
    private static AiSuggestion ToSuggestion(string task, JsonElement result)
    {
        var text = result.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";

        var fields = new Dictionary<string, string?>();
        if (result.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in f.EnumerateObject().Where(p => AllowedFields.Contains(p.Name)))
            {
                var value = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.GetRawText(),
                    _ => null,
                };
                if (value != null)
                    fields[property.Name] = value.Length > 8000 ? value[..8000] : value;
            }
        }

        return new AiSuggestion(task, text.Length > 8000 ? text[..8000] : text, fields);
    }

    // Revisjonslogg: hvilken oppgave og hvilken endring, ikke innholdet
    private async Task LogAsync(string entityType, string entityId, string task, string? number, CancellationToken ct)
    {
        _db.AddAuditEvent(entityType, entityId, AuditActions.AiSuggestionRequested, new { task, number });
        await _db.SaveChangesAsync(ct);
    }
}

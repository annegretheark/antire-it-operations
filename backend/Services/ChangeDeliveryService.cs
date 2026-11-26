using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;

namespace backend.Services;

// En simulert Terraform-plan: teksten, hashen og antall endringer
public record SimulatedPlan(string Text, string Hash, int Add, int Change, int Destroy);

// Svar fra sjekken før produksjonssetting (som en «required check» i en pipeline)
public record DeploymentGateResult(string ChangeNumber, string Environment, bool Allowed, string Reason, bool Simulated);

// Leverandør av GitHub- og Terraform-integrasjon. I prototypen finnes bare den
// simulerte (MockDeliveryProvider), som aldri gjør kall ut av appen.
public interface IChangeDeliveryProvider
{
    string Name { get; }

    SimulatedPlan CreatePlan(ChangeRequest change, IReadOnlyList<string> scopes, DateTime nowUtc);
}

// Simulert GitHub og Terraform: lager en syntetisk plan ut fra endringens Azure-omfang.
// Ingen nettverkskall, ingen ekte Terraform, ingen endring i infrastruktur.
public class MockDeliveryProvider : IChangeDeliveryProvider
{
    public string Name => "mock";

    public SimulatedPlan CreatePlan(ChangeRequest change, IReadOnlyList<string> scopes, DateTime nowUtc)
    {
        // Ord i implementeringsplanen som betyr at noe fjernes, gir «destroy» i planen
        var plan = (change.ImplementationPlan ?? "").ToLowerInvariant();
        var destroy = new[] { "slett", "fjern", "delete", "destroy", "remove" }.Any(plan.Contains) ? 1 : 0;

        var text = new StringBuilder()
            .AppendLine($"# SIMULERT Terraform-plan for {change.Number} ({change.Environment})")
            .AppendLine($"# Laget {nowUtc:O} av appens mock. Ingen ekte ressurser er lest eller endret.");

        foreach (var scope in scopes)
            text.AppendLine($"  ~ update in-place: {scope}");
        if (scopes.Count == 0)
            text.AppendLine("  + create: simulated_resource.example");
        if (destroy > 0)
            text.AppendLine("  - destroy: simulated_resource.old");

        var change_ = scopes.Count;
        var add = scopes.Count == 0 ? 1 : 0;
        text.AppendLine($"Plan: {add} to add, {change_} to change, {destroy} to destroy.");

        var body = text.ToString();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        return new SimulatedPlan(body, hash, add, change_, destroy);
    }
}

// Leveranse for endringer (ENDRING_OG_RISIKO.md, C6): simulert plan, sjekken før
// produksjonssetting og simulert Apply. Produksjon avvises alltid i prototypen.
public class ChangeDeliveryService
{
    private readonly AppDbContext _db;
    private readonly ChangeWorkflowService _workflow;
    private readonly IChangeDeliveryProvider _provider;
    private readonly IConfiguration _configuration;

    public ChangeDeliveryService(
        AppDbContext db, ChangeWorkflowService workflow, IChangeDeliveryProvider provider, IConfiguration configuration)
    {
        _db = db;
        _workflow = workflow;
        _provider = provider;
        _configuration = configuration;
    }

    public string ProviderName => _provider.Name;

    // Bare den simulerte leverandøren finnes; funksjonen kan slås av per tenant
    public async Task EnsureEnabledAsync(CancellationToken ct)
    {
        var configured = _configuration["Delivery:Provider"] ?? "mock";
        if (configured != "mock")
            throw GovernanceException.BadRequest("Ekte GitHub- og Terraform-integrasjon er ikke bygget. Bruk Delivery:Provider = mock.");

        var enabled = await _db.GovernanceSettings
            .Select(s => (bool?)s.SimulatedDeliveryEnabled)
            .FirstOrDefaultAsync(ct) ?? true;
        if (!enabled)
            throw GovernanceException.BadRequest("Simulert leveranse er slått av for denne tenanten.");
    }

    public async Task<(ChangeRequest Change, SimulatedPlan Plan)> CreatePlanAsync(int id, CancellationToken ct = default)
    {
        await EnsureEnabledAsync(ct);

        var change = await _workflow.LoadAsync(id, ct);
        var subscriptionIds = change.AzureScopes.Select(s => s.AzureSubscriptionId).ToList();
        var names = await _db.AzureSubscriptions
            .Where(s => subscriptionIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.DisplayName, ct);

        var scopes = change.AzureScopes
            .Select(s => $"{names.GetValueOrDefault(s.AzureSubscriptionId, $"#{s.AzureSubscriptionId}")}" +
                         (s.ResourceGroup == null ? "" : $"/{s.ResourceGroup}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        var plan = _provider.CreatePlan(change, scopes, DateTime.UtcNow);
        var updated = await _workflow.SetTerraformPlanAsync(id, plan.Hash,
            new { add = plan.Add, change = plan.Change, destroy = plan.Destroy }, ct);

        return (updated, plan);
    }

    // Sjekken før Apply. Ja bare når endringen er under implementering, har gyldig
    // godkjenning, miljøet stemmer og commit og plan er de som ble godkjent.
    public async Task<DeploymentGateResult> CheckGateAsync(
        ChangeRequest change, string? environment, string? commitSha, string? planHash, CancellationToken ct = default)
    {
        await EnsureEnabledAsync(ct);

        var reason = await GateProblemAsync(change, environment, commitSha, planHash, ct);
        var result = new DeploymentGateResult(change.Number, environment ?? "", reason == null, reason ?? "Godkjent endring.", true);

        _db.AddAuditEvent(nameof(ChangeRequest), change.Id.ToString(), AuditActions.ChangeGateChecked, new
        {
            change.Number, environment, commitSha, planHash, allowed = result.Allowed, reason = result.Reason, simulated = true,
        });
        await _db.SaveChangesAsync(ct);

        return result;
    }

    private async Task<string?> GateProblemAsync(
        ChangeRequest change, string? environment, string? commitSha, string? planHash, CancellationToken ct)
    {
        if (environment == ChangeEnvironments.Production)
            return "Produksjon er sperret i prototypen.";
        if (environment == null || !ChangeEnvironments.All.Contains(environment))
            return "Ukjent miljø.";
        if (environment != change.Environment)
            return $"Endringen gjelder {change.Environment}, ikke {environment}.";
        if (change.Status != ChangeStatuses.Implementation)
            return "Endringen er ikke under implementering.";
        if (change.CommitSha == null || change.TerraformPlanHash == null)
            return "Endringen mangler koblet commit eller Terraform-plan.";
        if (!string.Equals(commitSha, change.CommitSha, StringComparison.OrdinalIgnoreCase))
            return "Commit er ikke den som ble godkjent.";
        if (!string.Equals(planHash, change.TerraformPlanHash, StringComparison.OrdinalIgnoreCase))
            return "Terraform-planen er ikke den som ble godkjent.";

        var (ok, problem) = await _workflow.CheckApprovalAsync(change, ct);
        return ok ? null : problem;
    }

    // Simulert Apply i utvikling eller test (Implementer). Endrer ingen infrastruktur.
    public async Task<DeploymentGateResult> SimulateApplyAsync(int id, CancellationToken ct = default)
    {
        await EnsureEnabledAsync(ct);

        var change = await _workflow.LoadAsync(id, ct);
        var roles = await _workflow.MyRolesAsync(ct);

        if (!_workflow.IsImplementerRole(roles, change))
            throw GovernanceException.Forbidden("Simulert Apply krever rollen Implementer.");
        if (change.Environment == ChangeEnvironments.Production)
            throw GovernanceException.Forbidden("Produksjon er sperret i prototypen.");

        var result = await CheckGateAsync(change, change.Environment, change.CommitSha, change.TerraformPlanHash, ct);

        _db.AddAuditEvent(nameof(ChangeRequest), change.Id.ToString(), AuditActions.ChangeSimulatedApply, new
        {
            change.Number, environment = change.Environment, allowed = result.Allowed, reason = result.Reason, simulated = true,
        });
        await _db.SaveChangesAsync(ct);

        return result;
    }
}

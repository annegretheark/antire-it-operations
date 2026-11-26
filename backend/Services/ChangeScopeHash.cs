using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using backend.Models;

namespace backend.Services;

// SHA-256 av det en godkjenning gjelder (ENDRING_OG_RISIKO.md, C2): type, miljø,
// beskrivelse, tjenester, Azure-omfang, risiko, analyser, implementerings- og
// rollback-plan, commit og Terraform-plan. Planlagt tidspunkt er ikke med.
// Endres noe av dette, gjelder ikke tidligere godkjenninger lenger.
public static class ChangeScopeHash
{
    public static string Compute(ChangeRequest change)
    {
        var canonical = new
        {
            type = change.Type,
            environment = change.Environment,
            description = Normalize(change.Description),
            services = change.Services.Select(s => s.ServiceCatalogItemId).Distinct().Order().ToArray(),
            azure = change.AzureScopes
                .Select(s => $"{s.AzureSubscriptionId}|{(s.ResourceGroup ?? "").Trim().ToLowerInvariant()}")
                .Distinct().Order(StringComparer.Ordinal).ToArray(),
            likelihood = change.Likelihood,
            impact = change.Impact,
            riskAnalysis = Normalize(change.RiskAnalysis),
            impactAnalysis = Normalize(change.ImpactAnalysis),
            securityImpact = Normalize(change.SecurityImpact),
            gdprImpact = Normalize(change.GdprImpact),
            implementationPlan = Normalize(change.ImplementationPlan),
            rollbackPlan = Normalize(change.RollbackPlan),
            commitSha = Normalize(change.CommitSha)?.ToLowerInvariant(),
            terraformPlanHash = Normalize(change.TerraformPlanHash)?.ToLowerInvariant(),
        };

        var json = JsonSerializer.Serialize(canonical);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    // Like linjeskift og ingen mellomrom i endene, så lagring på nytt ikke endrer hashen
    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Replace("\r\n", "\n").Trim();
}

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Identity;

namespace backend.Services;

public record AzureSubscriptionInfo(string SubscriptionId, string DisplayName, string State);

// Kostnad per tjenestetype summert over abonnementene (fase 5 steg 5)
public record AzureCostResult(string? Currency, IReadOnlyDictionary<string, decimal> ByService, int Counted, int Skipped);

public record AzureResource(
    string Id,
    string Name,
    string Type,
    string Location,
    string ResourceGroup,
    string SubscriptionId,
    IReadOnlyDictionary<string, string> Tags
);

// Azure svarte med feil. Message er til brukeren (norsk).
public class AzureUnavailableException(string message, int? statusCode, Exception? inner = null)
    : Exception(message, inner)
{
    public int? StatusCode { get; } = statusCode;
}

// Azure Resource Manager og Resource Graph med appens egen tjenestekonto i
// innlogget tenant. Appen ser bare abonnementer der den har fått Reader-rollen.
// Registrert som singleton; metodene er virtual slik at testene kan bytte ut Azure.
public class AzureService
{
    private const string ArmScope = "https://management.azure.com/.default";
    private const string ArmBase = "https://management.azure.com";

    // Øvre grense for ressursoversikten (første versjon lagrer ingenting)
    public const int MaxResources = 5000;

    private readonly IHttpClientFactory _httpClients;
    private readonly AppCredentials _credentials;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public AzureService(IHttpClientFactory httpClients, IConfiguration configuration)
    {
        _httpClients = httpClients;
        // Samme innlogging som Graph (sertifikat, managed identity eller hemmelighet)
        _credentials = new AppCredentials(configuration);
    }

    public virtual async Task<List<AzureSubscriptionInfo>> ListSubscriptionsAsync(
        string tenantId, CancellationToken cancellationToken = default)
    {
        var result = new List<AzureSubscriptionInfo>();
        string? url = $"{ArmBase}/subscriptions?api-version=2022-12-01";

        while (url != null)
        {
            var page = await SendAsync<SubscriptionPage>(tenantId, HttpMethod.Get, url, null, cancellationToken);

            result.AddRange((page.Value ?? [])
                .Where(s => !string.IsNullOrWhiteSpace(s.SubscriptionId))
                .Select(s => new AzureSubscriptionInfo(
                    s.SubscriptionId!, s.DisplayName ?? s.SubscriptionId!, s.State ?? "")));

            url = page.NextLink;
        }

        return result;
    }

    // Alle ressurser i abonnementene (følger skipToken), sortert på navn
    public virtual async Task<List<AzureResource>> QueryResourcesAsync(
        string tenantId, IReadOnlyCollection<string> subscriptionIds,
        CancellationToken cancellationToken = default)
    {
        var result = new List<AzureResource>();

        if (subscriptionIds.Count == 0)
            return result;

        string? skipToken = null;

        do
        {
            var body = new
            {
                subscriptions = subscriptionIds,
                query = "Resources | project id, name, type, location, resourceGroup, subscriptionId, tags | order by name asc",
                options = new Dictionary<string, object?>
                {
                    ["$top"] = 1000,
                    ["$skipToken"] = skipToken,
                    ["resultFormat"] = "objectArray",
                },
            };

            var page = await SendAsync<ResourceGraphPage>(
                tenantId, HttpMethod.Post,
                $"{ArmBase}/providers/Microsoft.ResourceGraph/resources?api-version=2022-10-01",
                body, cancellationToken);

            result.AddRange((page.Data ?? []).Select(r => new AzureResource(
                r.Id ?? "",
                r.Name ?? "",
                r.Type ?? "",
                r.Location ?? "",
                r.ResourceGroup ?? "",
                r.SubscriptionId ?? "",
                ReadTags(r.Tags))));

            skipToken = page.SkipToken;
        }
        while (skipToken != null && result.Count < MaxResources);

        return result.Take(MaxResources).ToList();
    }

    private async Task<T> SendAsync<T>(
        string tenantId, HttpMethod method, string url, object? body, CancellationToken cancellationToken)
    {
        var credential = _credentials.For(tenantId);

        AccessToken token;

        try
        {
            token = await credential.GetTokenAsync(new TokenRequestContext([ArmScope]), cancellationToken);
        }
        catch (AuthenticationFailedException e)
        {
            throw new AzureUnavailableException(
                "Appen kunne ikke logge inn mot Azure i denne tenanten.", null, e);
        }

        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        if (body != null)
            request.Content = JsonContent.Create(body);

        using var response = await _httpClients.CreateClient(nameof(AzureService))
            .SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            var status = (int)response.StatusCode;

            throw new AzureUnavailableException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    "Appen har ikke tilgang i Azure. Gi appen rollen Reader på abonnementene (se scripts/Grant-AzureReader.ps1).",
                HttpStatusCode.TooManyRequests =>
                    "Azure begrenser antall kall akkurat nå. Prøv igjen om litt.",
                _ => $"Azure svarte med feil ({status}).",
            }, status, new HttpRequestException(detail));
        }

        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
            ?? throw new AzureUnavailableException("Tomt svar fra Azure.", (int)response.StatusCode);
    }

    private static Dictionary<string, string> ReadTags(JsonElement? tags) =>
        tags is { ValueKind: JsonValueKind.Object } t
            ? t.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ToString())
            : [];

    private sealed record SubscriptionPage(
        List<SubscriptionDto>? Value,
        [property: JsonPropertyName("nextLink")] string? NextLink);

    private sealed record SubscriptionDto(string? SubscriptionId, string? DisplayName, string? State);

    private sealed record ResourceGraphPage(
        List<ResourceDto>? Data,
        [property: JsonPropertyName("$skipToken")] string? SkipToken);

    private sealed record ResourceDto(
        string? Id, string? Name, string? Type, string? Location,
        string? ResourceGroup, string? SubscriptionId, JsonElement? Tags);

    // ---------- Kostnader (fase 5 steg 5) ----------

    // Kostnad per tjenestetype i perioden, summert over abonnementene (Cost Management
    // Query API). Abonnementer appen ikke har tilgang til, telles som hoppet over;
    // feiler alle, kastes feilen. Struping (429) prøves på nytt med ventetid.
    public virtual async Task<AzureCostResult> GetCostsAsync(
        string tenantId, IReadOnlyCollection<string> subscriptionIds, DateTime fromUtc, DateTime toUtc,
        CancellationToken cancellationToken = default)
    {
        var byService = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        string? currency = null;
        var counted = 0;
        var skipped = 0;
        AzureUnavailableException? lastError = null;

        var body = new
        {
            type = "ActualCost",
            timeframe = "Custom",
            timePeriod = new { from = fromUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"), to = toUtc.AddSeconds(-1).ToString("yyyy-MM-ddTHH:mm:ssZ") },
            dataset = new
            {
                granularity = "None",
                aggregation = new { totalCost = new { name = "Cost", function = "Sum" } },
                grouping = new[] { new { type = "Dimension", name = "ServiceName" } },
            },
        };

        foreach (var subscriptionId in subscriptionIds)
        {
            var url = $"{ArmBase}/subscriptions/{Uri.EscapeDataString(subscriptionId)}" +
                      "/providers/Microsoft.CostManagement/query?api-version=2023-11-01";

            try
            {
                var page = await SendWithRetryAsync(tenantId, url, body, cancellationToken);
                foreach (var (service, cost, rowCurrency) in ParseCostRows(page))
                {
                    byService[service] = byService.GetValueOrDefault(service) + cost;
                    currency ??= rowCurrency;
                }
                counted++;
            }
            catch (AzureUnavailableException e)
            {
                lastError = e;
                skipped++;
            }
        }

        if (counted == 0 && lastError != null)
            throw lastError;

        return new AzureCostResult(currency, byService, counted, skipped);
    }

    private async Task<JsonElement> SendWithRetryAsync(string tenantId, string url, object body, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await SendAsync<JsonElement>(tenantId, HttpMethod.Post, url, body, ct);
            }
            catch (AzureUnavailableException e) when (e.StatusCode == 429 && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(10 * attempt), ct);
            }
        }
    }

    // Radene i svaret: kolonnene finnes ved navn (Cost/PreTaxCost, ServiceName, Currency)
    public static List<(string Service, decimal Cost, string? Currency)> ParseCostRows(JsonElement page)
    {
        var result = new List<(string, decimal, string?)>();
        if (!page.TryGetProperty("properties", out var properties) ||
            !properties.TryGetProperty("columns", out var columns) ||
            !properties.TryGetProperty("rows", out var rows))
        {
            return result;
        }

        var names = columns.EnumerateArray().Select(c => c.GetProperty("name").GetString() ?? "").ToList();
        var costIndex = names.FindIndex(n => n is "Cost" or "PreTaxCost" or "CostUSD");
        var serviceIndex = names.FindIndex(n => n == "ServiceName");
        var currencyIndex = names.FindIndex(n => n == "Currency");
        if (costIndex < 0)
            return result;

        foreach (var row in rows.EnumerateArray())
        {
            var cells = row.EnumerateArray().ToList();
            var cost = cells[costIndex].ValueKind == JsonValueKind.Number ? cells[costIndex].GetDecimal() : 0m;
            var service = serviceIndex >= 0 && cells[serviceIndex].ValueKind == JsonValueKind.String
                ? cells[serviceIndex].GetString() ?? "Ukjent" : "Ukjent";
            var currency = currencyIndex >= 0 && cells[currencyIndex].ValueKind == JsonValueKind.String
                ? cells[currencyIndex].GetString() : null;
            result.Add((string.IsNullOrWhiteSpace(service) ? "Ukjent" : service, cost, currency));
        }

        return result;
    }

    // ---------- Azure Monitor-varsler (fase 4.2) ----------

    // Varsler fra de siste 7 dagene i abonnementene (Alerts Management API, krever
    // bare Reader). Abonnementer appen ikke har tilgang til, hoppes over; feiler
    // alle, kastes feilen.
    public virtual async Task<List<AlertSignal>> ListMonitorAlertsAsync(
        string tenantId, IReadOnlyCollection<string> subscriptionIds,
        CancellationToken cancellationToken = default)
    {
        var result = new List<AlertSignal>();
        AzureUnavailableException? lastError = null;
        var succeeded = 0;

        foreach (var subscriptionId in subscriptionIds)
        {
            string? url = $"{ArmBase}/subscriptions/{Uri.EscapeDataString(subscriptionId)}" +
                "/providers/Microsoft.AlertsManagement/alerts?api-version=2019-05-05-preview&timeRange=7d";

            try
            {
                while (url != null)
                {
                    var page = await SendAsync<JsonElement>(tenantId, HttpMethod.Get, url, null, cancellationToken);
                    result.AddRange(ParseMonitorAlerts(page));
                    url = page.TryGetProperty("nextLink", out var next) && next.ValueKind == JsonValueKind.String
                        ? next.GetString()
                        : null;
                }

                succeeded++;
            }
            catch (AzureUnavailableException e)
            {
                lastError = e;
            }
        }

        if (succeeded == 0 && lastError != null)
            throw lastError;

        return result;
    }

    // Gjør om en side fra Alerts Management API til varsler. Samme varselregel på
    // samme ressurs er samme varsel (ExternalId), slik at et varsel som fyrer igjen
    // etter å ha vært løst, gjenåpner saken i stedet for å gi en ny.
    public static List<AlertSignal> ParseMonitorAlerts(JsonElement page)
    {
        var result = new List<AlertSignal>();

        if (!page.TryGetProperty("value", out var items) || items.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("properties", out var properties) ||
                !properties.TryGetProperty("essentials", out var essentials))
            {
                continue;
            }

            string? Get(JsonElement element, string name) =>
                element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;

            var alertId = Get(item, "id");

            if (string.IsNullOrEmpty(alertId))
                continue;

            var rule = Get(essentials, "alertRule") ?? Get(item, "name") ?? alertId;
            var target = Get(essentials, "targetResource") ?? "";
            var externalId = $"{rule}|{target}".ToLowerInvariant();

            // Holder ID-en innenfor kolonnen (500 tegn) uten å miste entydigheten
            if (externalId.Length > 500)
            {
                externalId = "sha256:" + Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(externalId)));
            }

            var severity = Get(essentials, "severity") ?? "Sev4";
            var monitorCondition = Get(essentials, "monitorCondition");
            var alertState = Get(essentials, "alertState");
            var resolved = string.Equals(monitorCondition, "Resolved", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(alertState, "Closed", StringComparison.OrdinalIgnoreCase);

            var firedAt = DateTime.TryParse(Get(essentials, "startDateTime"), null,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var started)
                ? started
                : DateTime.UtcNow;

            var title = Get(item, "name") ?? rule.Split('/').Last();
            var targetName = Get(essentials, "targetResourceName");

            result.Add(new AlertSignal(
                backend.Models.AlertSources.AzureMonitor,
                externalId,
                targetName == null ? title : $"{title} ({targetName})",
                severity,
                string.IsNullOrEmpty(target) ? null : target,
                $"https://portal.azure.com/#view/Microsoft_Azure_Monitoring_Alerts/AlertDetails.ReactView/alertId/{Uri.EscapeDataString(alertId)}",
                resolved ? backend.Models.AlertStates.Resolved : backend.Models.AlertStates.Fired,
                firedAt,
                item.GetRawText()));
        }

        return result;
    }
}

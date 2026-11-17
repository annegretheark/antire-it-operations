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
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly ConcurrentDictionary<string, TokenCredential> _credentials = new();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public AzureService(IHttpClientFactory httpClients, IConfiguration configuration)
    {
        _httpClients = httpClients;
        _clientId = configuration["MicrosoftGraph:ClientId"]
            ?? throw new InvalidOperationException("ClientId mangler");
        _clientSecret = configuration["MicrosoftGraph:ClientSecret"]
            ?? throw new InvalidOperationException("ClientSecret mangler");
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
        var credential = _credentials.GetOrAdd(tenantId.ToLowerInvariant(),
            id => new ClientSecretCredential(id, _clientId, _clientSecret));

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
}

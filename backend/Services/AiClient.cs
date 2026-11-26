using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Core;
using Microsoft.Extensions.Caching.Memory;

namespace backend.Services;

// AI kan ikke brukes nå (av, ikke satt opp, feilet eller for mange forespørsler)
public class AiUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

// Klient for AI-modellen. Svaret er alltid et JSON-objekt som tekst; kalleren
// tolker det som data og aldri som en kommando.
public interface IAiClient
{
    Task<JsonElement> CompleteJsonAsync(string instructions, string input, CancellationToken cancellationToken = default);
}

// Azure OpenAI i Microsoft Foundry (FASE5_DRIFTSRAPPORT.md kap. 4, ENDRING_OG_RISIKO.md C8).
// OpenAI v1-API-et med deploymentnavnet som modell. Innlogging med AppCredentials
// (sertifikat lokalt, federert identitet i Azure). Ingen API-nøkkel.
//
// Konfigurasjon: Ai:Enabled (standard false), Ai:Endpoint, Ai:Deployment, Ai:TimeoutSeconds (60).
public class FoundryAiClient : IAiClient
{
    private const string Scope = "https://cognitiveservices.azure.com/.default";

    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _configuration;
    private readonly Lazy<TokenCredential> _credential;

    public FoundryAiClient(IHttpClientFactory http, IConfiguration configuration)
    {
        _http = http;
        _configuration = configuration;
        // Innloggingen lages først når AI faktisk brukes
        _credential = new Lazy<TokenCredential>(() =>
            new AppCredentials(configuration).For(configuration["AzureAd:TenantId"]
                ?? throw new AiUnavailableException("AzureAd:TenantId mangler.")));
    }

    public async Task<JsonElement> CompleteJsonAsync(string instructions, string input, CancellationToken cancellationToken = default)
    {
        var endpoint = _configuration["Ai:Endpoint"]?.TrimEnd('/');
        var deployment = _configuration["Ai:Deployment"];
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(deployment))
            throw new AiUnavailableException("AI er ikke satt opp (Ai:Endpoint og Ai:Deployment).");

        var token = await _credential.Value.GetTokenAsync(new TokenRequestContext([Scope]), cancellationToken);

        var body = JsonSerializer.Serialize(new
        {
            model = deployment,
            response_format = new { type = "json_object" },
            temperature = 0.2,
            messages = new object[]
            {
                new { role = "system", content = instructions },
                new { role = "user", content = input },
            },
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/openai/v1/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        var client = _http.CreateClient(nameof(FoundryAiClient));
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(_configuration.GetValue("Ai:TimeoutSeconds", 60), 5, 120));

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new AiUnavailableException($"AI-modellen svarte med {(int)response.StatusCode}.");

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var content = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "{}";
        return ParseObject(content);
    }

    // Svaret må være et JSON-objekt
    public static JsonElement ParseObject(string content)
    {
        try
        {
            using var parsed = JsonDocument.Parse(content);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                throw new AiUnavailableException("AI-svaret var ikke et JSON-objekt.");
            return parsed.RootElement.Clone();
        }
        catch (JsonException e)
        {
            throw new AiUnavailableException("AI-svaret var ikke gyldig JSON.", e);
        }
    }
}

// Felles inngang til AI: av/på, grense per bruker per time, instruksjonsfiler og
// tidsavbrudd. Alt annet enn et gyldig svar blir AiUnavailableException.
public class AiService
{
    private readonly IAiClient _client;
    private readonly IConfiguration _configuration;
    private readonly IMemoryCache _cache;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<AiService> _logger;
    private static readonly ConcurrentDictionary<string, string> Prompts = new();

    public AiService(IAiClient client, IConfiguration configuration, IMemoryCache cache, IWebHostEnvironment environment, ILogger<AiService> logger)
    {
        _client = client;
        _configuration = configuration;
        _cache = cache;
        _environment = environment;
        _logger = logger;
    }

    public bool Enabled => _configuration.GetValue("Ai:Enabled", false);

    public int MaxPerHour => Math.Max(1, _configuration.GetValue("Ai:MaxRequestsPerUserPerHour", 20));

    // Instruksjonene fra backend/Prompts/<name>.md
    public string Prompt(string name) => Prompts.GetOrAdd(name, n =>
        File.ReadAllText(Path.Combine(_environment.ContentRootPath, "Prompts", $"{n}.md")));

    public async Task<JsonElement> CompleteAsync(string promptName, object input, string? userKey, CancellationToken ct)
    {
        if (!Enabled)
            throw new AiUnavailableException("AI er ikke slått på (Ai:Enabled).");

        if (userKey != null)
        {
            var key = $"ai-rate:{userKey}:{DateTime.UtcNow:yyyyMMddHH}";
            var count = _cache.GetOrCreate(key, e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1); return new int[1]; })!;
            if (Interlocked.Increment(ref count[0]) > MaxPerHour)
                throw new AiUnavailableException($"Du har brukt AI {MaxPerHour} ganger denne timen. Prøv igjen senere.");
        }

        var json = JsonSerializer.Serialize(input, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        try
        {
            return await _client.CompleteJsonAsync(Prompt(promptName), json, ct);
        }
        catch (AiUnavailableException)
        {
            throw;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(e, "AI-kall ({Prompt}) feilet", promptName);
            throw new AiUnavailableException("AI-modellen svarte ikke. Prøv igjen senere.", e);
        }
    }
}

// Vasker fritekst før den sendes til modellen (CLAUDE.md, kapittel AI): e-postadresser,
// GUID-er (objekt-ID-er), Azure-ressurs-ID-er og navnene på ansatte byttes ut.
public static partial class AiTextScrubber
{
    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex Guid();

    [GeneratedRegex(@"/subscriptions/[^\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex ResourceId();

    public static string? Scrub(string? text, IReadOnlyCollection<string> personNames)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var result = ResourceId().Replace(text, "[ressurs]");
        result = Email().Replace(result, "[e-post]");
        result = Guid().Replace(result, "[id]");

        // Lengste navn først, så «Kari Nordmann» byttes før «Kari»
        foreach (var name in personNames.Where(n => n.Length >= 3).OrderByDescending(n => n.Length))
            result = Regex.Replace(result, $@"\b{Regex.Escape(name)}\b", "[person]", RegexOptions.IgnoreCase);

        return result.Length > 8000 ? result[..8000] : result;
    }
}

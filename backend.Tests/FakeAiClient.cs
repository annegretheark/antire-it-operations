using System.Collections.Concurrent;
using System.Text.Json;
using backend.Services;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace backend.Tests;

// AI-modell uten Foundry: tar vare på hver forespørsel og svarer med Response,
// eller feiler når Failure er satt
public class FakeAiClient : IAiClient
{
    public ConcurrentQueue<(string Instructions, string Input)> Requests { get; } = new();

    public string Response { get; set; } = """{ "text": "Forslag", "fields": {} }""";

    public Exception? Failure { get; set; }

    public Task<JsonElement> CompleteJsonAsync(string instructions, string input, CancellationToken cancellationToken = default)
    {
        Requests.Enqueue((instructions, input));

        if (Failure is { } failure)
            throw failure;

        return Task.FromResult(FoundryAiClient.ParseObject(Response));
    }
}

// API-et med AI slått på og den falske modellen (grense: 5 forespørsler per time)
public class AiWebApplicationFactory : SqliteWebApplicationFactory
{
    public FakeAiClient Ai => (FakeAiClient)Services.GetRequiredService<IAiClient>();

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Ai:Enabled", "true");
        builder.UseSetting("Ai:MaxRequestsPerUserPerHour", "5");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAiClient>();
            services.AddSingleton<IAiClient, FakeAiClient>();
        });
    }
}

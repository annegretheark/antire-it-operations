using System.Collections.Concurrent;
using backend.Services;

namespace backend.Tests;

// TeamsService uten Teams: tar vare på meldingene, eller feiler når Failure er satt
public class FakeTeamsService(IHttpClientFactory httpClients) : TeamsService(httpClients)
{
    public ConcurrentQueue<(string Url, string Payload)> Sent { get; } = new();

    public Exception? Failure { get; set; }

    public override Task SendAsync(string webhookUrl, string payload, CancellationToken cancellationToken = default)
    {
        if (Failure is { } failure)
            throw failure;

        Sent.Enqueue((webhookUrl, payload));
        return Task.CompletedTask;
    }
}

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;

namespace backend.Services;

// Teams svarte ikke med OK. Message er til brukeren (norsk).
public class TeamsDeliveryException(string message, Exception? inner = null) : Exception(message, inner);

// Sender meldinger til en Teams-kanal via en Workflows-webhook («Post to a
// channel when a webhook request is received»). Singleton; virtual for testene.
public class TeamsService
{
    private readonly IHttpClientFactory _httpClients;

    public TeamsService(IHttpClientFactory httpClients)
    {
        _httpClients = httpClients;
    }

    // Godtar bare Workflows-adresser over https, slik at appen ikke kan brukes
    // til å sende forespørsler til vilkårlige adresser
    public static bool IsAllowedWebhookUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.EndsWith(".logic.azure.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".powerplatform.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".webhook.office.com", StringComparison.OrdinalIgnoreCase));

    public virtual async Task SendAsync(string webhookUrl, string payload, CancellationToken cancellationToken = default)
    {
        if (!IsAllowedWebhookUrl(webhookUrl))
            throw new TeamsDeliveryException("Webhook-adressen er ikke en gyldig Teams Workflows-adresse.");

        using var content = new StringContent(payload, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        HttpResponseMessage response;

        try
        {
            response = await _httpClients.CreateClient(nameof(TeamsService))
                .PostAsync(webhookUrl, content, cancellationToken);
        }
        catch (HttpRequestException e)
        {
            throw new TeamsDeliveryException("Fikk ikke kontakt med Teams.", e);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new TeamsDeliveryException(
                    $"Teams svarte med feil ({(int)response.StatusCode}). Sjekk webhook-adressen.");
            }
        }
    }
}

// Teams-varsling per tenant: oppsett (kryptert webhook-adresse), kø og sending.
// Feil i Teams stopper aldri det som utløste meldingen; køen prøves på nytt av
// AlertMonitorService.
public class TeamsNotificationService
{
    public const string ProtectorPurpose = "TeamsWebhookUrl";

    private readonly AppDbContext _db;
    private readonly TeamsService _teams;
    private readonly IDataProtector _protector;
    private readonly string _appUrl;
    private readonly ILogger<TeamsNotificationService> _logger;

    public TeamsNotificationService(
        AppDbContext db,
        TeamsService teams,
        IDataProtectionProvider dataProtection,
        IConfiguration configuration,
        ILogger<TeamsNotificationService> logger)
    {
        _db = db;
        _teams = teams;
        _protector = dataProtection.CreateProtector(ProtectorPurpose);
        _appUrl = (configuration["App:FrontendUrl"]
            ?? configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()?.FirstOrDefault()
            ?? "http://localhost:5173").TrimEnd('/');
        _logger = logger;
    }

    public async Task<TeamsSettings?> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        await _db.TeamsSettings.FirstOrDefaultAsync(cancellationToken);

    public string? Decrypt(TeamsSettings? settings)
    {
        if (string.IsNullOrEmpty(settings?.EncryptedWebhookUrl))
            return null;

        try
        {
            return _protector.Unprotect(settings.EncryptedWebhookUrl);
        }
        catch (Exception e)
        {
            // F.eks. nøklene for Data Protection er byttet; adressen må legges inn på nytt
            _logger.LogWarning(e, "Kunne ikke dekryptere Teams-webhook-adressen.");
            return null;
        }
    }

    public string Encrypt(string webhookUrl) => _protector.Protect(webhookUrl);

    // https://prod-12.westeurope.logic.azure.com/…a1b2c3
    public static string Mask(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Host}/…{url[^Math.Min(6, url.Length)..]}"
            : "…";

    public string TicketUrl(int ticketId) => $"{_appUrl}/?sak={ticketId}";

    // Lenker til endringer og risikoer i appen (C5)
    public string ChangeUrl(int id, bool history = false) => $"{_appUrl}/?endring={id}{(history ? "&historikk=1" : "")}";
    public string RiskUrl(int id, bool history = false) => $"{_appUrl}/?risiko={id}{(history ? "&historikk=1" : "")}";

    public string OpsReportUrl(int id) => $"{_appUrl}/?driftsrapport={id}";

    // Ukentlig driftsrapport (fase 5 steg 4): bare aggregerte tall og AI-teksten
    public Task NotifyOpsReportAsync(OpsReport report, OpsReportMetrics metrics, CancellationToken cancellationToken = default) =>
        EnqueueAsync(null, "ops_report", TeamsCards.ForOpsReport(report, metrics, OpsReportUrl(report.Id)), cancellationToken);

    // Kort for endringer og risiko. Sendes bare når tenanten har slått det på (standard på).
    public async Task NotifyGovernanceAsync(string kind, string payload, CancellationToken cancellationToken = default)
    {
        var enabled = await _db.GovernanceSettings
            .Select(s => (bool?)s.TeamsNotificationsEnabled)
            .FirstOrDefaultAsync(cancellationToken) ?? true;

        if (enabled)
            await EnqueueAsync(null, kind, payload, cancellationToken);
    }

    // Ny sak fra varsel, eller sak gjenåpnet av varsel
    public Task NotifyAlertTicketAsync(Ticket ticket, Alert alert, bool reopened, CancellationToken cancellationToken = default) =>
        EnqueueAsync(ticket.Id, reopened ? "reopened" : "new_alert_ticket",
            TeamsCards.ForTicket(ticket, alert, reopened, TicketUrl(ticket.Id)), cancellationToken);

    // Ny manuell sak med prioritet kritisk, hvis tenanten har slått det på
    public async Task NotifyCriticalManualTicketAsync(Ticket ticket, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);

        if (settings?.NotifyCriticalManualTickets != true)
            return;

        await EnqueueAsync(ticket.Id, "critical_ticket",
            TeamsCards.ForTicket(ticket, null, reopened: false, TicketUrl(ticket.Id)), cancellationToken);
    }

    // Prøver meldinger som ikke er sendt ennå. Gir antall som ble sendt.
    public async Task<int> DeliverPendingAsync(CancellationToken cancellationToken = default)
    {
        var pending = await _db.TeamsNotifications
            .Where(n => n.Status == TeamsNotificationStatuses.Pending)
            .OrderBy(n => n.Id)
            .Take(50)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
            return 0;

        var url = Decrypt(await GetSettingsAsync(cancellationToken));
        var sent = 0;

        foreach (var notification in pending)
        {
            if (await TrySendAsync(notification, url, cancellationToken))
                sent++;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return sent;
    }

    // Testmelding fra Innstillinger. Sendes direkte (ikke via køen).
    public async Task SendTestAsync(CancellationToken cancellationToken = default)
    {
        var url = Decrypt(await GetSettingsAsync(cancellationToken))
            ?? throw new TeamsDeliveryException("Ingen webhook-adresse er lagt inn.");

        await _teams.SendAsync(url, TeamsCards.Test(_appUrl), cancellationToken);
    }

    private async Task EnqueueAsync(int? ticketId, string kind, string payload, CancellationToken cancellationToken)
    {
        try
        {
            var url = Decrypt(await GetSettingsAsync(cancellationToken));

            // Uten oppsett lages ingen melding
            if (url == null)
                return;

            var notification = new TeamsNotification
            {
                TicketId = ticketId,
                Kind = kind,
                Payload = payload,
                CreatedAt = DateTime.UtcNow,
            };

            _db.TeamsNotifications.Add(notification);
            await TrySendAsync(notification, url, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Kunne ikke legge Teams-melding i kø for sak {TicketId}", ticketId);
        }
    }

    private async Task<bool> TrySendAsync(TeamsNotification notification, string? url, CancellationToken cancellationToken)
    {
        notification.Attempts++;

        try
        {
            if (url == null)
                throw new TeamsDeliveryException("Ingen webhook-adresse er lagt inn.");

            await _teams.SendAsync(url, notification.Payload, cancellationToken);

            notification.Status = TeamsNotificationStatuses.Sent;
            notification.SentAt = DateTime.UtcNow;
            notification.LastError = null;
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Teams-melding {Id} feilet (forsøk {Attempt})", notification.Id, notification.Attempts);

            notification.LastError = e is TeamsDeliveryException ? e.Message : "Ukjent feil ved sending til Teams.";

            if (notification.Attempts >= TeamsNotification.MaxAttempts)
                notification.Status = TeamsNotificationStatuses.Failed;

            return false;
        }
    }
}

// Adaptive Card-meldinger i formatet Workflows-webhooken forventer
public static class TeamsCards
{
    private static readonly Dictionary<string, string> PriorityNames = new()
    {
        [TicketPriorities.Low] = "Lav",
        [TicketPriorities.Normal] = "Normal",
        [TicketPriorities.High] = "Høy",
        [TicketPriorities.Critical] = "Kritisk",
    };

    public static string ForTicket(Ticket ticket, Alert? alert, bool reopened, string ticketUrl)
    {
        var heading = alert == null
            ? $"Ny kritisk sak #{ticket.Id}"
            : reopened
                ? $"Sak #{ticket.Id} er gjenåpnet av et nytt varsel"
                : $"Ny sak #{ticket.Id} fra varsel";

        var facts = new List<object>
        {
            Fact("Prioritet", PriorityNames.GetValueOrDefault(ticket.Priority, ticket.Priority)),
            Fact("Kategori", ticket.Category),
        };

        if (alert != null)
        {
            facts.Add(Fact("Kilde", alert.Source == AlertSources.Defender ? "Microsoft Defender" : "Azure Monitor"));
            facts.Add(Fact("Alvorlighetsgrad", alert.Severity));

            if (!string.IsNullOrEmpty(alert.ResourceId))
                facts.Add(Fact("Ressurs", alert.ResourceId));

            facts.Add(Fact("Forekomster", alert.Occurrences.ToString()));
        }

        var actions = new List<object> { OpenUrl("Åpne sak", ticketUrl) };

        if (!string.IsNullOrEmpty(alert?.PortalUrl))
            actions.Add(OpenUrl("Åpne varselet", alert.PortalUrl));

        return Message(
            new { type = "TextBlock", text = heading, weight = "Bolder", size = "Medium", wrap = true },
            new { type = "TextBlock", text = alert?.Title ?? Shorten(ticket.Description), wrap = true },
            new { type = "FactSet", facts },
            actions);
    }

    public static string Test(string appUrl) =>
        Message(
            new { type = "TextBlock", text = "Testmelding fra Antire IT Operations", weight = "Bolder", size = "Medium" },
            new { type = "TextBlock", text = "Teams-varsling er satt opp riktig.", wrap = true },
            new { type = "FactSet", facts = new List<object> { Fact("Sendt", DateTime.UtcNow.ToString("u")) } },
            [OpenUrl("Åpne appen", appUrl)]);

    // Driftsrapporten: periode, de viktigste tallene og AI-oppsummeringen når den finnes
    public static string ForOpsReport(OpsReport report, OpsReportMetrics m, string reportUrl)
    {
        var norway = SlaCalculator.Norway;
        var start = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(report.PeriodStart, DateTimeKind.Utc), norway);
        var end = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(report.PeriodEnd, DateTimeKind.Utc), norway).AddDays(-1);

        var facts = new List<(string, string)>
        {
            ("Nye saker", m.Tickets.Created.ToString()),
            ("Løste saker", m.Tickets.Resolved.ToString()),
            ("Åpne kritiske saker", m.Tickets.OpenByPriority.GetValueOrDefault(TicketPriorities.Critical).ToString()),
            ("SLA-brudd (respons/løsning)", $"{m.Sla.ResponseBreaches} / {m.Sla.ResolutionBreaches}"),
            ("Nye varsler", m.Alerts.New.Sum(a => a.Count).ToString()),
            ("Bestillinger som venter", m.Access.PendingApproval.ToString()),
            ("Inaktive gjester", m.M365.InactiveGuests?.ToString() ?? "–"),
        };

        // Kostnad i Azure (steg 5), med endring fra uka før
        if (m.Costs?.Total is { } total)
        {
            var change = m.Costs.ChangePercent is { } pct ? $" ({pct.ToString("+0.0;-0.0;0.0", System.Globalization.CultureInfo.InvariantCulture)} %)" : "";
            facts.Add(("Kostnad Azure", $"{total.ToString("N0", new System.Globalization.CultureInfo("nb-NO"))} {m.Costs.Currency}{change}"));
        }

        var text = "Nøkkeltallene er beregnet av appen.";
        if (report.AiStatus == OpsReportAiStatuses.Ok && report.AiSummary != null)
        {
            var actions = report.AiActions == null
                ? []
                : JsonSerializer.Deserialize<List<string>>(report.AiActions) ?? [];
            text = $"Skrevet av AI: {Shorten(report.AiSummary)}" +
                   (actions.Count > 0 ? "\n\nTiltak:\n" + string.Join("\n", actions.Select(a => $"- {Shorten(a)}")) : "");
        }

        return Card($"Driftsrapport {start:d.M.} – {end:d.M.yyyy}", text, facts, [("Åpne rapporten", reportUrl)]);
    }

    // Generelt kort: overskrift, kort tekst, fakta og lenker (alle knapper åpner appen)
    public static string Card(string heading, string text, IEnumerable<(string Title, string Value)> facts,
        IEnumerable<(string Title, string Url)> links) =>
        Message(
            new { type = "TextBlock", text = heading, weight = "Bolder", size = "Medium", wrap = true },
            new { type = "TextBlock", text, wrap = true },
            new { type = "FactSet", facts = facts.Select(f => Fact(f.Title, f.Value)).ToList() },
            links.Select(l => OpenUrl(l.Title, l.Url)).ToList());

    private static object Fact(string title, string value) => new { title, value };

    private static object OpenUrl(string title, string url) =>
        new { type = "Action.OpenUrl", title, url };

    private static string Shorten(string text) => text.Length > 300 ? text[..297] + "…" : text;

    private static string Message(object heading, object text, object facts, List<object> actions) =>
        JsonSerializer.Serialize(new
        {
            type = "message",
            attachments = new[]
            {
                new
                {
                    contentType = "application/vnd.microsoft.card.adaptive",
                    content = new Dictionary<string, object>
                    {
                        ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                        ["type"] = "AdaptiveCard",
                        ["version"] = "1.4",
                        ["body"] = new[] { heading, text, facts },
                        ["actions"] = actions,
                    },
                },
            },
        });
}

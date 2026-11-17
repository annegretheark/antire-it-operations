namespace backend.Models;

// Et varsel fra Azure Monitor eller Microsoft Defender (fase 4). Samme varsel
// (kilde + ExternalId) lagres én gang; nye forekomster øker Occurrences.
public class Alert : ITenantOwned, IAudited
{
    public int Id { get; set; }

    // Entra-tenanten raden hører til (settes automatisk)
    public string TenantId { get; set; } = string.Empty;

    // azure-monitor | defender (AlertSources)
    public string Source { get; set; } = string.Empty;

    // Varselets ID hos kilden, unik per kilde i tenanten
    public string ExternalId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    // Slik kilden oppgir den: Sev0–Sev4 (Azure) eller high | medium | low | informational (Defender)
    public string Severity { get; set; } = string.Empty;

    public string? ResourceId { get; set; }

    // Lenke til varselet i Azure-portalen eller Defender-portalen
    public string? PortalUrl { get; set; }

    // fired | resolved (AlertStates)
    public string State { get; set; } = AlertStates.Fired;

    // Først mottatt, og sist mottatt med en endring (ny forekomst eller løst)
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }

    // Når varselet sist fyrte hos kilden; nytt tidspunkt = ny forekomst
    public DateTime LastFiredAt { get; set; }

    public int Occurrences { get; set; } = 1;

    // Hele varselet fra kilden (jsonb)
    public string? RawPayload { get; set; }

    // Saken varselet ga, om alvorlighetsgraden var høy nok
    public int? TicketId { get; set; }
}

public static class AlertSources
{
    public const string AzureMonitor = "azure-monitor";
    public const string Defender = "defender";

    public static readonly string[] All = [AzureMonitor, Defender];
}

public static class AlertStates
{
    public const string Fired = "fired";
    public const string Resolved = "resolved";
}

// Alvorlighetsgrader per kilde, fra mest til minst alvorlig, og prioriteten de gir
public static class AlertSeverities
{
    public static readonly string[] Azure = ["Sev0", "Sev1", "Sev2", "Sev3", "Sev4"];
    public static readonly string[] Defender = ["high", "medium", "low", "informational"];

    public static string[] For(string source) =>
        source == AlertSources.Defender ? Defender : Azure;

    // Lavere tall = mer alvorlig. Ukjent alvorlighetsgrad regnes som minst alvorlig.
    public static int Rank(string source, string severity)
    {
        var index = Array.FindIndex(For(source), s => s.Equals(severity, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? int.MaxValue : index;
    }

    public static bool IsValid(string source, string severity) =>
        For(source).Contains(severity, StringComparer.OrdinalIgnoreCase);

    // Azure Sev0 → kritisk, Sev1 → høy, Sev2 → normal, Sev3–4 → lav.
    // Defender høy → høy, middels → normal, lav og informasjon → lav.
    public static string PriorityFor(string source, string severity) =>
        (source, severity.ToLowerInvariant()) switch
        {
            (AlertSources.Defender, "high") => TicketPriorities.High,
            (AlertSources.Defender, "medium") => TicketPriorities.Normal,
            (AlertSources.Defender, _) => TicketPriorities.Low,
            (_, "sev0") => TicketPriorities.Critical,
            (_, "sev1") => TicketPriorities.High,
            (_, "sev2") => TicketPriorities.Normal,
            _ => TicketPriorities.Low,
        };
}

// Hvilke varsler som blir saker, per kilde i tenanten (Innstillinger → Varselregler)
public class AlertRule : ITenantOwned, IAudited
{
    public int Id { get; set; }

    public string TenantId { get; set; } = string.Empty;

    // azure-monitor | defender, én regel per kilde
    public string Source { get; set; } = string.Empty;

    // Varsler minst så alvorlige som dette blir saker; mindre alvorlige lagres bare
    public string MinSeverity { get; set; } = string.Empty;

    // Kategori på saken; styrer automatisk tildeling (ansvarsområder og grupper)
    public string Category { get; set; } = string.Empty;

    // Fast prioritet i stedet for den som følger av alvorlighetsgraden
    public string? PriorityOverride { get; set; }

    // Varsle Teams om nye og gjenåpnede saker fra varsler
    public bool NotifyTeams { get; set; } = true;

    // Av = varsler lagres, men gir aldri sak
    public bool CreateTickets { get; set; } = true;
}

// Teams-oppsett for tenanten (Innstillinger → Teams). Webhook-adressen er en
// hemmelighet og lagres kryptert med ASP.NET Data Protection.
public class TeamsSettings : ITenantOwned, IAudited
{
    public int Id { get; set; }

    public string TenantId { get; set; } = string.Empty;

    public string? EncryptedWebhookUrl { get; set; }

    // Varsle også ved nye manuelle saker med prioritet kritisk
    public bool NotifyCriticalManualTickets { get; set; }

    public DateTime UpdatedAt { get; set; }
}

// Kø for Teams-meldinger. Sendes med en gang; feiler det, prøver bakgrunnsjobben
// på nytt (maks MaxAttempts ganger).
public class TeamsNotification : ITenantOwned
{
    public const int MaxAttempts = 5;

    public int Id { get; set; }

    public string TenantId { get; set; } = string.Empty;

    public int? TicketId { get; set; }

    // new_alert_ticket | reopened | critical_ticket | test
    public string Kind { get; set; } = string.Empty;

    // Adaptive Card-meldingen som JSON
    public string Payload { get; set; } = string.Empty;

    // pending | sent | failed (TeamsNotificationStatuses)
    public string Status { get; set; } = TeamsNotificationStatuses.Pending;

    public int Attempts { get; set; }

    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? SentAt { get; set; }
}

public static class TeamsNotificationStatuses
{
    public const string Pending = "pending";
    public const string Sent = "sent";
    public const string Failed = "failed";
}

public static class TicketSources
{
    public const string Manual = "manual";
    public const string Alert = "alert";
}

namespace backend.Models;

// Ukentlig driftsrapport (fase 5, FASE5_DRIFTSRAPPORT.md). Én rapport per tenant
// og periode. Nøkkeltallene beregnes av koden (OpsReportService); AI-delen
// (oppsummering og tiltak) kommer i et senere steg.
public class OpsReport : ITenantOwned, IAudited
{
    public int Id { get; set; }

    // Entra-tenanten raden hører til (settes automatisk)
    public string TenantId { get; set; } = string.Empty;

    // Perioden i UTC: fra og med PeriodStart, til (ikke med) PeriodEnd.
    // For ukerapporten mandag 00:00 til neste mandag 00:00 norsk tid.
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }

    // OpsReportMetrics som JSON (jsonb). Bare tall og kategorinavn, aldri
    // navn, e-post, sakstekster, varseltitler eller ressurs-ID-er.
    public string Metrics { get; set; } = "{}";

    // Fra AI-modellen (senere steg)
    public string? AiSummary { get; set; }

    // Tiltakene fra modellen som JSON-liste (jsonb)
    public string? AiActions { get; set; }

    // ok | disabled | failed (OpsReportAiStatuses)
    public string AiStatus { get; set; } = OpsReportAiStatuses.Disabled;

    public DateTime CreatedAt { get; set; }

    // "system" for den ukentlige jobben, ellers innlogget bruker
    public string CreatedBy { get; set; } = string.Empty;
}

public static class OpsReportAiStatuses
{
    public const string Ok = "ok";
    public const string Disabled = "disabled";
    public const string Failed = "failed";
}

// Nøkkeltallene i rapporten (kapittel 2). «Nå»-tall gjelder tidspunktet
// rapporten ble laget (GeneratedAt), periodetall gjelder PeriodStart–PeriodEnd.
public record OpsReportMetrics(
    DateTime GeneratedAt,
    OpsTicketMetrics Tickets,
    OpsSlaMetrics Sla,
    OpsAlertMetrics Alerts,
    OpsAccessMetrics Access,
    OpsLifecycleMetrics Lifecycle,
    OpsM365Metrics M365,
    // Kostnader fra Cost Management (steg 5); null i rapporter fra før steg 5
    OpsCostMetrics? Costs = null
);

public record OpsTicketMetrics(
    int Created,
    int Resolved,
    // Åpne saker nå per prioritet (low | normal | high | critical)
    IReadOnlyDictionary<string, int> OpenByPriority,
    int OpenOlderThan14Days
);

public record OpsSlaMetrics(
    // Frister som gikk ut i perioden uten å bli nådd
    int ResponseBreaches,
    int ResolutionBreaches,
    // Åpne saker «i fare» nå
    int AtRisk
);

// Antall varsler per kilde og alvorlighetsgrad
public record OpsAlertCount(string Source, string Severity, int Count);

public record OpsAlertMetrics(
    IReadOnlyList<OpsAlertCount> New,
    // Varsler fra før perioden som fyrte igjen i perioden
    IReadOnlyList<OpsAlertCount> Recurred,
    // Kilder (azure-monitor, defender) der siste henting feilet
    IReadOnlyList<string> FailingSources
);

public record OpsAccessMetrics(
    int PendingApproval,
    // Alder i hele dager på den eldste ventende bestillingen
    int? OldestPendingDays
);

public record OpsLifecycleMetrics(
    // Aktive prosesser med start- eller sluttdato de neste 14 dagene
    int OnboardingNext14Days,
    int OffboardingNext14Days
);

public record OpsM365Metrics(
    // Gjester uten vellykket pålogging på 90 dager; null når dataene ikke er tilgjengelige
    int? InactiveGuests,
    string? UnavailableReason
);

// Kostnad for én tjenestetype (kategori fra Cost Management, f.eks. «Virtual Machines»)
public record OpsServiceCost(string Service, decimal Amount);

// Kostnader i Azure (steg 5). Bare summer og tjenestetyper: ingen abonnementsnavn,
// abonnements-ID-er eller ressurs-ID-er, slik at tallene kan sendes til AI.
public record OpsCostMetrics(
    string? Currency,
    decimal? Total,
    decimal? PreviousTotal,
    // Endring fra uka før i prosent; null når uka før var 0 eller mangler
    decimal? ChangePercent,
    IReadOnlyList<OpsServiceCost> TopServices,
    int SubscriptionsCounted,
    int SubscriptionsSkipped,
    string? UnavailableReason
);

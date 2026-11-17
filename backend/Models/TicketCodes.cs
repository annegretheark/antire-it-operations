namespace backend.Models;

// Koder for sakens status. Norske visningsnavn ligger i frontend.
public static class TicketStatuses
{
    public const string New = "new";
    public const string InProgress = "in_progress";
    public const string Resolved = "resolved";

    public static readonly string[] All = [New, InProgress, Resolved];
}

// Koder for prioritet på saker, tjenester og SLA-mål
public static class TicketPriorities
{
    public const string Low = "low";
    public const string Normal = "normal";
    public const string High = "high";
    public const string Critical = "critical";

    public static readonly string[] All = [Low, Normal, High, Critical];
}

// SLA-status for en sak (beregnes, lagres ikke)
public static class SlaStatuses
{
    // Saken har ingen frist (laget før SLA, eller prioriteten mangler mål)
    public const string None = "none";
    public const string Ok = "ok";
    // Under 25 % av tiden igjen til en frist som ikke er nådd
    public const string AtRisk = "at_risk";
    public const string Breached = "breached";
    // Løst innen begge fristene
    public const string Met = "met";
}

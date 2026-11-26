using backend.Models;

namespace backend.Services;

// Teams-kort for endringer og risiko (ENDRING_OG_RISIKO.md, C5). Kortene inneholder
// bare nummer, tittel, kategorier, status, tidspunkt og roller, aldri beskrivelser,
// planer, analyser eller begrunnelser. Alle knapper åpner appen, der serveren sjekker
// identitet og rolle. Feil i Teams stopper aldri arbeidsflyten (køen fanger dem).
public class GovernanceNotifier
{
    public const string KindPendingApproval = "change_pending_approval";
    public const string KindInfoRequested = "change_info_requested";
    public const string KindStatus = "change_status";
    public const string KindUpcoming = "change_upcoming";
    public const string KindPostReviewMissing = "change_post_review_missing";
    public const string KindRiskUntreated = "risk_untreated";
    public const string KindRiskOverdue = "risk_action_overdue";
    public const string KindAcceptanceExpiring = "risk_acceptance_expiring";
    public const string KindAcceptanceExpired = "risk_acceptance_expired";

    // Statuser som gir kort (i tillegg til «venter på godkjenning»)
    public static readonly string[] NotifiedStatuses =
    [
        ChangeStatuses.Approved, ChangeStatuses.Rejected, ChangeStatuses.Implementation, ChangeStatuses.Completed,
        ChangeStatuses.Failed, ChangeStatuses.RolledBack, ChangeStatuses.Closed,
    ];

    private static readonly Dictionary<string, string> StatusNames = new()
    {
        [ChangeStatuses.PendingApproval] = "Venter på godkjenning",
        [ChangeStatuses.Approved] = "Godkjent",
        [ChangeStatuses.Rejected] = "Avvist",
        [ChangeStatuses.Implementation] = "Implementeres",
        [ChangeStatuses.Completed] = "Gjennomført",
        [ChangeStatuses.Failed] = "Feilet",
        [ChangeStatuses.RolledBack] = "Rullet tilbake",
        [ChangeStatuses.Closed] = "Lukket",
    };

    private static readonly Dictionary<string, string> TypeNames = new()
    {
        [ChangeTypes.Standard] = "Standard", [ChangeTypes.Normal] = "Normal", [ChangeTypes.Emergency] = "Hasteendring",
    };

    private static readonly Dictionary<string, string> EnvironmentNames = new()
    {
        [ChangeEnvironments.Development] = "Utvikling", [ChangeEnvironments.Test] = "Test",
        [ChangeEnvironments.Production] = "Produksjon",
    };

    private static readonly Dictionary<string, string> RiskStatusNames = new()
    {
        [RiskStatuses.Identified] = "Identifisert", [RiskStatuses.Assessed] = "Vurdert",
        [RiskStatuses.InTreatment] = "Under behandling", [RiskStatuses.Monitoring] = "Overvåkes",
        [RiskStatuses.Accepted] = "Akseptert", [RiskStatuses.Closed] = "Lukket",
    };

    private static readonly Dictionary<string, string> LevelNames = new()
    {
        [RiskLevels.Low] = "Lav", [RiskLevels.Medium] = "Middels", [RiskLevels.High] = "Høy", [RiskLevels.Critical] = "Kritisk",
    };

    // Rollenavn slik de vises i appen
    private static string RoleName(string role) =>
        string.Join(' ', role.Split('_').Select(w => w == "gdpr" ? "GDPR" : char.ToUpperInvariant(w[0]) + w[1..]));

    private static readonly TimeZoneInfo Norway = SlaCalculator.Norway;

    private readonly TeamsNotificationService _teams;

    public GovernanceNotifier(TeamsNotificationService teams)
    {
        _teams = teams;
    }

    private static string Time(DateTime? utc) => utc is { } value
        ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(value, DateTimeKind.Utc), Norway).ToString("dd.MM.yyyy HH:mm")
        : "–";

    private static string Date(DateTime? utc) => utc is { } value
        ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(value, DateTimeKind.Utc), Norway).ToString("dd.MM.yyyy")
        : "–";

    private List<(string, string)> ChangeFacts(ChangeRequest change) =>
    [
        ("Type", TypeNames.GetValueOrDefault(change.Type, change.Type)),
        ("Miljø", EnvironmentNames.GetValueOrDefault(change.Environment, change.Environment)),
        ("Risikoscore", change.RiskScore?.ToString() ?? RiskScore.Of(change.Likelihood, change.Impact)?.ToString() ?? "–"),
        ("Planlagt", $"{Time(change.PlannedStart)} – {Time(change.PlannedEnd)}"),
    ];

    private List<(string, string)> ChangeLinks(ChangeRequest change, string openTitle = "Åpne endringen") =>
        [(openTitle, _teams.ChangeUrl(change.Id)), ("Se historikk", _teams.ChangeUrl(change.Id, history: true))];

    private List<(string, string)> RiskLinks(Risk risk) =>
        [("Åpne risikoen", _teams.RiskUrl(risk.Id)), ("Se historikk", _teams.RiskUrl(risk.Id, history: true))];

    private static string Title(string number, string title) => $"{number}: {title}";

    // Ny status på en endring. «Venter på godkjenning» viser rollene som mangler.
    public Task ChangeStatusAsync(ChangeRequest change, string status, IEnumerable<string> missingRoles, CancellationToken ct = default)
    {
        if (status == ChangeStatuses.PendingApproval)
        {
            var facts = ChangeFacts(change);
            var missing = missingRoles.ToList();
            if (missing.Count > 0)
                facts.Add(("Mangler godkjenning fra", string.Join(", ", missing.Select(RoleName))));

            return _teams.NotifyGovernanceAsync(KindPendingApproval, TeamsCards.Card(
                $"{change.Number} venter på godkjenning", Title(change.Number, change.Title), facts,
                ChangeLinks(change, "Åpne og vurder")), ct);
        }

        if (!NotifiedStatuses.Contains(status))
            return Task.CompletedTask;

        var name = StatusNames.GetValueOrDefault(status, status);
        return _teams.NotifyGovernanceAsync(KindStatus, TeamsCards.Card(
            $"{change.Number}: {name}", Title(change.Number, change.Title), ChangeFacts(change), ChangeLinks(change)), ct);
    }

    public Task InfoRequestedAsync(ChangeRequest change, string role, CancellationToken ct = default) =>
        _teams.NotifyGovernanceAsync(KindInfoRequested, TeamsCards.Card(
            $"{change.Number}: mer informasjon trengs", Title(change.Number, change.Title),
            [.. ChangeFacts(change), ("Spør", RoleName(role))], ChangeLinks(change)), ct);

    public Task UpcomingAsync(ChangeRequest change, CancellationToken ct = default) =>
        _teams.NotifyGovernanceAsync(KindUpcoming, TeamsCards.Card(
            $"Kommende produksjonsendring {change.Number}", Title(change.Number, change.Title),
            ChangeFacts(change), ChangeLinks(change)), ct);

    public Task PostReviewMissingAsync(ChangeRequest change, CancellationToken ct = default) =>
        _teams.NotifyGovernanceAsync(KindPostReviewMissing, TeamsCards.Card(
            $"Etterkontroll mangler for {change.Number}", Title(change.Number, change.Title),
            ChangeFacts(change), ChangeLinks(change)), ct);

    private List<(string, string)> RiskFacts(Risk risk) =>
    [
        ("Nivå", $"{LevelNames.GetValueOrDefault(RiskLevels.For(risk.Score))} ({risk.Score})"),
        ("Kategori", RoleName(risk.Category)),
        ("Status", RiskStatusNames.GetValueOrDefault(risk.Status, risk.Status)),
    ];

    public Task RiskUntreatedAsync(Risk risk, CancellationToken ct = default) =>
        _teams.NotifyGovernanceAsync(KindRiskUntreated, TeamsCards.Card(
            $"{risk.Number} er ikke behandlet", Title(risk.Number, risk.Title),
            [.. RiskFacts(risk), ("Eier", risk.OwnerEmployeeId == null ? "Mangler" : "Satt")], RiskLinks(risk)), ct);

    public Task RiskOverdueAsync(Risk risk, CancellationToken ct = default) =>
        _teams.NotifyGovernanceAsync(KindRiskOverdue, TeamsCards.Card(
            $"Tiltak for {risk.Number} er forbi fristen", Title(risk.Number, risk.Title),
            [.. RiskFacts(risk), ("Frist", Date(risk.DueDate))], RiskLinks(risk)), ct);

    public Task AcceptanceExpiringAsync(Risk risk, DateTime validUntil, CancellationToken ct = default) =>
        _teams.NotifyGovernanceAsync(KindAcceptanceExpiring, TeamsCards.Card(
            $"Risikoaksept for {risk.Number} utløper snart", Title(risk.Number, risk.Title),
            [.. RiskFacts(risk), ("Gyldig til", Date(validUntil))], RiskLinks(risk)), ct);

    public Task AcceptanceExpiredAsync(Risk risk, CancellationToken ct = default) =>
        _teams.NotifyGovernanceAsync(KindAcceptanceExpired, TeamsCards.Card(
            $"Risikoaksept for {risk.Number} er utløpt", Title(risk.Number, risk.Title),
            RiskFacts(risk), RiskLinks(risk)), ct);
}

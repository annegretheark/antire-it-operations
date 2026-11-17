using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;
using backend.Services;

namespace backend.Controllers;

public record AlertDto(
    int Id,
    string Source,
    string ExternalId,
    string Title,
    string Severity,
    string? ResourceId,
    string? PortalUrl,
    string State,
    DateTime FirstSeenAt,
    DateTime LastSeenAt,
    DateTime LastFiredAt,
    int Occurrences,
    int? TicketId,
    string? TicketStatus
);

public record SimulateAlertRequest(
    string? Source,
    string? Severity,
    string? Title,
    // Samme ID som før gir ny forekomst av samme varsel
    string? ExternalId,
    string? ResourceId,
    // fired (standard) | resolved
    string? State
);

public record UpdateAlertRuleRequest(
    string? MinSeverity,
    string? Category,
    string? PriorityOverride,
    bool NotifyTeams,
    bool CreateTickets
);

// Varsler som er mottatt (Helpdesk.Agent og Helpdesk.Admin), status per kilde,
// og simulerte varsler for å teste flyten uten Defender-lisens (Helpdesk.Admin).
[ApiController]
[Route("api/alerts")]
[Authorize(Policy = AuthPolicies.HelpdeskAgent)]
public class AlertsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AlertIngestionService _ingestion;
    private readonly AlertSourceStatusRegistry _status;
    private readonly ITenantContext _tenant;

    public AlertsController(
        AppDbContext db,
        AlertIngestionService ingestion,
        AlertSourceStatusRegistry status,
        ITenantContext tenant)
    {
        _db = db;
        _ingestion = ingestion;
        _status = status;
        _tenant = tenant;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<AlertDto>>> GetAll(
        [FromQuery] string? source,
        [FromQuery] string? state,
        [FromQuery] string? severity)
    {
        var query = _db.Alerts.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(source))
            query = query.Where(a => a.Source == source);

        if (!string.IsNullOrWhiteSpace(state))
            query = query.Where(a => a.State == state);

        if (!string.IsNullOrWhiteSpace(severity))
            query = query.Where(a => a.Severity == severity);

        var alerts = await query.OrderByDescending(a => a.LastFiredAt).Take(500).ToListAsync();

        var ticketIds = alerts.Where(a => a.TicketId != null).Select(a => a.TicketId!.Value).ToList();
        var ticketStatus = await _db.Tickets
            .AsNoTracking()
            .Where(t => ticketIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Status);

        return alerts.Select(a => new AlertDto(
            a.Id, a.Source, a.ExternalId, a.Title, a.Severity, a.ResourceId, a.PortalUrl, a.State,
            a.FirstSeenAt, a.LastSeenAt, a.LastFiredAt, a.Occurrences, a.TicketId,
            a.TicketId is int id ? ticketStatus.GetValueOrDefault(id) : null)).ToList();
    }

    // Siste henting per kilde (f.eks. «mangler rettigheten SecurityAlert.Read.All»)
    [HttpGet("sources")]
    public IReadOnlyList<AlertSourceStatus> GetSources() => _status.For(_tenant.TenantId!);

    // Sender et testvarsel gjennom hele flyten: varsel, regel, sak, SLA, tildeling og Teams
    [HttpPost("simulate")]
    [Authorize(Policy = AuthPolicies.HelpdeskAdmin)]
    public async Task<ActionResult<AlertIngestResult>> Simulate([FromBody] SimulateAlertRequest request)
    {
        var source = request.Source ?? AlertSources.Defender;

        if (!AlertSources.All.Contains(source))
            return BadRequest("Ugyldig kilde.");

        var severity = AlertSeverities.For(source)
            .FirstOrDefault(s => s.Equals(request.Severity, StringComparison.OrdinalIgnoreCase));

        if (severity == null)
            return BadRequest($"Ugyldig alvorlighetsgrad. Gyldige: {string.Join(", ", AlertSeverities.For(source))}.");

        var state = request.State ?? AlertStates.Fired;

        if (state != AlertStates.Fired && state != AlertStates.Resolved)
            return BadRequest("Ugyldig tilstand.");

        var externalId = string.IsNullOrWhiteSpace(request.ExternalId)
            ? $"simulert-{Guid.NewGuid():N}"
            : request.ExternalId.Trim();

        var signal = new AlertSignal(
            source,
            externalId,
            string.IsNullOrWhiteSpace(request.Title) ? "Simulert varsel" : request.Title.Trim(),
            severity,
            string.IsNullOrWhiteSpace(request.ResourceId) ? null : request.ResourceId.Trim(),
            null,
            state,
            DateTime.UtcNow,
            System.Text.Json.JsonSerializer.Serialize(new { simulated = true, externalId }));

        return await _ingestion.IngestAsync(signal, HttpContext.RequestAborted);
    }
}

// Varselregler per kilde (Helpdesk.Admin)
[ApiController]
[Route("api/alertrules")]
[Authorize(Policy = AuthPolicies.HelpdeskAdmin)]
public class AlertRulesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AlertRuleService _rules;

    public AlertRulesController(AppDbContext db, AlertRuleService rules)
    {
        _db = db;
        _rules = rules;
    }

    [HttpGet]
    public Task<List<AlertRule>> GetAll() => _rules.GetAllAsync(HttpContext.RequestAborted);

    [HttpPut("{source}")]
    public async Task<ActionResult<AlertRule>> Update(string source, [FromBody] UpdateAlertRuleRequest request)
    {
        if (!AlertSources.All.Contains(source))
            return NotFound();

        if (request.MinSeverity == null || !AlertSeverities.IsValid(source, request.MinSeverity))
            return BadRequest($"Ugyldig alvorlighetsgrad. Gyldige: {string.Join(", ", AlertSeverities.For(source))}.");

        var category = request.Category?.Trim();

        if (string.IsNullOrWhiteSpace(category) || category.Length > 100)
            return BadRequest("Kategori er påkrevd (maks 100 tegn).");

        var priority = string.IsNullOrWhiteSpace(request.PriorityOverride) ? null : request.PriorityOverride;

        if (priority != null && !TicketPriorities.All.Contains(priority))
            return BadRequest("Ugyldig prioritet.");

        await _rules.GetAllAsync(HttpContext.RequestAborted);

        var rule = await _db.AlertRules.FirstAsync(r => r.Source == source);
        rule.MinSeverity = AlertSeverities.For(source)
            .First(s => s.Equals(request.MinSeverity, StringComparison.OrdinalIgnoreCase));
        rule.Category = category;
        rule.PriorityOverride = priority;
        rule.NotifyTeams = request.NotifyTeams;
        rule.CreateTickets = request.CreateTickets;

        await _db.SaveChangesAsync();

        return Ok(rule);
    }
}

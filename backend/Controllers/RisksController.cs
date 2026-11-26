using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;
using backend.Services;

namespace backend.Controllers;

public record RiskSummaryDto(
    int Id, string Number, string Title, string Category, int Likelihood, int Consequence, int Score, string Level,
    int? ResidualScore, string? Strategy, string Status, int? OwnerEmployeeId, int? ActionOwnerEmployeeId,
    DateTime? DueDate, DateTime? NextReviewAt, DateTime UpdatedAt);

public record RiskDetailsDto(
    Risk Risk,
    string Level,
    string? ResidualLevel,
    bool AcceptanceActive,
    string? AcceptanceInactiveReason,
    RiskLinksInput Links,
    // Hva innlogget person kan gjøre nå
    IReadOnlyList<string> AllowedTransitions,
    bool CanEdit,
    bool CanAccept
);

public record RiskTransitionRequest(string? To, string? Note);

public record RiskAcceptRequest(string? Justification, DateTime? ValidUntil, int? ExpectedScore);

public record RiskRevokeRequest(string? Justification);

// 5×5: Counts[sannsynlighet - 1][konsekvens - 1]
public record RiskMatrixDto(int[][] Inherent, int[][] Residual, int OpenRisks);

// Risikoregisteret (ENDRING_OG_RISIKO.md, C3). Lese og registrere: Helpdesk.Agent
// eller en endringsrolle. Alle regler håndheves i RiskRegisterService.
[ApiController]
[Route("api/risks")]
public class RisksController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly RiskRegisterService _register;

    public RisksController(AppDbContext db, RiskRegisterService register)
    {
        _db = db;
        _register = register;
    }

    private CancellationToken Ct => HttpContext.RequestAborted;

    private async Task RequireReadAsync()
    {
        if (!RiskRegisterService.CanRead(await _register.MyRolesAsync(Ct), User.IsHelpdeskAgent()))
            throw GovernanceException.Forbidden("Krever Helpdesk.Agent eller en rolle i endrings- og risikoprosessen.");
    }

    [HttpGet]
    public Task<IActionResult> GetAll(
        [FromQuery] string? category, [FromQuery] string? status, [FromQuery] string? level,
        [FromQuery] int? ownerEmployeeId, [FromQuery] bool unowned = false, [FromQuery] bool overdue = false,
        [FromQuery] bool awaitingAcceptance = false) => RunAsync(async () =>
    {
        await RequireReadAsync();
        var now = DateTime.UtcNow;
        var query = _db.Risks.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(r => r.Category == category);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(r => r.Status == status);
        if (level != null && RiskLevels.All.Contains(level))
        {
            var (min, max) = RiskLevels.Range(level);
            query = query.Where(r => r.Score >= min && r.Score <= max);
        }
        if (ownerEmployeeId != null) query = query.Where(r => r.OwnerEmployeeId == ownerEmployeeId);
        if (unowned) query = query.Where(r => r.OwnerEmployeeId == null && r.Status != RiskStatuses.Closed);
        if (overdue)
        {
            query = query.Where(r => r.DueDate < now &&
                (r.Status == RiskStatuses.Identified || r.Status == RiskStatuses.Assessed || r.Status == RiskStatuses.InTreatment));
        }
        if (awaitingAcceptance)
        {
            query = query.Where(r => r.Strategy == RiskStrategies.Accept &&
                (r.Status == RiskStatuses.Assessed || r.Status == RiskStatuses.Monitoring));
        }

        var risks = await query.OrderByDescending(r => r.Score).ThenBy(r => r.Number).Take(500).ToListAsync(Ct);

        return Ok(risks.Select(r => new RiskSummaryDto(
            r.Id, r.Number, r.Title, r.Category, r.Likelihood, r.Consequence, r.Score, RiskLevels.For(r.Score),
            r.ResidualScore, r.Strategy, r.Status, r.OwnerEmployeeId, r.ActionOwnerEmployeeId,
            r.DueDate, r.NextReviewAt, r.UpdatedAt)));
    });

    [HttpGet("matrix")]
    public Task<IActionResult> Matrix() => RunAsync(async () =>
    {
        await RequireReadAsync();

        var open = await _db.Risks.AsNoTracking()
            .Where(r => r.Status != RiskStatuses.Closed)
            .Select(r => new { r.Likelihood, r.Consequence, r.ResidualLikelihood, r.ResidualConsequence })
            .ToListAsync(Ct);

        int[][] Grid() => Enumerable.Range(0, 5).Select(_ => new int[5]).ToArray();
        var inherent = Grid();
        var residual = Grid();

        foreach (var r in open)
        {
            inherent[r.Likelihood - 1][r.Consequence - 1]++;
            if (r.ResidualLikelihood is { } rl && r.ResidualConsequence is { } rc)
                residual[rl - 1][rc - 1]++;
        }

        return Ok(new RiskMatrixDto(inherent, residual, open.Count));
    });

    [HttpGet("{id:int}")]
    public Task<IActionResult> Get(int id) => RunAsync(async () =>
    {
        await RequireReadAsync();
        return Ok(await DetailsAsync(await _register.LoadAsync(id, Ct)));
    });

    [HttpPost]
    public Task<IActionResult> Create([FromBody] RiskInput input) => RunAsync(async () =>
    {
        var risk = await _register.CreateAsync(input, User.IsHelpdeskAgent(), Ct);
        return StatusCode(201, await DetailsAsync(risk));
    });

    [HttpPut("{id:int}")]
    public Task<IActionResult> Update(int id, [FromBody] RiskInput input) => RunAsync(async () =>
    {
        await RequireReadAsync();
        return Ok(await DetailsAsync(await _register.UpdateAsync(id, input, Ct)));
    });

    [HttpPost("{id:int}/transitions")]
    public Task<IActionResult> Transition(int id, [FromBody] RiskTransitionRequest request) => RunAsync(async () =>
    {
        await RequireReadAsync();
        return Ok(await DetailsAsync(await _register.TransitionAsync(id, request.To, request.Note, Ct)));
    });

    [HttpPost("{id:int}/acceptances")]
    public Task<IActionResult> Accept(int id, [FromBody] RiskAcceptRequest request) => RunAsync(async () =>
    {
        await RequireReadAsync();
        return Ok(await DetailsAsync(await _register.AcceptAsync(
            id, request.Justification, request.ValidUntil, request.ExpectedScore, Ct)));
    });

    [HttpPost("{id:int}/acceptances/revoke")]
    public Task<IActionResult> Revoke(int id, [FromBody] RiskRevokeRequest request) => RunAsync(async () =>
    {
        await RequireReadAsync();
        return Ok(await DetailsAsync(await _register.RevokeAsync(id, request.Justification, Ct)));
    });

    [HttpPut("{id:int}/links")]
    public Task<IActionResult> SetLinks(int id, [FromBody] RiskLinksInput input) => RunAsync(async () =>
    {
        await RequireReadAsync();
        return Ok(await _register.SetLinksAsync(id, input, Ct));
    });

    [HttpGet("{id:int}/history")]
    public Task<IActionResult> History(int id) => RunAsync(async () =>
    {
        await RequireReadAsync();
        var risk = await _register.LoadAsync(id, Ct);
        var riskId = risk.Id.ToString();
        var acceptanceIds = risk.Acceptances.Select(a => a.Id.ToString()).ToList();

        return Ok(await _db.AuditEvents.AsNoTracking()
            .Where(e => (e.EntityType == nameof(Risk) && e.EntityId == riskId) ||
                        (e.EntityType == nameof(RiskAcceptance) && acceptanceIds.Contains(e.EntityId)))
            .OrderBy(e => e.OccurredAt).ThenBy(e => e.Id)
            .ToListAsync(Ct));
    });

    private async Task<RiskDetailsDto> DetailsAsync(Risk risk)
    {
        var roles = await _register.MyRolesAsync(Ct);
        var links = await _register.LinksAsync(risk.Id, Ct);
        var state = RiskRegisterService.AcceptanceState(risk, DateTime.UtcNow);
        var serviceIds = links.ServiceIds ?? [];

        return new RiskDetailsDto(
            risk,
            RiskLevels.For(risk.Score),
            risk.ResidualScore is { } residual ? RiskLevels.For(residual) : null,
            state.Active,
            state.InactiveReason,
            links,
            _register.AllowedTransitions(risk, roles, serviceIds),
            _register.CanEdit(risk, roles, serviceIds),
            RiskRegisterService.CanAccept(risk, roles, serviceIds));
    }

    private async Task<IActionResult> RunAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (GovernanceException e)
        {
            return StatusCode(e.StatusCode, new { message = e.Message });
        }
    }
}

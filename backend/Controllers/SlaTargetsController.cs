using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;
using backend.Services;

namespace backend.Controllers;

public record UpdateSlaTargetRequest(
    int ResponseMinutes,
    int ResolutionMinutes,
    bool TwentyFourSeven
);

// SLA-mål per prioritet (Helpdesk.Admin). Endringer gjelder nye saker og saker
// som får ny prioritet; fristene på andre saker beregnes ikke på nytt.
[ApiController]
[Route("api/slatargets")]
[Authorize(Policy = AuthPolicies.HelpdeskAdmin)]
public class SlaTargetsController : ControllerBase
{
    // 60 døgn er mer enn nok for et SLA-mål
    private const int MaxMinutes = 60 * 24 * 60;

    private readonly AppDbContext _db;
    private readonly SlaService _sla;

    public SlaTargetsController(AppDbContext db, SlaService sla)
    {
        _db = db;
        _sla = sla;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<SlaTarget>>> GetAll() =>
        await _sla.GetTargetsAsync(HttpContext.RequestAborted);

    [HttpPut("{priority}")]
    public async Task<ActionResult<SlaTarget>> Update(
        string priority,
        [FromBody] UpdateSlaTargetRequest request)
    {
        if (!TicketPriorities.All.Contains(priority))
            return NotFound();

        if (request.ResponseMinutes < 1 || request.ResolutionMinutes < 1 ||
            request.ResponseMinutes > MaxMinutes || request.ResolutionMinutes > MaxMinutes)
        {
            return BadRequest("Tidene må være mellom 1 minutt og 60 døgn.");
        }

        if (request.ResponseMinutes > request.ResolutionMinutes)
            return BadRequest("Responstiden kan ikke være lengre enn løsningstiden.");

        await _sla.EnsureSeededAsync(HttpContext.RequestAborted);

        var target = await _db.SlaTargets.FirstOrDefaultAsync(t => t.Priority == priority);

        if (target == null)
        {
            target = new SlaTarget { Priority = priority };
            _db.SlaTargets.Add(target);
        }

        target.ResponseMinutes = request.ResponseMinutes;
        target.ResolutionMinutes = request.ResolutionMinutes;
        target.TwentyFourSeven = request.TwentyFourSeven;

        await _db.SaveChangesAsync();

        return Ok(target);
    }
}

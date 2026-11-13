
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;

namespace backend.Controllers;

[ApiController]
[Route("api/accessrequests")]
public class AccessRequestsController : ControllerBase
{
    private readonly AppDbContext _context;

    public AccessRequestsController(AppDbContext context)
    {
        _context = context;
    }

    // Hent alle tilgangsbestillinger
    [HttpGet]
    public async Task<ActionResult<IEnumerable<AccessRequest>>> GetAll()
    {
        return await _context.AccessRequests
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();
    }

    // Opprett tilgangsbestilling
    [HttpPost]
    public async Task<ActionResult<AccessRequest>> Create(
        [FromBody] AccessRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.EmployeeName) ||
            string.IsNullOrWhiteSpace(request.SystemName) ||
            string.IsNullOrWhiteSpace(request.Reason) ||
            string.IsNullOrWhiteSpace(request.ManagerName))
        {
            return BadRequest("Alle feltene må fylles ut.");
        }

        request.Id = 0;
        request.Status = "Venter på godkjenning";
        request.CreatedAt = DateTime.UtcNow;
        request.ApprovedAt = null;

        _context.AccessRequests.Add(request);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetAll), request);
    }

    // Godkjenn eller avslå
    // TODO: Krev autentisert leder før produksjon
    [HttpPatch("{id:int}/approval")]
    public async Task<ActionResult<AccessRequest>> SetApproval(
        int id,
        [FromBody] ApprovalUpdate update)
    {
        if (update.Status != "Godkjent" &&
            update.Status != "Avslått")
        {
            return BadRequest("Ugyldig status.");
        }

        var request = await _context.AccessRequests.FindAsync(id);

        if (request == null)
            return NotFound();

        if (request.Status != "Venter på godkjenning")
            return BadRequest("Bestillingen er allerede behandlet.");

        request.Status = update.Status;
        request.ApprovedAt = update.Status == "Godkjent"
            ? DateTime.UtcNow
            : null;

        await _context.SaveChangesAsync();

        return Ok(request);
    }

    // IT markerer godkjent tilgang som fullført
    // TODO: Krev autentisert IT-rolle før produksjon
    [HttpPatch("{id:int}/complete")]
    public async Task<ActionResult<AccessRequest>> Complete(int id)
    {
        var request = await _context.AccessRequests.FindAsync(id);

        if (request == null)
            return NotFound();

        if (request.Status != "Godkjent")
            return BadRequest(
                "Bare godkjente bestillinger kan fullføres.");

        request.Status = "Fullført";

        await _context.SaveChangesAsync();

        return Ok(request);
    }
}

public class ApprovalUpdate
{
    public string Status { get; set; } = string.Empty;
}

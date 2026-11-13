
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;using backend.Services;


namespace backend.Controllers;

public record CreateTicketRequest(
    string Category,
    string Description,
    int? ReportedByEmployeeId
);

public record UpdateTicketStatusRequest(
    string Status
);

public record AssignTicketRequest(
    string? AssignedTo
);

public record UpdateTicketPriorityRequest(
    string Priority
);

[ApiController]
[Route("api/[controller]")]
public class TicketsController : ControllerBase
{
   private readonly AppDbContext _db;
private readonly TicketAssignmentService _assignmentService;

public TicketsController(
    AppDbContext db,
    TicketAssignmentService assignmentService)
{
    _db = db;
    _assignmentService = assignmentService;
}
    // Hent alle saker
    [HttpGet]
    public async Task<ActionResult<IEnumerable<Ticket>>> GetTickets()
    {
        var tickets = await _db.Tickets
            .AsNoTracking()
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();

        return Ok(tickets);
    }

    // Opprett ny sak
    [HttpPost]
    public async Task<ActionResult<Ticket>> CreateTicket(
        [FromBody] CreateTicketRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Category) ||
            string.IsNullOrWhiteSpace(request.Description))
        {
            return BadRequest(
                "Kategori og beskrivelse er påkrevd."
            );
        }

        var ticket = new Ticket
        {
            Category = request.Category,
            Description = request.Description,
            ReportedByEmployeeId = request.ReportedByEmployeeId,
            Status = "Ny",
            Priority = "Normal",
            CreatedAt = DateTime.UtcNow
            
        };
        // Finn ansvarlig basert på rettigheter og ansvarsområde
ticket.AssignedToEmployeeId =
    await _assignmentService.FindAssigneeAsync(ticket.Category);

        _db.Tickets.Add(ticket);
        await _db.SaveChangesAsync();

        return StatusCode(201, ticket);
    }

    // Endre status
    [HttpPatch("{id:int}/status")]
    public async Task<IActionResult> UpdateStatus(
        int id,
        [FromBody] UpdateTicketStatusRequest request)
    {
        var allowedStatuses = new[]
        {
            "Ny",
            "Under behandling",
            "Løst"
        };

        if (!allowedStatuses.Contains(request.Status))
        {
            return BadRequest("Ugyldig status.");
        }

        var ticket = await _db.Tickets.FindAsync(id);

        if (ticket == null)
        {
            return NotFound();
        }

        ticket.Status = request.Status;

        await _db.SaveChangesAsync();

        return Ok(ticket);
    }

    // Tildel ansvarlig konsulent
    [HttpPatch("{id:int}/assign")]
    public async Task<IActionResult> AssignTicket(
        int id,
        [FromBody] AssignTicketRequest request)
    {
        var ticket = await _db.Tickets.FindAsync(id);

        if (ticket == null)
        {
            return NotFound();
        }

        ticket.AssignedTo =
            string.IsNullOrWhiteSpace(request.AssignedTo)
                ? null
                : request.AssignedTo.Trim();

        await _db.SaveChangesAsync();

        return Ok(ticket);
    }

    // Endre prioritet
    [HttpPatch("{id:int}/priority")]
    public async Task<IActionResult> UpdatePriority(
        int id,
        [FromBody] UpdateTicketPriorityRequest request)
    {
        var allowedPriorities = new[]
        {
            "Lav",
            "Normal",
            "Høy",
            "Kritisk"
        };

        if (!allowedPriorities.Contains(request.Priority))
        {
            return BadRequest("Ugyldig prioritet.");
        }

        var ticket = await _db.Tickets.FindAsync(id);

        if (ticket == null)
        {
            return NotFound();
        }

        ticket.Priority = request.Priority;

        await _db.SaveChangesAsync();

        return Ok(ticket);
    }
    [HttpGet("me")]
[Microsoft.AspNetCore.Authorization.Authorize]
public IActionResult GetCurrentUser()
{
    return Ok(new
    {
        IsAuthenticated = User.Identity?.IsAuthenticated,
        ObjectId = User.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
            ?? User.FindFirst("oid")?.Value,
        Name = User.Identity?.Name
    });
}
}

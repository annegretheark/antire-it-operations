
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Identity.Web;


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
    int? AssignedToEmployeeId
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
    // Agent og admin ser alle saker i tenanten, andre ser bare saker de selv har meldt inn
    [HttpGet]
    public async Task<ActionResult<IEnumerable<Ticket>>> GetTickets()
    {
        var query = _db.Tickets.AsNoTracking();

        if (!User.IsHelpdeskAgent())
        {
            var callerEmployeeId = await GetCallerEmployeeIdAsync();

            if (callerEmployeeId == null)
                return Ok(Array.Empty<Ticket>());

            query = query.Where(t => t.ReportedByEmployeeId == callerEmployeeId);
        }

        var tickets = await query
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();

        return Ok(tickets);
    }

    // Finn ansatt koblet til innlogget bruker via Entra objekt-ID
    private async Task<int?> GetCallerEmployeeIdAsync()
    {
        var objectId = User.GetObjectId();

        if (string.IsNullOrWhiteSpace(objectId))
            return null;

        return await _db.Employees
            .AsNoTracking()
            .Where(e => e.EntraObjectId == objectId)
            .Select(e => (int?)e.Id)
            .FirstOrDefaultAsync();
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

        // Bare agent og admin kan melde inn på vegne av andre
        var reportedByEmployeeId = User.IsHelpdeskAgent()
            ? request.ReportedByEmployeeId
            : await GetCallerEmployeeIdAsync();

        var ticket = new Ticket
        {
            Category = request.Category,
            Description = request.Description,
            ReportedByEmployeeId = reportedByEmployeeId,
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
    [Authorize(Policy = AuthPolicies.HelpdeskAgent)]
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
    [Authorize(Policy = AuthPolicies.HelpdeskAgent)]
    public async Task<IActionResult> AssignTicket(
        int id,
        [FromBody] AssignTicketRequest request)
    {
        var ticket = await _db.Tickets.FindAsync(id);

        if (ticket == null)
        {
            return NotFound();
        }

        // null fjerner ansvarlig; ellers må det være en aktiv ansatt
        if (request.AssignedToEmployeeId is int employeeId)
        {
            var isActiveEmployee = await _db.Employees.AnyAsync(e =>
                e.Id == employeeId && e.Status == "Aktiv");

            if (!isActiveEmployee)
            {
                return BadRequest("Ansvarlig må være en aktiv ansatt.");
            }
        }

        ticket.AssignedToEmployeeId = request.AssignedToEmployeeId;

        await _db.SaveChangesAsync();

        return Ok(ticket);
    }

    // Endre prioritet
    [HttpPatch("{id:int}/priority")]
    [Authorize(Policy = AuthPolicies.HelpdeskAgent)]
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
public IActionResult GetCurrentUser(
    [FromServices] Microsoft.Extensions.Options.IOptions<TenantOptions> tenants)
{
    var tenantId = User.GetTenantId();

    return Ok(new
    {
        IsAuthenticated = User.Identity?.IsAuthenticated,
        ObjectId = User.GetObjectId(),
        Name = User.Identity?.Name,
        TenantId = tenantId,
        TenantName = tenants.Value.NameOf(tenantId) ?? tenantId,
        IsHelpdeskAdmin = User.IsInRole(AppRoles.HelpdeskAdmin),
        IsHelpdeskAgent = User.IsHelpdeskAgent(),
        IsApprover = User.IsAccessApprover(),
        IsEmployeeAdmin = User.IsInRole(AppRoles.EmployeeSyncAdmin)
    });
}
}

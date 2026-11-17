
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Identity.Web;


namespace backend.Controllers;

// Med ServiceCatalogItemId hentes kategori og prioritet fra tjenestekatalogen,
// og Category ignoreres. Uten den er Category påkrevd (som før katalogen).
public record CreateTicketRequest(
    string? Category,
    string Description,
    int? ReportedByEmployeeId,
    int? ServiceCatalogItemId = null,
    // Utstyret saken gjelder. Vanlige brukere kan bare velge eget utstyr.
    int? AssetId = null
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
    private readonly SlaService _sla;
    private readonly TeamsNotificationService _teams;

    public TicketsController(
        AppDbContext db,
        TicketAssignmentService assignmentService,
        SlaService sla,
        TeamsNotificationService teams)
    {
        _db = db;
        _assignmentService = assignmentService;
        _sla = sla;
        _teams = teams;
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

    // Historikk for en sak (hvem endret hva og når). Samme tilgang som til
    // selve saken: agent og admin, eller den som meldte inn saken.
    [HttpGet("{id:int}/history")]
    public async Task<ActionResult<IEnumerable<AuditEventDto>>> GetHistory(int id)
    {
        var ticket = await _db.Tickets
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id);

        if (ticket == null)
            return NotFound();

        if (!User.IsHelpdeskAgent())
        {
            var callerEmployeeId = await GetCallerEmployeeIdAsync();

            if (callerEmployeeId == null || ticket.ReportedByEmployeeId != callerEmployeeId)
                return NotFound();
        }

        var entityId = id.ToString();

        var events = await _db.AuditEvents
            .AsNoTracking()
            .Where(e => e.EntityType == nameof(Ticket) && e.EntityId == entityId)
            .OrderBy(e => e.Id)
            .ToListAsync();

        return Ok(events.Select(AuditEventDto.From));
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
        if (string.IsNullOrWhiteSpace(request.Description))
            return BadRequest("Beskrivelse er påkrevd.");

        var category = request.Category?.Trim();
        var priority = TicketPriorities.Normal;

        if (request.ServiceCatalogItemId is int itemId)
        {
            var item = await _db.ServiceCatalogItems
                .AsNoTracking()
                .FirstOrDefaultAsync(i => i.Id == itemId && i.IsActive);

            if (item == null)
                return BadRequest("Ukjent eller deaktivert tjeneste.");

            category = item.Category;
            priority = item.DefaultPriority;
        }
        else if (string.IsNullOrWhiteSpace(category))
        {
            return BadRequest("Tjeneste eller kategori er påkrevd.");
        }

        // Bare agent og admin kan melde inn på vegne av andre
        var reportedByEmployeeId = User.IsHelpdeskAgent()
            ? request.ReportedByEmployeeId
            : await GetCallerEmployeeIdAsync();

        if (request.AssetId is int assetId)
        {
            var asset = await _db.Assets
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == assetId);

            var isOwnAsset = reportedByEmployeeId != null &&
                asset?.AssignedToEmployeeId == reportedByEmployeeId;

            if (asset == null || (!User.IsHelpdeskAgent() && !isOwnAsset))
                return BadRequest("Ukjent utstyr.");
        }

        await _sla.EnsureSeededAsync();

        var ticket = new Ticket
        {
            Category = category,
            Description = request.Description,
            ReportedByEmployeeId = reportedByEmployeeId,
            ServiceCatalogItemId = request.ServiceCatalogItemId,
            AssetId = request.AssetId,
            Status = TicketStatuses.New,
            Priority = priority,
            CreatedAt = DateTime.UtcNow
        };

        await _sla.ApplyDeadlinesAsync(ticket);

        // Finn ansvarlig basert på rettigheter og ansvarsområde
        ticket.AssignedToEmployeeId =
            await _assignmentService.FindAssigneeAsync(ticket.Category);

        _db.Tickets.Add(ticket);
        await _db.SaveChangesAsync();

        // Kritiske saker varsles i Teams hvis tenanten har slått det på
        if (ticket.Priority == TicketPriorities.Critical)
            await _teams.NotifyCriticalManualTicketAsync(ticket, HttpContext.RequestAborted);

        return StatusCode(201, ticket);
    }

    // Endre status. Første endring teller som respons; løst setter ResolvedAt,
    // og gjenåpning nullstiller den.
    [HttpPatch("{id:int}/status")]
    [Authorize(Policy = AuthPolicies.HelpdeskAgent)]
    public async Task<IActionResult> UpdateStatus(
        int id,
        [FromBody] UpdateTicketStatusRequest request)
    {
        if (!TicketStatuses.All.Contains(request.Status))
            return BadRequest("Ugyldig status.");

        var ticket = await _db.Tickets.FindAsync(id);

        if (ticket == null)
            return NotFound();

        if (ticket.Status != request.Status)
        {
            var now = DateTime.UtcNow;
            ticket.FirstRespondedAt ??= now;

            if (request.Status == TicketStatuses.Resolved)
                ticket.ResolvedAt = now;
            else if (ticket.Status == TicketStatuses.Resolved)
                ticket.ResolvedAt = null;

            ticket.Status = request.Status;
        }

        await _db.SaveChangesAsync();

        return Ok(ticket);
    }

    // Tildel ansvarlig konsulent. Første endring teller som respons.
    [HttpPatch("{id:int}/assign")]
    [Authorize(Policy = AuthPolicies.HelpdeskAgent)]
    public async Task<IActionResult> AssignTicket(
        int id,
        [FromBody] AssignTicketRequest request)
    {
        var ticket = await _db.Tickets.FindAsync(id);

        if (ticket == null)
            return NotFound();

        // null fjerner ansvarlig; ellers må det være en aktiv ansatt
        if (request.AssignedToEmployeeId is int employeeId)
        {
            var isActiveEmployee = await _db.Employees.AnyAsync(e =>
                e.Id == employeeId && e.Status == "Aktiv");

            if (!isActiveEmployee)
                return BadRequest("Ansvarlig må være en aktiv ansatt.");
        }

        if (ticket.AssignedToEmployeeId != request.AssignedToEmployeeId)
        {
            ticket.FirstRespondedAt ??= DateTime.UtcNow;
            ticket.AssignedToEmployeeId = request.AssignedToEmployeeId;
        }

        await _db.SaveChangesAsync();

        return Ok(ticket);
    }

    // Endre prioritet. Fristene beregnes på nytt fra opprettelsestidspunktet.
    [HttpPatch("{id:int}/priority")]
    [Authorize(Policy = AuthPolicies.HelpdeskAgent)]
    public async Task<IActionResult> UpdatePriority(
        int id,
        [FromBody] UpdateTicketPriorityRequest request)
    {
        if (!TicketPriorities.All.Contains(request.Priority))
            return BadRequest("Ugyldig prioritet.");

        await _sla.EnsureSeededAsync();

        var ticket = await _db.Tickets.FindAsync(id);

        if (ticket == null)
            return NotFound();

        if (ticket.Priority != request.Priority)
        {
            ticket.Priority = request.Priority;
            await _sla.ApplyDeadlinesAsync(ticket);
        }

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

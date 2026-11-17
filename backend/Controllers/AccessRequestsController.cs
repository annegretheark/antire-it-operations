using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Identity.Web;

namespace backend.Controllers;

public record CreateAccessRequest(
    int? EmployeeId,
    int? ServiceCatalogItemId,
    string? Reason,
    string? ManagerName
);

// Bestillingen etter godkjenning eller nytt forsøk, med resultatet av å gi
// tilgangen i Entra (null når det ikke ble forsøkt)
public record AccessRequestResult(AccessRequest Request, AccessGrantOutcome? Grant);

[ApiController]
[Route("api/accessrequests")]
public class AccessRequestsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly AccessGrantService _grants;

    public AccessRequestsController(AppDbContext context, AccessGrantService grants)
    {
        _context = context;
        _grants = grants;
    }

    // Agent, admin og godkjennere ser alle bestillinger i tenanten,
    // andre ser bare bestillinger de selv har opprettet
    [HttpGet]
    public async Task<ActionResult<IEnumerable<AccessRequest>>> GetAll()
    {
        var query = _context.AccessRequests.AsNoTracking();

        if (!User.IsHelpdeskAgent() && !User.IsAccessApprover())
        {
            var objectId = User.GetObjectId();

            if (string.IsNullOrWhiteSpace(objectId))
                return Ok(Array.Empty<AccessRequest>());

            query = query.Where(r => r.RequestedByObjectId == objectId);
        }

        return await query
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();
    }

    // Opprett tilgangsbestilling. Systemet velges fra tjenestekatalogen og den
    // ansatte fra ansattlisten. Krever systemet ikke godkjenning og bestillingen
    // gjelder deg selv, godkjennes den med en gang og tilgangen gis.
    [HttpPost]
    public async Task<ActionResult<AccessRequest>> Create([FromBody] CreateAccessRequest body)
    {
        if (body.EmployeeId == null ||
            body.ServiceCatalogItemId == null ||
            string.IsNullOrWhiteSpace(body.Reason) ||
            string.IsNullOrWhiteSpace(body.ManagerName))
        {
            return BadRequest("Alle feltene må fylles ut.");
        }

        var system = await _context.ServiceCatalogItems
            .AsNoTracking()
            .FirstOrDefaultAsync(i =>
                i.Id == body.ServiceCatalogItemId &&
                i.Type == ServiceCatalogTypes.Access &&
                i.IsActive);

        if (system == null)
            return BadRequest("Velg et aktivt system fra tjenestekatalogen.");

        var employee = await _context.Employees
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == body.EmployeeId && e.Status == "Aktiv");

        if (employee == null)
            return BadRequest("Velg en aktiv ansatt.");

        var callerObjectId = User.GetObjectId();

        var request = new AccessRequest
        {
            RequestedByObjectId = callerObjectId,
            EmployeeId = employee.Id,
            EmployeeName = $"{employee.FirstName} {employee.LastName}".Trim(),
            ServiceCatalogItemId = system.Id,
            SystemName = system.Name,
            Reason = body.Reason.Trim(),
            ManagerName = body.ManagerName.Trim(),
            Status = AccessRequestStatuses.Pending,
            CreatedAt = DateTime.UtcNow,
        };

        var forYourself = !string.IsNullOrWhiteSpace(callerObjectId) &&
            string.Equals(employee.EntraObjectId, callerObjectId, StringComparison.OrdinalIgnoreCase);

        if (!system.RequiresApproval && forYourself)
        {
            request.Status = AccessRequestStatuses.Approved;
            request.ApprovedAt = DateTime.UtcNow;
        }

        _context.AccessRequests.Add(request);
        await _context.SaveChangesAsync();

        if (request.Status == AccessRequestStatuses.Approved)
            await _grants.TryGrantAsync(request, HttpContext.RequestAborted);

        return CreatedAtAction(nameof(GetAll), request);
    }

    // Godkjenn eller avslå. Godkjent tilgang til et system med Entra-gruppe
    // gis automatisk.
    [HttpPatch("{id:int}/approval")]
    [Authorize(Policy = AuthPolicies.AccessApprover)]
    public async Task<ActionResult<AccessRequestResult>> SetApproval(
        int id,
        [FromBody] ApprovalUpdate update)
    {
        if (update.Status != AccessRequestStatuses.Approved &&
            update.Status != AccessRequestStatuses.Rejected)
        {
            return BadRequest("Ugyldig status.");
        }

        var request = await _context.AccessRequests.FindAsync(id);

        if (request == null)
            return NotFound();

        if (request.Status != AccessRequestStatuses.Pending)
            return BadRequest("Bestillingen er allerede behandlet.");

        var approved = update.Status == AccessRequestStatuses.Approved;

        request.Status = update.Status;
        request.ApprovedAt = approved ? DateTime.UtcNow : null;

        _context.SetAuditAction(request, approved ? AuditActions.Approved : AuditActions.Rejected);

        await _context.SaveChangesAsync();

        var grant = approved
            ? await _grants.TryGrantAsync(request, HttpContext.RequestAborted)
            : null;

        return Ok(new AccessRequestResult(request, grant is { Attempted: true } ? grant : null));
    }

    // Nytt forsøk på å gi tilgangen i Entra, f.eks. etter at rettigheten er på plass
    [HttpPost("{id:int}/grant")]
    [Authorize(Policy = AuthPolicies.HelpdeskAgent)]
    public async Task<ActionResult<AccessRequestResult>> Grant(int id)
    {
        var request = await _context.AccessRequests.FindAsync(id);

        if (request == null)
            return NotFound();

        if (request.Status != AccessRequestStatuses.Approved)
            return BadRequest("Bare godkjente bestillinger kan gis tilgang.");

        var grant = await _grants.TryGrantAsync(request, HttpContext.RequestAborted);

        return Ok(new AccessRequestResult(request, grant));
    }

    // IT markerer godkjent tilgang som fullført (når tilgangen er gitt manuelt)
    [HttpPatch("{id:int}/complete")]
    [Authorize(Policy = AuthPolicies.HelpdeskAgent)]
    public async Task<ActionResult<AccessRequest>> Complete(int id)
    {
        var request = await _context.AccessRequests.FindAsync(id);

        if (request == null)
            return NotFound();

        if (request.Status != AccessRequestStatuses.Approved)
            return BadRequest("Bare godkjente bestillinger kan fullføres.");

        request.Status = AccessRequestStatuses.Completed;
        _context.SetAuditAction(request, AuditActions.Completed);

        await _context.SaveChangesAsync();

        return Ok(request);
    }
}

public class ApprovalUpdate
{
    public string Status { get; set; } = string.Empty;
}

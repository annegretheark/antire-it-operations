using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;
using backend.Services;

namespace backend.Controllers;

public record LifecycleSummaryDto(
    int Id,
    int EmployeeId,
    string EmployeeName,
    string Type,
    DateTime ReferenceDate,
    string Status,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    int TaskCount,
    int DoneCount,
    int OverdueCount
);

public record LifecycleAccessDto(
    int AccessRequestId,
    string SystemName,
    string Status,
    string? GroupId
);

public record LifecycleAssetDto(int Id, string AssetTag, string? Model, string Status);

public record LifecycleDetailsDto(
    LifecycleSummaryDto Process,
    IReadOnlyList<LifecycleTask> Tasks,
    // Offboarding: utstyr som skal leveres inn og tilganger som kan fjernes
    IReadOnlyList<LifecycleAssetDto> Assets,
    IReadOnlyList<LifecycleAccessDto> Accesses
);

public record StartLifecycleRequest(int EmployeeId, string? Type, DateTime? ReferenceDate);

public record UpdateLifecycleTaskRequest(bool Done, string? Note);

public record SaveTemplateTaskRequest(
    string? Type,
    string? Title,
    string? Description,
    string? Category,
    int OffsetDays,
    int SortOrder,
    int? ServiceCatalogItemId,
    bool IsActive = true
);

// Onboarding og offboarding (Helpdesk.Agent og Helpdesk.Admin)
[ApiController]
[Route("api/lifecycle")]
[Authorize(Policy = AuthPolicies.HelpdeskAgent)]
public class LifecycleController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly LifecycleService _lifecycle;
    private readonly GraphService _graph;
    private readonly ITenantContext _tenant;
    private readonly ICurrentUser _currentUser;

    public LifecycleController(
        AppDbContext db,
        LifecycleService lifecycle,
        GraphService graph,
        ITenantContext tenant,
        ICurrentUser currentUser)
    {
        _db = db;
        _lifecycle = lifecycle;
        _graph = graph;
        _tenant = tenant;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<IEnumerable<LifecycleSummaryDto>> GetAll(
        [FromQuery] string? status, [FromQuery] string? type)
    {
        var query = _db.LifecycleProcesses.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(p => p.Status == status);

        if (!string.IsNullOrWhiteSpace(type))
            query = query.Where(p => p.Type == type);

        var today = DateTime.UtcNow.Date;

        return await query
            .OrderBy(p => p.ReferenceDate)
            .Select(p => new LifecycleSummaryDto(
                p.Id,
                p.EmployeeId,
                (p.Employee.FirstName + " " + p.Employee.LastName).Trim(),
                p.Type,
                p.ReferenceDate,
                p.Status,
                p.CreatedAt,
                p.CompletedAt,
                p.Tasks.Count,
                p.Tasks.Count(t => t.CompletedAt != null),
                p.Tasks.Count(t => t.CompletedAt == null && t.DueDate < today)))
            .Take(500)
            .ToListAsync();
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<LifecycleDetailsDto>> Get(int id)
    {
        var summary = await SummaryAsync(id);

        if (summary == null)
            return NotFound();

        var tasks = await _db.LifecycleTasks
            .AsNoTracking()
            .Where(t => t.ProcessId == id)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Id)
            .ToListAsync();

        IReadOnlyList<LifecycleAssetDto> assets = [];
        IReadOnlyList<LifecycleAccessDto> accesses = [];

        if (summary.Type == LifecycleTypes.Offboarding)
        {
            assets = await _db.Assets
                .AsNoTracking()
                .Where(a => a.AssignedToEmployeeId == summary.EmployeeId)
                .OrderBy(a => a.AssetTag)
                .Select(a => new LifecycleAssetDto(a.Id, a.AssetTag, a.Model, a.Status))
                .ToListAsync();

            accesses = await AccessesAsync(summary.EmployeeId);
        }

        return new LifecycleDetailsDto(summary, tasks, assets, accesses);
    }

    // Start manuelt. Uten dato brukes startdato (onboarding) eller sluttdato/i dag (offboarding).
    [HttpPost]
    public async Task<ActionResult<LifecycleSummaryDto>> Start([FromBody] StartLifecycleRequest request)
    {
        var type = request.Type ?? LifecycleTypes.Onboarding;

        if (!LifecycleTypes.All.Contains(type))
            return BadRequest("Ugyldig type.");

        var employee = await _db.Employees.FirstOrDefaultAsync(e => e.Id == request.EmployeeId);

        if (employee == null || employee.AnonymizedAt != null)
            return BadRequest("Ukjent ansatt.");

        var date = request.ReferenceDate
            ?? (type == LifecycleTypes.Onboarding ? employee.StartDate : employee.EndDate ?? DateTime.UtcNow);

        var process = await _lifecycle.StartAsync(employee, type, date);

        return StatusCode(201, await SummaryAsync(process.Id));
    }

    [HttpPatch("tasks/{taskId:int}")]
    public async Task<ActionResult<LifecycleTask>> UpdateTask(int taskId, [FromBody] UpdateLifecycleTaskRequest request)
    {
        if (request.Note?.Length > 1000)
            return BadRequest("Notatet kan ha maks 1000 tegn.");

        try
        {
            var task = await _lifecycle.SetTaskDoneAsync(
                taskId, request.Done, request.Note, _currentUser.Name ?? _currentUser.ObjectId ?? "ukjent");

            return task == null ? NotFound() : Ok(task);
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(e.Message);
        }
    }

    [HttpPost("{id:int}/cancel")]
    public async Task<IActionResult> Cancel(int id)
    {
        var process = await _db.LifecycleProcesses.FirstOrDefaultAsync(p => p.Id == id);

        if (process == null)
            return NotFound();

        if (process.Status != LifecycleStatuses.Active)
            return BadRequest("Bare aktive prosesser kan avbrytes.");

        process.Status = LifecycleStatuses.Cancelled;
        await _db.SaveChangesAsync();

        return NoContent();
    }

    // Offboarding: fjerner den ansatte fra Entra-gruppen en tilgangsbestilling ga
    [HttpPost("{id:int}/revoke/{accessRequestId:int}")]
    public async Task<ActionResult<LifecycleAccessDto>> Revoke(int id, int accessRequestId)
    {
        var process = await _db.LifecycleProcesses.Include(p => p.Employee).FirstOrDefaultAsync(p => p.Id == id);

        if (process == null || process.Type != LifecycleTypes.Offboarding)
            return NotFound();

        var access = (await AccessesAsync(process.EmployeeId)).FirstOrDefault(a => a.AccessRequestId == accessRequestId);

        if (access == null)
            return NotFound();

        if (access.Status != AccessRequestStatuses.Completed || access.GroupId == null)
            return BadRequest("Bare fullførte tilganger via Entra-gruppe kan fjernes her.");

        if (string.IsNullOrWhiteSpace(process.Employee.EntraObjectId))
            return BadRequest("Den ansatte er ikke koblet til en bruker i Entra ID.");

        bool removed;

        try
        {
            removed = await _graph.RemoveGroupMemberAsync(
                _tenant.TenantId!, access.GroupId, process.Employee.EntraObjectId);
        }
        catch (GraphUnavailableException e)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = e.Message });
        }

        var request = await _db.AccessRequests.FirstAsync(r => r.Id == accessRequestId);
        request.Status = AccessRequestStatuses.Revoked;
        _db.SetAuditAction(request, AuditActions.AccessRevoked);
        _db.AddAuditEvent(nameof(AccessRequest), request.Id.ToString(), AuditActions.AccessRevoked, new
        {
            groupId = access.GroupId,
            userObjectId = process.Employee.EntraObjectId,
            wasMember = removed,
        });

        await _db.SaveChangesAsync();

        return access with { Status = request.Status };
    }

    private async Task<LifecycleSummaryDto?> SummaryAsync(int id)
    {
        var today = DateTime.UtcNow.Date;

        return await _db.LifecycleProcesses
            .AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new LifecycleSummaryDto(
                p.Id, p.EmployeeId, (p.Employee.FirstName + " " + p.Employee.LastName).Trim(),
                p.Type, p.ReferenceDate, p.Status, p.CreatedAt, p.CompletedAt,
                p.Tasks.Count,
                p.Tasks.Count(t => t.CompletedAt != null),
                p.Tasks.Count(t => t.CompletedAt == null && t.DueDate < today)))
            .FirstOrDefaultAsync();
    }

    // Tilganger den ansatte har fått via tilgangsbestillinger
    private async Task<List<LifecycleAccessDto>> AccessesAsync(int employeeId) =>
        await (
            from request in _db.AccessRequests.AsNoTracking()
            where request.EmployeeId == employeeId &&
                (request.Status == AccessRequestStatuses.Completed || request.Status == AccessRequestStatuses.Revoked)
            join item in _db.ServiceCatalogItems.AsNoTracking()
                on request.ServiceCatalogItemId equals item.Id into items
            from item in items.DefaultIfEmpty()
            orderby request.SystemName
            select new LifecycleAccessDto(request.Id, request.SystemName, request.Status, item == null ? null : item.EntraGroupId)
        ).ToListAsync();
}

// Maler for onboarding og offboarding (Helpdesk.Admin)
[ApiController]
[Route("api/lifecycletemplates")]
[Authorize(Policy = AuthPolicies.HelpdeskAdmin)]
public class LifecycleTemplatesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly LifecycleService _lifecycle;

    public LifecycleTemplatesController(AppDbContext db, LifecycleService lifecycle)
    {
        _db = db;
        _lifecycle = lifecycle;
    }

    [HttpGet]
    public Task<List<LifecycleTemplateTask>> GetAll() => _lifecycle.GetTemplatesAsync(HttpContext.RequestAborted);

    [HttpPost]
    public async Task<ActionResult<LifecycleTemplateTask>> Create([FromBody] SaveTemplateTaskRequest request)
    {
        await _lifecycle.GetTemplatesAsync(HttpContext.RequestAborted);

        var task = new LifecycleTemplateTask();

        if (Apply(task, request) is { } error)
            return BadRequest(error);

        _db.LifecycleTemplateTasks.Add(task);
        await _db.SaveChangesAsync();

        return StatusCode(201, task);
    }

    // Endringer gjelder nye prosesser; prosesser som er startet, beholder oppgavene sine
    [HttpPut("{id:int}")]
    public async Task<ActionResult<LifecycleTemplateTask>> Update(int id, [FromBody] SaveTemplateTaskRequest request)
    {
        var task = await _db.LifecycleTemplateTasks.FindAsync(id);

        if (task == null)
            return NotFound();

        if (Apply(task, request) is { } error)
            return BadRequest(error);

        await _db.SaveChangesAsync();

        return Ok(task);
    }

    private static string? Apply(LifecycleTemplateTask task, SaveTemplateTaskRequest request)
    {
        var type = request.Type ?? LifecycleTypes.Onboarding;
        var title = request.Title?.Trim();

        if (!LifecycleTypes.All.Contains(type))
            return "Ugyldig type.";

        if (string.IsNullOrWhiteSpace(title) || title.Length > 200)
            return "Tittel er påkrevd (maks 200 tegn).";

        if (request.Description?.Length > 1000 || request.Category?.Length > 100)
            return "Beskrivelse kan ha maks 1000 tegn og kategori maks 100.";

        if (request.OffsetDays is < -365 or > 365)
            return "Fristen må være innenfor ett år før eller etter datoen.";

        task.Type = type;
        task.Title = title;
        task.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        task.Category = string.IsNullOrWhiteSpace(request.Category) ? null : request.Category.Trim();
        task.OffsetDays = request.OffsetDays;
        task.SortOrder = request.SortOrder;
        task.ServiceCatalogItemId = request.ServiceCatalogItemId;
        task.IsActive = request.IsActive;

        return null;
    }
}

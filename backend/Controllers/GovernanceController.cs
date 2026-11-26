using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;
using backend.Services;

namespace backend.Controllers;

public record MyGovernanceRolesDto(int? EmployeeId, IReadOnlyList<GovernanceRoleService.RoleGrant> Roles);

public record SaveRoleAssignmentRequest(string? Role, int? ServiceCatalogItemId, int? EmployeeId, string? EntraGroupId);

public record SaveGovernanceSettingsRequest(
    bool TeamsNotificationsEnabled, int ReminderDaysBefore, int? RetentionMonths, bool SimulatedDeliveryEnabled = true);

public record SaveRaciRequest(string? Activity, string? Role, string? Raci);

public record SaveApprovalRuleRequest(string? ChangeType, string? Environment, string? RequiredRole, int MinApprovals, int SortOrder);

public record SaveChangeTemplateRequest(
    string? Name, string? Description, string? Category, string? ImplementationPlan, string? TestPlan,
    string? RollbackPlan, List<string>? AllowedEnvironments, int MaxRiskScore, DateTime? NextReviewAt, bool IsActive = true);

// Roller, RACI og godkjenningsregler for endringer og risiko. «me» for alle
// innloggede; konfigurasjon bare for Helpdesk.Admin, som ikke kan tildele seg selv roller.
[ApiController]
[Route("api/governance")]
public class GovernanceController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly GovernanceRoleService _roles;
    private readonly GovernanceSetupService _setup;
    private readonly GraphService _graph;
    private readonly ITenantContext _tenant;
    private readonly ICurrentUser _currentUser;

    public GovernanceController(
        AppDbContext db, GovernanceRoleService roles, GovernanceSetupService setup,
        GraphService graph, ITenantContext tenant, ICurrentUser currentUser)
    {
        _db = db;
        _roles = roles;
        _setup = setup;
        _graph = graph;
        _tenant = tenant;
        _currentUser = currentUser;
    }

    private CancellationToken Ct => HttpContext.RequestAborted;

    [HttpGet("me")]
    public async Task<MyGovernanceRolesDto> Me()
    {
        var roles = await _roles.ForAsync(_currentUser.ObjectId, Ct);
        return new MyGovernanceRolesDto(roles.EmployeeId, roles.Grants);
    }

    // ---------- Rolletildelinger ----------

    [HttpGet("roles")]
    [Authorize(Policy = AuthPolicies.HelpdeskAdmin)]
    public async Task<List<GovernanceRoleAssignment>> GetRoles() =>
        await _db.GovernanceRoleAssignments.AsNoTracking().OrderBy(a => a.Role).ThenBy(a => a.Id).ToListAsync(Ct);

    [HttpPost("roles")]
    [Authorize(Policy = AuthPolicies.HelpdeskAdmin)]
    public async Task<IActionResult> AddRole([FromBody] SaveRoleAssignmentRequest request)
    {
        if (request.Role == null || !GovernanceRoles.All.Contains(request.Role))
            return BadRequest(new { message = "Ugyldig rolle." });

        var group = string.IsNullOrWhiteSpace(request.EntraGroupId) ? null : request.EntraGroupId.Trim().ToLowerInvariant();
        if ((request.EmployeeId == null) == (group == null))
            return BadRequest(new { message = "Velg enten en ansatt eller en Entra-gruppe." });
        if (group != null && !Guid.TryParse(group, out _))
            return BadRequest(new { message = "Ugyldig gruppe-ID." });

        if (request.ServiceCatalogItemId is { } serviceId && !await _db.ServiceCatalogItems.AnyAsync(s => s.Id == serviceId, Ct))
            return BadRequest(new { message = "Ukjent tjeneste." });

        var self = _currentUser.ObjectId?.ToLowerInvariant();

        if (request.EmployeeId is { } employeeId)
        {
            var employee = await _db.Employees.FirstOrDefaultAsync(e => e.Id == employeeId, Ct);
            if (employee == null || employee.AnonymizedAt != null)
                return BadRequest(new { message = "Ukjent ansatt." });
            if (self != null && string.Equals(employee.EntraObjectId, self, StringComparison.OrdinalIgnoreCase))
                return StatusCode(403, new { message = "Du kan ikke tildele roller til deg selv." });
        }

        if (group != null && self != null)
        {
            bool member;
            try
            {
                member = await _graph.IsMemberOfAnyGroupAsync(_tenant.TenantId!, self, [group], Ct);
            }
            catch (GraphUnavailableException e)
            {
                return StatusCode(502, new { message = e.Message });
            }

            if (member)
                return StatusCode(403, new { message = "Du kan ikke tildele roller til en gruppe du selv er medlem av." });
        }

        var assignment = new GovernanceRoleAssignment
        {
            Role = request.Role,
            ServiceCatalogItemId = request.ServiceCatalogItemId,
            EmployeeId = request.EmployeeId,
            EntraGroupId = group,
        };
        _db.GovernanceRoleAssignments.Add(assignment);
        _db.SetAuditAction(assignment, AuditActions.RoleAssigned);
        await _db.SaveChangesAsync(Ct);

        return StatusCode(201, assignment);
    }

    [HttpDelete("roles/{id:int}")]
    [Authorize(Policy = AuthPolicies.HelpdeskAdmin)]
    public async Task<IActionResult> RemoveRole(int id)
    {
        var assignment = await _db.GovernanceRoleAssignments.FirstOrDefaultAsync(a => a.Id == id, Ct);
        if (assignment == null)
            return NotFound();

        _db.GovernanceRoleAssignments.Remove(assignment);
        _db.SetAuditAction(assignment, AuditActions.RoleRemoved);
        await _db.SaveChangesAsync(Ct);
        return NoContent();
    }

    // ---------- RACI ----------

    [HttpGet("raci")]
    public async Task<IActionResult> GetRaci()
    {
        await _setup.EnsureDefaultsAsync(Ct);
        return Ok(await _db.RaciEntries.AsNoTracking().OrderBy(r => r.Activity).ThenBy(r => r.Role).ToListAsync(Ct));
    }

    // Setter én celle; tom Raci fjerner den
    [HttpPut("raci")]
    [Authorize(Policy = AuthPolicies.HelpdeskAdmin)]
    public async Task<IActionResult> SaveRaci([FromBody] SaveRaciRequest request)
    {
        if (request.Activity == null || !GovernanceActivities.All.Contains(request.Activity) ||
            request.Role == null || !GovernanceRoles.All.Contains(request.Role))
        {
            return BadRequest(new { message = "Ugyldig aktivitet eller rolle." });
        }

        var raci = string.IsNullOrWhiteSpace(request.Raci) ? null : request.Raci.Trim().ToUpperInvariant();
        if (raci != null && !RaciLetters.All.Contains(raci))
            return BadRequest(new { message = "RACI må være R, A, C eller I." });

        await _setup.EnsureDefaultsAsync(Ct);
        var entries = await _db.RaciEntries.Where(r => r.Activity == request.Activity).ToListAsync(Ct);

        // Én Accountable per aktivitet
        if (raci == RaciLetters.Accountable &&
            entries.Any(r => r.Raci == RaciLetters.Accountable && r.Role != request.Role))
        {
            return BadRequest(new { message = "Aktiviteten har allerede en Accountable. Endre den først." });
        }

        var entry = entries.FirstOrDefault(r => r.Role == request.Role);
        if (raci == null)
        {
            if (entry != null)
                _db.RaciEntries.Remove(entry);
        }
        else if (entry == null)
        {
            _db.RaciEntries.Add(new RaciEntry { Activity = request.Activity, Role = request.Role, Raci = raci });
        }
        else
        {
            entry.Raci = raci;
        }

        await _db.SaveChangesAsync(Ct);
        return NoContent();
    }

    // ---------- Dashboard (C7) ----------

    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard([FromServices] GovernanceDashboardService dashboard)
    {
        var roles = await _roles.ForAsync(_currentUser.ObjectId, Ct);
        if (!User.IsHelpdeskAgent() && !roles.HasAny)
            return StatusCode(403, new { message = "Krever Helpdesk.Agent eller en rolle i endrings- og risikoprosessen." });

        return Ok(await dashboard.BuildAsync(DateTime.UtcNow, Ct));
    }

    // ---------- Innstillinger ----------

    [HttpGet("settings")]
    [Authorize(Policy = AuthPolicies.HelpdeskAdmin)]
    public async Task<IActionResult> GetSettings()
    {
        await _setup.EnsureDefaultsAsync(Ct);
        return Ok(await _db.GovernanceSettings.AsNoTracking().FirstAsync(Ct));
    }

    [HttpPut("settings")]
    [Authorize(Policy = AuthPolicies.HelpdeskAdmin)]
    public async Task<IActionResult> SaveSettings([FromBody] SaveGovernanceSettingsRequest request)
    {
        if (request.ReminderDaysBefore is < 0 or > 60)
            return BadRequest(new { message = "Påminnelse må være 0–60 dager før." });
        if (request.RetentionMonths is < 1 or > 240)
            return BadRequest(new { message = "Oppbevaringstid må være 1–240 måneder (eller tom)." });

        await _setup.EnsureDefaultsAsync(Ct);
        var settings = await _db.GovernanceSettings.FirstAsync(Ct);
        settings.TeamsNotificationsEnabled = request.TeamsNotificationsEnabled;
        settings.ReminderDaysBefore = request.ReminderDaysBefore;
        settings.RetentionMonths = request.RetentionMonths;
        settings.SimulatedDeliveryEnabled = request.SimulatedDeliveryEnabled;
        await _db.SaveChangesAsync(Ct);
        return Ok(settings);
    }

    // ---------- Godkjenningsregler ----------

    [HttpGet("approval-rules")]
    public async Task<IActionResult> GetRules()
    {
        await _setup.EnsureDefaultsAsync(Ct);
        return Ok(await _db.ApprovalRules.AsNoTracking()
            .OrderBy(r => r.ChangeType).ThenBy(r => r.Environment).ThenBy(r => r.SortOrder).ToListAsync(Ct));
    }

    [HttpPost("approval-rules")]
    [Authorize(Policy = AuthPolicies.HelpdeskAdmin)]
    public async Task<IActionResult> AddRule([FromBody] SaveApprovalRuleRequest request)
    {
        if (ValidateRule(request) is { } error)
            return BadRequest(new { message = error });

        await _setup.EnsureDefaultsAsync(Ct);

        var rule = new ApprovalRule
        {
            ChangeType = request.ChangeType!, Environment = request.Environment!, RequiredRole = request.RequiredRole!,
            MinApprovals = request.MinApprovals, SortOrder = request.SortOrder,
        };
        _db.ApprovalRules.Add(rule);

        try
        {
            await _db.SaveChangesAsync(Ct);
        }
        catch (DbUpdateException)
        {
            return Conflict(new { message = "Regelen finnes allerede." });
        }

        return StatusCode(201, rule);
    }

    [HttpPut("approval-rules/{id:int}")]
    [Authorize(Policy = AuthPolicies.HelpdeskAdmin)]
    public async Task<IActionResult> UpdateRule(int id, [FromBody] SaveApprovalRuleRequest request)
    {
        if (ValidateRule(request) is { } error)
            return BadRequest(new { message = error });

        var rule = await _db.ApprovalRules.FirstOrDefaultAsync(r => r.Id == id, Ct);
        if (rule == null)
            return NotFound();

        rule.ChangeType = request.ChangeType!;
        rule.Environment = request.Environment!;
        rule.RequiredRole = request.RequiredRole!;
        rule.MinApprovals = request.MinApprovals;
        rule.SortOrder = request.SortOrder;
        await _db.SaveChangesAsync(Ct);
        return Ok(rule);
    }

    [HttpDelete("approval-rules/{id:int}")]
    [Authorize(Policy = AuthPolicies.HelpdeskAdmin)]
    public async Task<IActionResult> DeleteRule(int id)
    {
        var rule = await _db.ApprovalRules.FirstOrDefaultAsync(r => r.Id == id, Ct);
        if (rule == null)
            return NotFound();

        _db.ApprovalRules.Remove(rule);
        await _db.SaveChangesAsync(Ct);
        return NoContent();
    }

    private static string? ValidateRule(SaveApprovalRuleRequest request)
    {
        if (request.ChangeType is not (ChangeTypes.Normal or ChangeTypes.Emergency))
            return "Regler gjelder normal- og hasteendringer (standard er godkjent gjennom malen).";
        if (request.Environment == null ||
            (request.Environment != ApprovalRule.AnyEnvironment && !ChangeEnvironments.All.Contains(request.Environment)))
            return "Ugyldig miljø.";
        if (request.RequiredRole == null || !GovernanceRoles.All.Contains(request.RequiredRole))
            return "Ugyldig rolle.";
        if (request.MinApprovals is < 1 or > 10)
            return "Antall godkjenninger må være 1–10.";
        return null;
    }
}

// Forhåndsgodkjente maler for standardendringer. Helpdesk.Admin vedlikeholder dem;
// en endret mal må godkjennes på nytt av en Change Manager før den kan brukes.
[ApiController]
[Route("api/changetemplates")]
public class ChangeTemplatesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly GovernanceRoleService _roles;
    private readonly ICurrentUser _currentUser;

    public ChangeTemplatesController(AppDbContext db, GovernanceRoleService roles, ICurrentUser currentUser)
    {
        _db = db;
        _roles = roles;
        _currentUser = currentUser;
    }

    private CancellationToken Ct => HttpContext.RequestAborted;

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var roles = await _roles.ForAsync(_currentUser.ObjectId, Ct);
        if (!User.IsHelpdeskAgent() && !roles.HasAny)
            return Forbid();

        return Ok(await _db.ChangeTemplates.AsNoTracking().OrderBy(t => t.Name).ToListAsync(Ct));
    }

    [HttpPost]
    [Authorize(Policy = AuthPolicies.HelpdeskAdmin)]
    public async Task<IActionResult> Create([FromBody] SaveChangeTemplateRequest request)
    {
        var template = new ChangeTemplate();
        if (Apply(template, request) is { } error)
            return BadRequest(new { message = error });

        _db.ChangeTemplates.Add(template);
        try
        {
            await _db.SaveChangesAsync(Ct);
        }
        catch (DbUpdateException)
        {
            return Conflict(new { message = "En mal med samme navn finnes allerede." });
        }

        return StatusCode(201, template);
    }

    [HttpPut("{id:int}")]
    [Authorize(Policy = AuthPolicies.HelpdeskAdmin)]
    public async Task<IActionResult> Update(int id, [FromBody] SaveChangeTemplateRequest request)
    {
        var template = await _db.ChangeTemplates.FirstOrDefaultAsync(t => t.Id == id, Ct);
        if (template == null)
            return NotFound();

        if (Apply(template, request) is { } error)
            return BadRequest(new { message = error });

        await _db.SaveChangesAsync(Ct);
        return Ok(template);
    }

    // Forhåndsgodkjenning av malen (Change Manager)
    [HttpPost("{id:int}/approve")]
    public async Task<IActionResult> Approve(int id)
    {
        var roles = await _roles.ForAsync(_currentUser.ObjectId, Ct);
        if (!roles.Has(GovernanceRoles.ChangeManager))
            return StatusCode(403, new { message = "Bare en Change Manager (global) kan forhåndsgodkjenne maler." });

        var template = await _db.ChangeTemplates.FirstOrDefaultAsync(t => t.Id == id, Ct);
        if (template == null)
            return NotFound();

        template.ApprovedByObjectId = _currentUser.ObjectId;
        template.ApprovedByName = _currentUser.Name ?? _currentUser.ObjectId;
        template.ApprovedAt = DateTime.UtcNow;
        _db.SetAuditAction(template, AuditActions.TemplateApproved);
        await _db.SaveChangesAsync(Ct);
        return Ok(template);
    }

    // Endret innhold fjerner forhåndsgodkjenningen
    private static string? Apply(ChangeTemplate template, SaveChangeTemplateRequest request)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
            return "Navn er påkrevd (maks 200 tegn).";

        var environments = (request.AllowedEnvironments ?? []).Distinct().ToList();
        if (environments.Count == 0 || environments.Any(e => !ChangeEnvironments.All.Contains(e)))
            return "Velg minst ett gyldig miljø.";

        if (request.MaxRiskScore is < 1 or > 25)
            return "Maks risikoscore må være 1–25.";

        if (request.ImplementationPlan == null || request.RollbackPlan == null)
            return "En mal må ha implementerings- og rollback-plan.";

        if (request.Description?.Length > 4000 || request.Category?.Length > 100 ||
            request.ImplementationPlan.Length > 8000 || request.TestPlan?.Length > 8000 || request.RollbackPlan.Length > 8000)
            return "Et av feltene er for langt.";

        var allowed = string.Join(',', environments.Order());
        var contentChanged =
            template.Id == 0 ||
            template.ImplementationPlan != request.ImplementationPlan.Trim() ||
            template.TestPlan != request.TestPlan?.Trim() ||
            template.RollbackPlan != request.RollbackPlan.Trim() ||
            template.AllowedEnvironments != allowed ||
            template.MaxRiskScore != request.MaxRiskScore;

        template.Name = name;
        template.Description = request.Description?.Trim();
        template.Category = request.Category?.Trim();
        template.ImplementationPlan = request.ImplementationPlan.Trim();
        template.TestPlan = request.TestPlan?.Trim();
        template.RollbackPlan = request.RollbackPlan.Trim();
        template.AllowedEnvironments = allowed;
        template.MaxRiskScore = request.MaxRiskScore;
        template.NextReviewAt = request.NextReviewAt;
        template.IsActive = request.IsActive;

        if (contentChanged)
        {
            template.ApprovedAt = null;
            template.ApprovedByObjectId = null;
            template.ApprovedByName = null;
        }

        return null;
    }
}

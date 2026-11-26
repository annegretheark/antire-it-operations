using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;
using backend.Services;

namespace backend.Controllers;

public record ChangeSummaryDto(
    int Id, string Number, string Title, string Type, string Environment, string Priority, string Status,
    int? RiskScore, DateTime? PlannedStart, DateTime? PlannedEnd, string RequestedByName, DateTime UpdatedAt);

public record ChangeApprovalDto(
    int Id, string Role, string Decision, string ApproverName, DateTime DecidedAt, string? Comment, bool Valid);

public record ChangeDetailsDto(
    ChangeRequest Change,
    IReadOnlyList<int> ServiceIds,
    IReadOnlyList<ChangeAzureScopeInput> AzureScopes,
    IReadOnlyList<ChangeApprovalDto> Approvals,
    IReadOnlyList<ApprovalRequirement> Requirements,
    bool WaitingForInfo,
    // Hva innlogget person kan gjøre nå
    IReadOnlyList<string> AllowedTransitions,
    IReadOnlyList<string> CanDecideAs,
    bool CanPostReview,
    // Risikoer koblet til endringen (C3)
    IReadOnlyList<int>? LinkedRiskIds = null
);

public record OptionDto(int Id, string Name);

public record ChangeOptionsDto(
    IReadOnlyList<OptionDto> Services,
    IReadOnlyList<OptionDto> Subscriptions,
    IReadOnlyList<ChangeTemplate> Templates,
    IReadOnlyList<OptionDto> Employees);

public record DeliveryLinkRequest(string? PullRequestUrl, string? CommitSha, string? PipelineRunId);

public record SimulatedPlanDto(SimulatedPlan Plan, ChangeDetailsDto Details);

public record ChangeTransitionRequest(string? To, string? Note);

public record ChangeDecisionRequest(string? Role, string? Decision, string? Comment, string? ExpectedScopeHash);

public record ChangePostReviewRequest(string? Notes);

// Endringsforespørsler (ENDRING_OG_RISIKO.md, C2). Alle regler håndheves i
// ChangeWorkflowService; controlleren oversetter bare til HTTP.
[ApiController]
[Route("api/changes")]
public class ChangesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ChangeWorkflowService _workflow;
    private readonly ChangeDeliveryService _delivery;

    public ChangesController(AppDbContext db, ChangeWorkflowService workflow, ChangeDeliveryService delivery)
    {
        _db = db;
        _workflow = workflow;
        _delivery = delivery;
    }

    private bool IsAgent => User.IsHelpdeskAgent();

    private CancellationToken Ct => HttpContext.RequestAborted;

    // Liste: Helpdesk.Agent og endringsroller ser alle, andre bare egne
    [HttpGet]
    public async Task<IEnumerable<ChangeSummaryDto>> GetAll(
        [FromQuery] string? status, [FromQuery] string? type, [FromQuery] string? environment, [FromQuery] bool mine = false)
    {
        var roles = await _workflow.MyRolesAsync(Ct);
        var oid = roles.ObjectId;

        var query = _db.ChangeRequests.AsNoTracking();

        if ((!IsAgent && !roles.HasAny) || mine)
            query = query.Where(c => c.RequestedByObjectId == oid);
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(c => c.Status == status);
        if (!string.IsNullOrWhiteSpace(type))
            query = query.Where(c => c.Type == type);
        if (!string.IsNullOrWhiteSpace(environment))
            query = query.Where(c => c.Environment == environment);

        return await query
            .OrderByDescending(c => c.UpdatedAt)
            .Take(500)
            .Select(c => new ChangeSummaryDto(
                c.Id, c.Number, c.Title, c.Type, c.Environment, c.Priority, c.Status, c.RiskScore,
                c.PlannedStart, c.PlannedEnd, c.RequestedByName, c.UpdatedAt))
            .ToListAsync(Ct);
    }

    // Valg til skjemaene for endringer og risiko (Helpdesk.Agent eller en endringsrolle)
    [HttpGet("options")]
    public Task<IActionResult> Options() => RunAsync(async () =>
    {
        var roles = await _workflow.MyRolesAsync(Ct);
        if (!IsAgent && !roles.HasAny)
            throw GovernanceException.Forbidden("Krever Helpdesk.Agent eller en rolle i endrings- og risikoprosessen.");

        var now = DateTime.UtcNow;
        return Ok(new ChangeOptionsDto(
            await _db.ServiceCatalogItems.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.Name)
                .Select(s => new OptionDto(s.Id, s.Name)).ToListAsync(Ct),
            await _db.AzureSubscriptions.AsNoTracking().Where(s => s.IsVisible).OrderBy(s => s.DisplayName)
                .Select(s => new OptionDto(s.Id, s.DisplayName)).ToListAsync(Ct),
            await _db.ChangeTemplates.AsNoTracking()
                .Where(t => t.IsActive && t.ApprovedAt != null && (t.NextReviewAt == null || t.NextReviewAt > now))
                .OrderBy(t => t.Name).ToListAsync(Ct),
            await _db.Employees.AsNoTracking().Where(e => e.AnonymizedAt == null && e.Status == "Aktiv")
                .OrderBy(e => e.LastName).ThenBy(e => e.FirstName)
                .Select(e => new OptionDto(e.Id, (e.FirstName + " " + e.LastName).Trim())).ToListAsync(Ct)));
    });

    [HttpGet("{id:int}")]
    public Task<IActionResult> Get(int id) => RunAsync(async () =>
    {
        var change = await _workflow.LoadAsync(id, Ct);
        var roles = await _workflow.MyRolesAsync(Ct);

        if (!_workflow.CanRead(change, roles, IsAgent))
            throw GovernanceException.NotFound();

        return Ok(await DetailsAsync(change, roles));
    });

    [HttpPost]
    public Task<IActionResult> Create([FromBody] ChangeInput input) => RunAsync(async () =>
    {
        var change = await _workflow.CreateAsync(input, IsAgent, Ct);
        return StatusCode(201, await DetailsAsync(change, await _workflow.MyRolesAsync(Ct)));
    });

    [HttpPut("{id:int}")]
    public Task<IActionResult> Update(int id, [FromBody] ChangeInput input) => RunAsync(async () =>
    {
        await ReadableAsync(id);
        var change = await _workflow.UpdateAsync(id, input, Ct);
        return Ok(await DetailsAsync(change, await _workflow.MyRolesAsync(Ct)));
    });

    [HttpPost("{id:int}/transitions")]
    public Task<IActionResult> Transition(int id, [FromBody] ChangeTransitionRequest request) => RunAsync(async () =>
    {
        await ReadableAsync(id);
        var change = await _workflow.TransitionAsync(id, request.To, request.Note, Ct);
        return Ok(await DetailsAsync(change, await _workflow.MyRolesAsync(Ct)));
    });

    [HttpPost("{id:int}/decisions")]
    public Task<IActionResult> Decide(int id, [FromBody] ChangeDecisionRequest request) => RunAsync(async () =>
    {
        await ReadableAsync(id);
        var change = await _workflow.DecideAsync(
            id, request.Role, request.Decision, request.Comment, request.ExpectedScopeHash, Ct);
        return Ok(await DetailsAsync(change, await _workflow.MyRolesAsync(Ct)));
    });

    [HttpPost("{id:int}/post-review")]
    public Task<IActionResult> PostReview(int id, [FromBody] ChangePostReviewRequest request) => RunAsync(async () =>
    {
        await ReadableAsync(id);
        var change = await _workflow.PostReviewAsync(id, request.Notes, Ct);
        return Ok(await DetailsAsync(change, await _workflow.MyRolesAsync(Ct)));
    });

    // ---------- Leveranse (C6, simulert GitHub og Terraform) ----------

    [HttpPost("{id:int}/delivery/link")]
    public Task<IActionResult> LinkDelivery(int id, [FromBody] DeliveryLinkRequest request) => RunAsync(async () =>
    {
        await ReadableAsync(id);
        await _delivery.EnsureEnabledAsync(Ct);
        var change = await _workflow.LinkDeliveryAsync(id, request.PullRequestUrl, request.CommitSha, request.PipelineRunId, Ct);
        return Ok(await DetailsAsync(change, await _workflow.MyRolesAsync(Ct)));
    });

    // Simulert Terraform Plan; teksten returneres, hashen lagres
    [HttpPost("{id:int}/delivery/plan")]
    public Task<IActionResult> CreatePlan(int id) => RunAsync(async () =>
    {
        await ReadableAsync(id);
        var (change, plan) = await _delivery.CreatePlanAsync(id, Ct);
        return Ok(new SimulatedPlanDto(plan, await DetailsAsync(change, await _workflow.MyRolesAsync(Ct))));
    });

    // Sjekken en pipeline ville kalt før Apply. Produksjon avvises alltid i prototypen.
    [HttpGet("{number}/deployment-gate")]
    public Task<IActionResult> DeploymentGate(
        string number, [FromQuery] string? environment, [FromQuery] string? commitSha, [FromQuery] string? planHash) => RunAsync(async () =>
    {
        var roles = await _workflow.MyRolesAsync(Ct);
        if (!IsAgent && !roles.HasAny)
            throw GovernanceException.Forbidden("Krever Helpdesk.Agent eller en rolle i endringsprosessen.");

        var id = await _db.ChangeRequests.Where(c => c.Number == number).Select(c => (int?)c.Id).FirstOrDefaultAsync(Ct)
            ?? throw GovernanceException.NotFound();
        var change = await _workflow.LoadAsync(id, Ct);

        return Ok(await _delivery.CheckGateAsync(change, environment, commitSha, planHash, Ct));
    });

    [HttpPost("{id:int}/delivery/simulate-apply")]
    public Task<IActionResult> SimulateApply(int id) => RunAsync(async () =>
    {
        await ReadableAsync(id);
        return Ok(await _delivery.SimulateApplyAsync(id, Ct));
    });

    // Revisjonslogg for endringen (inkl. beslutninger og statusendringer)
    [HttpGet("{id:int}/history")]
    public Task<IActionResult> History(int id) => RunAsync(async () =>
    {
        var change = await ReadableAsync(id);
        var changeId = change.Id.ToString();
        var approvalIds = change.Approvals.Select(a => a.Id.ToString()).ToList();
        var serviceIds = change.Services.Select(s => s.Id.ToString()).ToList();
        var scopeIds = change.AzureScopes.Select(s => s.Id.ToString()).ToList();

        var events = await _db.AuditEvents
            .AsNoTracking()
            .Where(e =>
                (e.EntityType == nameof(ChangeRequest) && e.EntityId == changeId) ||
                (e.EntityType == nameof(ChangeApproval) && approvalIds.Contains(e.EntityId)) ||
                (e.EntityType == nameof(ChangeAffectedService) && serviceIds.Contains(e.EntityId)) ||
                (e.EntityType == nameof(ChangeAzureScope) && scopeIds.Contains(e.EntityId)))
            .OrderBy(e => e.OccurredAt).ThenBy(e => e.Id)
            .ToListAsync(Ct);

        return Ok(events);
    });

    // Endringen hvis innlogget person kan se den; ellers 404 (røper ikke at den finnes)
    private async Task<ChangeRequest> ReadableAsync(int id)
    {
        var change = await _workflow.LoadAsync(id, Ct);
        if (!_workflow.CanRead(change, await _workflow.MyRolesAsync(Ct), IsAgent))
            throw GovernanceException.NotFound();
        return change;
    }

    private async Task<ChangeDetailsDto> DetailsAsync(ChangeRequest change, GovernanceRoleService.PersonRoles roles)
    {
        var requirements = await _workflow.RequirementsAsync(change, Ct);
        var serviceIds = change.Services.Select(s => s.ServiceCatalogItemId).ToList();

        // Roller personen kan godkjenne i nå (kontrollene gjøres uansett på nytt ved beslutning)
        var canDecideAs = change.Status == ChangeStatuses.PendingApproval &&
                          !string.Equals(change.RequestedByObjectId, roles.ObjectId, StringComparison.OrdinalIgnoreCase)
            ? requirements.Select(r => r.Role).Distinct().Where(r => roles.Has(r, serviceIds)).ToList()
            : [];

        var canPostReview = change.PostReviewRequired && change.PostReviewedAt == null &&
                            change.Status == ChangeStatuses.Completed &&
                            (roles.Has(GovernanceRoles.Reviewer, serviceIds) || roles.Has(GovernanceRoles.ChangeManager, serviceIds));

        return new ChangeDetailsDto(
            change,
            serviceIds,
            change.AzureScopes.Select(s => new ChangeAzureScopeInput(s.AzureSubscriptionId, s.ResourceGroup)).ToList(),
            change.Approvals
                .OrderBy(a => a.DecidedAt).ThenBy(a => a.Id)
                .Select(a => new ChangeApprovalDto(a.Id, a.Role, a.Decision, a.ApproverName, a.DecidedAt, a.Comment,
                    a.ScopeHash == change.ScopeHash))
                .ToList(),
            requirements,
            ChangeWorkflowService.WaitingForInfo(change),
            await _workflow.AllowedTransitionsAsync(change, roles, Ct),
            canDecideAs,
            canPostReview,
            await _db.RiskChanges.Where(l => l.ChangeRequestId == change.Id).Select(l => l.RiskId).ToListAsync(Ct));
    }

    private async Task<IActionResult> RunAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (GovernanceException e) when (e.StatusCode == 404)
        {
            return NotFound(new { message = e.Message });
        }
        catch (GovernanceException e)
        {
            return StatusCode(e.StatusCode, new { message = e.Message });
        }
    }
}

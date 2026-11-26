using Microsoft.AspNetCore.Mvc;
using backend.Services;

namespace backend.Controllers;

public record AiAssistRequest(string? Task);

public record AiDraftRequest(string? Text);

public record AiStatusDto(bool Enabled, int MaxRequestsPerHour, IReadOnlyList<string> ChangeTasks);

// Change and Risk-agenten (ENDRING_OG_RISIKO.md, C8). Gir bare forslag; ingenting her
// endrer endringer, risikoer, godkjenninger eller rettigheter. Samme tilgang som for
// endringer og risiko, og bare endringer personen kan se.
[ApiController]
[Route("api/ai")]
public class AiController : ControllerBase
{
    private readonly AiService _ai;
    private readonly AiAssistService _assist;
    private readonly ChangeWorkflowService _workflow;
    private readonly GovernanceDashboardService _dashboard;

    public AiController(AiService ai, AiAssistService assist, ChangeWorkflowService workflow, GovernanceDashboardService dashboard)
    {
        _ai = ai;
        _assist = assist;
        _workflow = workflow;
        _dashboard = dashboard;
    }

    private CancellationToken Ct => HttpContext.RequestAborted;

    [HttpGet("status")]
    public AiStatusDto Status() => new(_ai.Enabled, _ai.MaxPerHour, AiAssistService.ChangeTasks);

    [HttpPost("changes/{id:int}/assist")]
    public Task<IActionResult> AssistChange(int id, [FromBody] AiAssistRequest request) => RunAsync(async () =>
    {
        var change = await _workflow.LoadAsync(id, Ct);
        if (!_workflow.CanRead(change, await _workflow.MyRolesAsync(Ct), User.IsHelpdeskAgent()))
            throw GovernanceException.NotFound();

        return Ok(await _assist.AssistChangeAsync(change, request.Task, Ct));
    });

    [HttpPost("changes/draft")]
    public Task<IActionResult> DraftChange([FromBody] AiDraftRequest request) => RunAsync(async () =>
    {
        await RequireAccessAsync();
        return Ok(await _assist.DraftChangeAsync(request.Text, Ct));
    });

    [HttpPost("risks/summary")]
    public Task<IActionResult> SummarizeRisks() => RunAsync(async () =>
    {
        await RequireAccessAsync();
        return Ok(await _assist.SummarizeRisksAsync(await _dashboard.BuildAsync(DateTime.UtcNow, Ct), Ct));
    });

    private async Task RequireAccessAsync()
    {
        if (!User.IsHelpdeskAgent() && !(await _workflow.MyRolesAsync(Ct)).HasAny)
            throw GovernanceException.Forbidden("Krever Helpdesk.Agent eller en rolle i endrings- og risikoprosessen.");
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
        catch (AiUnavailableException e)
        {
            // AI av, ikke satt opp, feilet eller for mange forespørsler: tydelig melding
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = e.Message });
        }
    }
}

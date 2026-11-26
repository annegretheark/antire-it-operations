using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;
using backend.Services;

namespace backend.Controllers;

// En rapport i listen (uten nøkkeltall)
public record OpsReportSummaryDto(
    int Id,
    DateTime PeriodStart,
    DateTime PeriodEnd,
    string AiStatus,
    DateTime CreatedAt,
    string CreatedBy
);

// Hele rapporten, med nøkkeltallene som objekt
public record OpsReportDto(
    int Id,
    DateTime PeriodStart,
    DateTime PeriodEnd,
    OpsReportMetrics? Metrics,
    string? AiSummary,
    IReadOnlyList<string>? AiActions,
    string AiStatus,
    DateTime CreatedAt,
    string CreatedBy
);

// Ukentlige driftsrapporter (FASE5_DRIFTSRAPPORT.md). Lese: Helpdesk.Agent og
// Helpdesk.Admin. Lage rapport for forrige uke: Helpdesk.Admin.
[ApiController]
[Route("api/opsreports")]
[Authorize(Policy = AuthPolicies.HelpdeskAgent)]
public class OpsReportsController : ControllerBase
{
    public const int ListSize = 12;

    private readonly AppDbContext _db;
    private readonly OpsReportService _reports;

    public OpsReportsController(AppDbContext db, OpsReportService reports)
    {
        _db = db;
        _reports = reports;
    }

    [HttpGet]
    public async Task<IEnumerable<OpsReportSummaryDto>> GetAll() =>
        await _db.OpsReports
            .OrderByDescending(r => r.PeriodStart)
            .Take(ListSize)
            .Select(r => new OpsReportSummaryDto(
                r.Id, r.PeriodStart, r.PeriodEnd, r.AiStatus, r.CreatedAt, r.CreatedBy))
            .ToListAsync(HttpContext.RequestAborted);

    [HttpGet("{id:int}")]
    public async Task<ActionResult<OpsReportDto>> Get(int id)
    {
        var report = await _db.OpsReports.SingleOrDefaultAsync(r => r.Id == id, HttpContext.RequestAborted);

        return report == null ? NotFound() : ToDto(report);
    }

    // Lager rapporten for forrige uke. Finnes den, gis den tilbake uendret (200).
    [HttpPost("generate")]
    [Authorize(Policy = AuthPolicies.HelpdeskAdmin)]
    public async Task<ActionResult<OpsReportDto>> Generate()
    {
        var now = DateTime.UtcNow;
        var (start, end) = OpsReportService.PreviousWeek(now);

        var (report, created) = await _reports.GenerateAsync(
            start, end, now, cancellationToken: HttpContext.RequestAborted);

        return created
            ? CreatedAtAction(nameof(Get), new { id = report.Id }, ToDto(report))
            : Ok(ToDto(report));
    }

    private static OpsReportDto ToDto(OpsReport report) => new(
        report.Id,
        report.PeriodStart,
        report.PeriodEnd,
        OpsReportService.ReadMetrics(report),
        report.AiSummary,
        report.AiActions == null
            ? null
            : System.Text.Json.JsonSerializer.Deserialize<List<string>>(report.AiActions, OpsReportService.JsonOptions),
        report.AiStatus,
        report.CreatedAt,
        report.CreatedBy);
}

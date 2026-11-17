using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;
using backend.Services;

namespace backend.Controllers;

public record GdprCandidateDto(
    int EmployeeId,
    string Name,
    DateTime? EndDate,
    int AssetCount,
    bool ActiveProcess,
    // Når den automatiske anonymiseringen tar personen (null = av)
    DateTime? AutomaticAt
);

public record UpdateGdprSettingsRequest(int? AnonymizeAfterMonths);

public record SaveProcessingActivityRequest(
    string? Name,
    string? System,
    string? Purpose,
    string? DataSubjects,
    string? DataCategories,
    string? LegalBasis,
    string? Retention,
    string? Recipients,
    string? ThirdCountryTransfers,
    string? SecurityMeasures,
    string? Owner,
    DateTime? LastReviewedAt
);

// GDPR (Helpdesk.Admin): innsyn, anonymisering og behandlingsprotokoll
[ApiController]
[Route("api/gdpr")]
[Authorize(Policy = AuthPolicies.HelpdeskAdmin)]
public class GdprController : ControllerBase
{
    private static readonly JsonSerializerOptions ExportJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly AppDbContext _db;
    private readonly GdprService _gdpr;

    public GdprController(AppDbContext db, GdprService gdpr)
    {
        _db = db;
        _gdpr = gdpr;
    }

    // Innsyn: alt om personen som JSON-fil
    [HttpGet("employees/{id:int}/export")]
    public async Task<IActionResult> Export(int id)
    {
        var export = await _gdpr.ExportAsync(id, HttpContext.RequestAborted);

        if (export == null)
            return NotFound();

        var bytes = JsonSerializer.SerializeToUtf8Bytes(export, ExportJson);
        return File(bytes, "application/json", $"innsyn-ansatt-{id}-{DateTime.UtcNow:yyyyMMdd}.json");
    }

    [HttpPost("employees/{id:int}/anonymize")]
    public async Task<ActionResult<AnonymizeResult>> Anonymize(int id)
    {
        var (result, error) = await _gdpr.AnonymizeAsync(id, HttpContext.RequestAborted);
        return error != null ? BadRequest(error) : Ok(result);
    }

    // Ansatte som har sluttet og ikke er anonymisert ennå
    [HttpGet("candidates")]
    public async Task<IEnumerable<GdprCandidateDto>> Candidates()
    {
        var settings = await _gdpr.GetSettingsAsync(HttpContext.RequestAborted);

        var employees = await _db.Employees
            .AsNoTracking()
            .Where(e => e.Status == "Sluttet" && e.AnonymizedAt == null)
            .OrderBy(e => e.EndDate)
            .Select(e => new
            {
                e.Id,
                Name = (e.FirstName + " " + e.LastName).Trim(),
                e.EndDate,
                Assets = _db.Assets.Count(a => a.AssignedToEmployeeId == e.Id),
                Active = _db.LifecycleProcesses.Any(p => p.EmployeeId == e.Id && p.Status == LifecycleStatuses.Active),
            })
            .ToListAsync();

        return employees.Select(e => new GdprCandidateDto(
            e.Id, e.Name, e.EndDate, e.Assets, e.Active,
            settings.AnonymizeAfterMonths is int months && e.EndDate != null
                ? e.EndDate.Value.Date.AddMonths(months)
                : null));
    }

    [HttpGet("settings")]
    public Task<GdprSettings> GetSettings() => _gdpr.GetSettingsAsync(HttpContext.RequestAborted);

    [HttpPut("settings")]
    public async Task<ActionResult<GdprSettings>> UpdateSettings([FromBody] UpdateGdprSettingsRequest request)
    {
        if (request.AnonymizeAfterMonths is < 1 or > 120)
            return BadRequest("Fristen må være mellom 1 og 120 måneder, eller tom (av).");

        var settings = await _gdpr.GetSettingsAsync(HttpContext.RequestAborted);
        settings.AnonymizeAfterMonths = request.AnonymizeAfterMonths;
        await _db.SaveChangesAsync();

        return Ok(settings);
    }

    // ---------- Behandlingsprotokoll (art. 30) ----------

    [HttpGet("processing")]
    public Task<List<ProcessingActivity>> GetActivities() => _gdpr.GetActivitiesAsync(HttpContext.RequestAborted);

    [HttpPost("processing")]
    public async Task<ActionResult<ProcessingActivity>> CreateActivity([FromBody] SaveProcessingActivityRequest request)
    {
        await _gdpr.GetSettingsAsync(HttpContext.RequestAborted);

        var activity = new ProcessingActivity();

        if (Apply(activity, request) is { } error)
            return BadRequest(error);

        _db.ProcessingActivities.Add(activity);
        await _db.SaveChangesAsync();

        return StatusCode(201, activity);
    }

    [HttpPut("processing/{id:int}")]
    public async Task<ActionResult<ProcessingActivity>> UpdateActivity(int id, [FromBody] SaveProcessingActivityRequest request)
    {
        var activity = await _db.ProcessingActivities.FindAsync(id);

        if (activity == null)
            return NotFound();

        if (Apply(activity, request) is { } error)
            return BadRequest(error);

        await _db.SaveChangesAsync();

        return Ok(activity);
    }

    [HttpDelete("processing/{id:int}")]
    public async Task<IActionResult> DeleteActivity(int id)
    {
        var activity = await _db.ProcessingActivities.FindAsync(id);

        if (activity == null)
            return NotFound();

        _db.ProcessingActivities.Remove(activity);
        await _db.SaveChangesAsync();

        return NoContent();
    }

    private static string? Apply(ProcessingActivity activity, SaveProcessingActivityRequest request)
    {
        static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        var name = Clean(request.Name);
        var purpose = Clean(request.Purpose);
        var subjects = Clean(request.DataSubjects);
        var categories = Clean(request.DataCategories);
        var retention = Clean(request.Retention);
        var basis = request.LegalBasis ?? LegalBases.Contract;

        if (name == null || purpose == null || subjects == null || categories == null || retention == null)
            return "Navn, formål, registrerte, opplysninger og lagringstid er påkrevd.";

        if (name.Length > 200 || Clean(request.System)?.Length > 200 || Clean(request.Owner)?.Length > 200)
            return "Navn, system og ansvarlig kan ha maks 200 tegn.";

        if (!LegalBases.All.Contains(basis))
            return "Ugyldig behandlingsgrunnlag.";

        activity.Name = name;
        activity.System = Clean(request.System);
        activity.Purpose = purpose;
        activity.DataSubjects = subjects;
        activity.DataCategories = categories;
        activity.LegalBasis = basis;
        activity.Retention = retention;
        activity.Recipients = Clean(request.Recipients);
        activity.ThirdCountryTransfers = Clean(request.ThirdCountryTransfers);
        activity.SecurityMeasures = Clean(request.SecurityMeasures);
        activity.Owner = Clean(request.Owner);
        activity.LastReviewedAt = request.LastReviewedAt;

        return null;
    }
}

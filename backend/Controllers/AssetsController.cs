using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using backend.Data;
using backend.Models;
using backend.Services;

namespace backend.Controllers;

public record AssetDto(
    int Id,
    string AssetTag,
    string Type,
    string? Manufacturer,
    string? Model,
    string? SerialNumber,
    string Status,
    int? AssignedToEmployeeId,
    string? AssignedToName,
    DateTime? PurchaseDate,
    DateTime? WarrantyEndsAt,
    string Source,
    string? Notes
);

public record AssetTicketDto(
    int Id,
    string Category,
    string Description,
    string Status,
    string Priority,
    DateTime CreatedAt
);

public record AssetDetailsDto(AssetDto Asset, IReadOnlyList<AssetTicketDto> Tickets);

public record AssignAssetRequest(int? EmployeeId);

public record ImportAssetsRequest(string? Csv);

// IT-utstyr. Helpdesk.Agent og Helpdesk.Admin ser og endrer alt; andre ser bare
// utstyr som er tildelt dem selv. Utstyr slettes ikke (status retired i stedet).
[ApiController]
[Route("api/assets")]
public class AssetsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AssetService _assets;

    public AssetsController(AppDbContext db, AssetService assets)
    {
        _db = db;
        _assets = assets;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<AssetDto>>> GetAll(
        [FromQuery] string? status,
        [FromQuery] string? type,
        [FromQuery] int? employeeId,
        [FromQuery] string? search)
    {
        var query = _db.Assets.AsNoTracking();

        if (!User.IsHelpdeskAgent())
        {
            var callerId = await GetCallerEmployeeIdAsync();

            if (callerId == null)
                return Ok(Array.Empty<AssetDto>());

            query = query.Where(a => a.AssignedToEmployeeId == callerId);
        }

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(a => a.Status == status);

        if (!string.IsNullOrWhiteSpace(type))
            query = query.Where(a => a.Type == type);

        if (employeeId != null)
            query = query.Where(a => a.AssignedToEmployeeId == employeeId);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(a =>
                a.AssetTag.ToLower().Contains(term) ||
                (a.SerialNumber != null && a.SerialNumber.ToLower().Contains(term)) ||
                (a.Model != null && a.Model.ToLower().Contains(term)) ||
                (a.Manufacturer != null && a.Manufacturer.ToLower().Contains(term)));
        }

        return await query
            .OrderBy(a => a.AssetTag)
            .Select(ToDto)
            .ToListAsync();
    }

    // Detaljer med saker som gjelder utstyret
    [HttpGet("{id:int}")]
    public async Task<ActionResult<AssetDetailsDto>> Get(int id)
    {
        var asset = await _db.Assets
            .AsNoTracking()
            .Where(a => a.Id == id)
            .Select(ToDto)
            .FirstOrDefaultAsync();

        if (asset == null)
            return NotFound();

        if (!User.IsHelpdeskAgent())
        {
            var callerId = await GetCallerEmployeeIdAsync();

            if (callerId == null || asset.AssignedToEmployeeId != callerId)
                return NotFound();
        }

        var tickets = await _db.Tickets
            .AsNoTracking()
            .Where(t => t.AssetId == id)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new AssetTicketDto(t.Id, t.Category, t.Description, t.Status, t.Priority, t.CreatedAt))
            .ToListAsync();

        return new AssetDetailsDto(asset, tickets);
    }

    [HttpPost]
    [Authorize(Policy = AuthPolicies.HelpdeskAgent)]
    public async Task<ActionResult<AssetDto>> Create([FromBody] AssetFields request)
    {
        var asset = new Asset { Source = AssetSources.Manual };

        if (await _assets.ApplyAsync(asset, request) is { } error)
            return BadRequest(error);

        if (await _assets.TagIsTakenAsync(asset.AssetTag, exceptId: 0))
            return Conflict("Det finnes allerede utstyr med dette utstyrsnummeret.");

        _db.Assets.Add(asset);
        await _db.SaveChangesAsync();

        return StatusCode(201, await LoadDtoAsync(asset.Id));
    }

    [HttpPut("{id:int}")]
    [Authorize(Policy = AuthPolicies.HelpdeskAgent)]
    public async Task<ActionResult<AssetDto>> Update(int id, [FromBody] AssetFields request)
    {
        var asset = await _db.Assets.FindAsync(id);

        if (asset == null)
            return NotFound();

        if (await _assets.ApplyAsync(asset, request) is { } error)
            return BadRequest(error);

        if (await _assets.TagIsTakenAsync(asset.AssetTag, exceptId: id))
            return Conflict("Det finnes allerede utstyr med dette utstyrsnummeret.");

        await _db.SaveChangesAsync();

        return Ok(await LoadDtoAsync(id));
    }

    // Tildel utstyr, eller registrer innlevering med employeeId = null
    [HttpPatch("{id:int}/assign")]
    [Authorize(Policy = AuthPolicies.HelpdeskAgent)]
    public async Task<ActionResult<AssetDto>> Assign(int id, [FromBody] AssignAssetRequest request)
    {
        var asset = await _db.Assets.FindAsync(id);

        if (asset == null)
            return NotFound();

        var fields = new AssetFields(
            asset.AssetTag, asset.Type, asset.Manufacturer, asset.Model, asset.SerialNumber,
            asset.Status, request.EmployeeId, asset.PurchaseDate, asset.WarrantyEndsAt, asset.Notes);

        if (await _assets.ApplyAsync(asset, fields) is { } error)
            return BadRequest(error);

        await _db.SaveChangesAsync();

        return Ok(await LoadDtoAsync(id));
    }

    // CSV-import. Ved feil lagres ingenting, og svaret lister radene som feilet.
    [HttpPost("import")]
    [Authorize(Policy = AuthPolicies.HelpdeskAgent)]
    [RequestSizeLimit(5_000_000)]
    public async Task<ActionResult<AssetImportResult>> Import([FromBody] ImportAssetsRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Csv))
            return BadRequest(new AssetImportResult(0, 0, ["Filen er tom."]));

        var result = await _assets.ImportAsync(request.Csv);

        return result.Errors.Count > 0 ? BadRequest(result) : Ok(result);
    }

    private static readonly System.Linq.Expressions.Expression<Func<Asset, AssetDto>> ToDto = a =>
        new AssetDto(
            a.Id,
            a.AssetTag,
            a.Type,
            a.Manufacturer,
            a.Model,
            a.SerialNumber,
            a.Status,
            a.AssignedToEmployeeId,
            a.AssignedToEmployee == null
                ? null
                : (a.AssignedToEmployee.FirstName + " " + a.AssignedToEmployee.LastName).Trim(),
            a.PurchaseDate,
            a.WarrantyEndsAt,
            a.Source,
            a.Notes);

    private async Task<AssetDto> LoadDtoAsync(int id) =>
        await _db.Assets.AsNoTracking().Where(a => a.Id == id).Select(ToDto).SingleAsync();

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
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;
using backend.Services;

namespace backend.Controllers;

public record SaveServiceCatalogItemRequest(
    string? Name,
    string? Category,
    string? Description,
    string? Type,
    string? DefaultPriority,
    bool RequiresApproval,
    string? EntraGroupId,
    bool IsActive = true
);

// Tjenestekatalogen. Alle innloggede kan lese aktive tjenester; bare
// Helpdesk.Admin kan se deaktiverte og endre katalogen. Tjenester slettes ikke.
[ApiController]
[Route("api/servicecatalog")]
public class ServiceCatalogController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ServiceCatalogService _catalog;

    public ServiceCatalogController(AppDbContext db, ServiceCatalogService catalog)
    {
        _db = db;
        _catalog = catalog;
    }

    // ?type=access gir bare systemer for tilgangsbestilling.
    // ?includeInactive=true tar med deaktiverte (bare Helpdesk.Admin).
    [HttpGet]
    public async Task<ActionResult<IEnumerable<ServiceCatalogItem>>> GetAll(
        [FromQuery] string? type,
        [FromQuery] bool includeInactive = false)
    {
        await _catalog.EnsureSeededAsync(HttpContext.RequestAborted);

        var query = _db.ServiceCatalogItems.AsNoTracking();

        if (!includeInactive || !User.IsInRole(AppRoles.HelpdeskAdmin))
            query = query.Where(i => i.IsActive);

        if (!string.IsNullOrWhiteSpace(type))
            query = query.Where(i => i.Type == type);

        return await query
            .OrderBy(i => i.Type)
            .ThenBy(i => i.Name)
            .ToListAsync();
    }

    [HttpPost]
    [Authorize(Policy = AuthPolicies.HelpdeskAdmin)]
    public async Task<ActionResult<ServiceCatalogItem>> Create(
        [FromBody] SaveServiceCatalogItemRequest request)
    {
        // Startinnholdet må finnes før første egne tjeneste, ellers blir det aldri lagt inn
        await _catalog.EnsureSeededAsync(HttpContext.RequestAborted);

        var item = new ServiceCatalogItem();

        if (Apply(item, request) is { } error)
            return BadRequest(error);

        if (await NameIsTakenAsync(item.Name, exceptId: null))
            return Conflict("Det finnes allerede en tjeneste med dette navnet.");

        _db.ServiceCatalogItems.Add(item);
        await _db.SaveChangesAsync();

        return StatusCode(201, item);
    }

    [HttpPut("{id:int}")]
    [Authorize(Policy = AuthPolicies.HelpdeskAdmin)]
    public async Task<ActionResult<ServiceCatalogItem>> Update(
        int id,
        [FromBody] SaveServiceCatalogItemRequest request)
    {
        var item = await _db.ServiceCatalogItems.FindAsync(id);

        if (item == null)
            return NotFound();

        if (Apply(item, request) is { } error)
            return BadRequest(error);

        if (await NameIsTakenAsync(item.Name, exceptId: id))
            return Conflict("Det finnes allerede en tjeneste med dette navnet.");

        await _db.SaveChangesAsync();

        return Ok(item);
    }

    private Task<bool> NameIsTakenAsync(string name, int? exceptId) =>
        _db.ServiceCatalogItems.AnyAsync(i => i.Name == name && i.Id != exceptId);

    // Validerer og kopierer feltene. Gir en feilmelding, eller null når alt er i orden.
    private static string? Apply(ServiceCatalogItem item, SaveServiceCatalogItemRequest request)
    {
        var name = request.Name?.Trim();
        var category = request.Category?.Trim();
        var type = request.Type?.Trim() ?? ServiceCatalogTypes.Incident;
        var priority = request.DefaultPriority?.Trim() ?? "Normal";
        var groupId = request.EntraGroupId?.Trim();

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(category))
            return "Navn og kategori er påkrevd.";

        if (name.Length > 100 || category.Length > 100 || request.Description?.Length > 500)
            return "Navn og kategori kan ha maks 100 tegn, beskrivelse maks 500.";

        if (!ServiceCatalogTypes.All.Contains(type))
            return "Ugyldig type.";

        if (!TicketPriorities.All.Contains(priority))
            return "Ugyldig prioritet.";

        string? normalizedGroupId = null;

        if (!string.IsNullOrEmpty(groupId))
        {
            if (type != ServiceCatalogTypes.Access)
                return "Entra-gruppe kan bare settes på tjenester av typen access.";

            if (!Guid.TryParse(groupId, out var parsed))
                return "Entra-gruppe-ID må være en gyldig GUID.";

            normalizedGroupId = parsed.ToString();
        }

        item.Name = name;
        item.Category = category;
        item.Description = string.IsNullOrWhiteSpace(request.Description)
            ? null
            : request.Description.Trim();
        item.Type = type;
        item.DefaultPriority = priority;
        item.RequiresApproval = request.RequiresApproval;
        item.EntraGroupId = normalizedGroupId;
        item.IsActive = request.IsActive;

        return null;
    }
}

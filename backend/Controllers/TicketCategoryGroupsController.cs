
using backend.Data;
using backend.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace backend.Controllers;

public record CreateTicketCategoryGroupRequest(
    string Category,
    string EntraGroupId
);

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Helpdesk.Admin")]
public class TicketCategoryGroupsController : ControllerBase
{
    private readonly AppDbContext _db;

    public TicketCategoryGroupsController(AppDbContext db)
    {
        _db = db;
    }

    // Hent godkjente Entra-grupper
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var groups = await _db.TicketCategoryGroups
            .AsNoTracking()
            .OrderBy(g => g.Category)
            .ThenBy(g => g.EntraGroupId)
            .ToListAsync();

        return Ok(groups);
    }

    // Registrer godkjent Entra-gruppe
    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateTicketCategoryGroupRequest request)
    {
        var category = request.Category?.Trim();
        var groupId = request.EntraGroupId?.Trim();

        if (string.IsNullOrWhiteSpace(category) ||
            !Guid.TryParse(groupId, out var parsedGroupId))
        {
            return BadRequest(
                "Gyldig kategori og Entra-gruppe-ID er påkrevd.");
        }

        var normalizedGroupId = parsedGroupId.ToString();

        var exists = await _db.TicketCategoryGroups
            .AnyAsync(g =>
                g.Category == category &&
                g.EntraGroupId == normalizedGroupId);

        if (exists)
        {
            return Conflict(
                "Gruppen er allerede registrert for denne kategorien.");
        }

        var group = new TicketCategoryGroup
        {
            Category = category,
            EntraGroupId = normalizedGroupId
        };

        _db.TicketCategoryGroups.Add(group);
        await _db.SaveChangesAsync();

        return CreatedAtAction(
            nameof(GetAll),
            new { id = group.Id },
            group);
    }

    // Fjern godkjent Entra-gruppe
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var group = await _db.TicketCategoryGroups.FindAsync(id);

        if (group == null)
            return NotFound();

        _db.TicketCategoryGroups.Remove(group);
        await _db.SaveChangesAsync();

        return NoContent();
    }
}

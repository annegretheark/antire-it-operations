using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;
using backend.Services;

namespace backend.Controllers;

public record EmployeeResponsibilityDto(
    int Id,
    int EmployeeId,
    string EmployeeName,
    string EmployeeStatus,
    string Category,
    bool CanBeAssigned
);

public record CreateEmployeeResponsibilityRequest(
    int EmployeeId,
    string? Category,
    bool CanBeAssigned = true
);

public record UpdateEmployeeResponsibilityRequest(
    bool CanBeAssigned
);

// Ansvarsområder: hvilke ansatte som kan få saker i en kategori automatisk
// (sammen med kategorigruppene, se TicketAssignmentService). Bare Helpdesk.Admin.
[ApiController]
[Route("api/employeeresponsibilities")]
[Authorize(Policy = AuthPolicies.HelpdeskAdmin)]
public class EmployeeResponsibilitiesController : ControllerBase
{
    private readonly AppDbContext _db;

    public EmployeeResponsibilitiesController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<EmployeeResponsibilityDto>>> GetAll()
    {
        return await _db.EmployeeResponsibilities
            .AsNoTracking()
            .OrderBy(r => r.Category)
            .ThenBy(r => r.Employee.FirstName)
            .ThenBy(r => r.Employee.LastName)
            .Select(r => new EmployeeResponsibilityDto(
                r.Id,
                r.EmployeeId,
                (r.Employee.FirstName + " " + r.Employee.LastName).Trim(),
                r.Employee.Status,
                r.Category,
                r.CanBeAssigned))
            .ToListAsync();
    }

    [HttpPost]
    public async Task<ActionResult<EmployeeResponsibility>> Create(
        [FromBody] CreateEmployeeResponsibilityRequest request)
    {
        var category = request.Category?.Trim();

        if (string.IsNullOrWhiteSpace(category) || category.Length > 100)
            return BadRequest("Kategori er påkrevd (maks 100 tegn).");

        var employeeIsActive = await _db.Employees
            .AnyAsync(e => e.Id == request.EmployeeId && e.Status == "Aktiv");

        if (!employeeIsActive)
            return BadRequest("Velg en aktiv ansatt.");

        var exists = await _db.EmployeeResponsibilities
            .AnyAsync(r => r.EmployeeId == request.EmployeeId && r.Category == category);

        if (exists)
            return Conflict("Den ansatte har allerede dette ansvarsområdet.");

        var responsibility = new EmployeeResponsibility
        {
            EmployeeId = request.EmployeeId,
            Category = category,
            CanBeAssigned = request.CanBeAssigned,
        };

        _db.EmployeeResponsibilities.Add(responsibility);
        await _db.SaveChangesAsync();

        return StatusCode(201, new { responsibility.Id });
    }

    // Slå automatisk tildeling av eller på uten å fjerne ansvarsområdet
    [HttpPatch("{id:int}")]
    public async Task<IActionResult> Update(
        int id,
        [FromBody] UpdateEmployeeResponsibilityRequest request)
    {
        var responsibility = await _db.EmployeeResponsibilities.FindAsync(id);

        if (responsibility == null)
            return NotFound();

        responsibility.CanBeAssigned = request.CanBeAssigned;
        await _db.SaveChangesAsync();

        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var responsibility = await _db.EmployeeResponsibilities.FindAsync(id);

        if (responsibility == null)
            return NotFound();

        _db.EmployeeResponsibilities.Remove(responsibility);
        await _db.SaveChangesAsync();

        return NoContent();
    }
}

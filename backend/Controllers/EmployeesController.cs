
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Authorization;

namespace backend.Controllers;

[ApiController]
[Route("api/employees")]
public class EmployeesController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly EmployeeNumberService _employeeNumbers;

    public EmployeesController(
        AppDbContext context,
        EmployeeNumberService employeeNumbers)
    {
        _context = context;
        _employeeNumbers = employeeNumbers;
    }

    // Hent alle ansatte
    [HttpGet]
    public async Task<ActionResult<IEnumerable<Employee>>> GetAll()
    {
        return await _context.Employees
            .OrderBy(e => e.LastName)
            .ThenBy(e => e.FirstName)
            .ToListAsync();
    }

    // Registrer ansatt
    [HttpPost]
    [Authorize(Policy = AuthPolicies.EmployeeAdmin)]
    public async Task<ActionResult<Employee>> Create(
        [FromBody] Employee employee)
    {
        if (string.IsNullOrWhiteSpace(employee.FirstName) ||
            string.IsNullOrWhiteSpace(employee.LastName) ||
            string.IsNullOrWhiteSpace(employee.Email))
        {
            return BadRequest(
                "Fornavn, etternavn og e-post er påkrevd.");
        }

        employee.Id = 0;
        employee.Status = "Aktiv";
        employee.EndDate = null;
        // Generer unikt ansattnummer fra PostgreSQL
        employee.EmployeeNumber = await _employeeNumbers.NextAsync();

        _context.Employees.Add(employee);
        await _context.SaveChangesAsync();

        return Ok(employee);
    }

    // Oppdater ansattinformasjon
    [HttpPut("{id:int}")]
    [Authorize(Policy = AuthPolicies.EmployeeAdmin)]
    public async Task<ActionResult<Employee>> Update(
        int id,
        [FromBody] Employee updated)
    {
        var employee = await _context.Employees.FindAsync(id);

        if (employee == null)
            return NotFound();

        if (string.IsNullOrWhiteSpace(updated.FirstName) ||
            string.IsNullOrWhiteSpace(updated.LastName) ||
            string.IsNullOrWhiteSpace(updated.Email))
        {
            return BadRequest(
                "Fornavn, etternavn og e-post er påkrevd.");
        }

        employee.FirstName = updated.FirstName.Trim();
        employee.LastName = updated.LastName.Trim();
        employee.Email = updated.Email.Trim();
        // Oppdater ansattnummer ved redigering
        employee.EmployeeNumber =
        string.IsNullOrWhiteSpace(updated.EmployeeNumber)
        ? null
        : updated.EmployeeNumber.Trim();
        // Kontroller at ansattnummeret ikke brukes av en annen ansatt
if (employee.EmployeeNumber != null)
{
    var exists = await _context.Employees.AnyAsync(e =>
        e.Id != id &&
        e.EmployeeNumber == employee.EmployeeNumber);

    if (exists)
    {
        return Conflict("Ansattnummeret er allerede i bruk.");
    }
}
        employee.Department = updated.Department.Trim();
        employee.JobTitle = updated.JobTitle.Trim();
        employee.StartDate = updated.StartDate;

        await _context.SaveChangesAsync();

        return Ok(employee);
    }

    // Registrer at en ansatt har sluttet
    [HttpPatch("{id:int}/terminate")]
    [Authorize(Policy = AuthPolicies.EmployeeAdmin)]
    public async Task<ActionResult<Employee>> Terminate(
        int id,
        [FromBody] TerminateEmployeeRequest update)
    {
        var employee = await _context.Employees.FindAsync(id);

        if (employee == null)
            return NotFound();

        if (employee.Status == "Sluttet")
            return BadRequest("Ansatt er allerede registrert som sluttet.");

        if (update.EndDate == default)
            return BadRequest("Sluttdato må oppgis.");

        if (update.EndDate.Date < employee.StartDate.Date)
            return BadRequest("Sluttdato kan ikke være før startdato.");

        employee.EndDate = update.EndDate;
        employee.Status = "Sluttet";

        await _context.SaveChangesAsync();

        return Ok(employee);
    }
}

public class TerminateEmployeeRequest
{
    public DateTime EndDate { get; set; }
}

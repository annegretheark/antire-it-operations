
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using backend.Data;

namespace backend.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "EmployeeSync.Admin")]
public class GraphController : ControllerBase
{
   
   private readonly GraphService _graphService;
private readonly AppDbContext _context;

public GraphController(
    GraphService graphService,
    AppDbContext context)
{
    _graphService = graphService;
    _context = context;
}
    [HttpGet("employees")]
    public async Task<IActionResult> GetEmployees()
    {
        var employees = await _graphService.GetEmployeesAsync();

        return Ok(employees.Select(user => new
        {
            user.Id,
            user.DisplayName,
            user.UserPrincipalName,
            user.Mail,
            user.Department,
            user.JobTitle,
            user.EmployeeId
        }));
    }

[HttpPost("sync-employees")]
public async Task<IActionResult> SyncEmployees()
{
    var users = await _graphService.GetEmployeesAsync();
    var created = 0;
    var updated = 0;

    foreach (var user in users)
    {
        if (string.IsNullOrWhiteSpace(user.Id))
            continue;

        var email = user.Mail ?? user.UserPrincipalName;

        if (string.IsNullOrWhiteSpace(email))
            continue;

        var employee = await _context.Employees
            .FirstOrDefaultAsync(e => e.EntraObjectId == user.Id);

        if (employee == null)
        {
            employee = await _context.Employees
                .FirstOrDefaultAsync(e =>
                    e.EntraObjectId == null &&
                    e.Email.ToLower() == email.ToLower());
        }

        if (employee == null)
        {
            employee = new backend.Models.Employee
            {
                EntraObjectId = user.Id,
                StartDate = DateTime.UtcNow,
                Status = "Aktiv"
            };

            _context.Employees.Add(employee);
            created++;
        }
        else
        {
            updated++;
        }

        employee.EntraObjectId = user.Id;
        employee.FirstName = user.GivenName ?? "";
        employee.LastName = user.Surname ?? "";
        employee.Email = email;
        employee.Department = user.Department ?? "";
        employee.JobTitle = user.JobTitle ?? "";
    }

    await _context.SaveChangesAsync();

    return Ok(new
    {
        created,
        updated,
        totalFromEntra = users.Count
    });
}
[HttpGet("employees/{entraObjectId}/groups")]
public async Task<IActionResult> GetEmployeeGroups(string entraObjectId)
{
    var groupIds = await _graphService.GetUserGroupIdsAsync(entraObjectId);

    return Ok(new
    {
        entraObjectId,
        groupIds
    });
}
}

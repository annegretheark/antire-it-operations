
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
    private readonly EmployeeNumberService _employeeNumbers;
    private readonly ITenantContext _tenant;
    private readonly LifecycleService _lifecycle;

    public GraphController(
        GraphService graphService,
        AppDbContext context,
        EmployeeNumberService employeeNumbers,
        ITenantContext tenant,
        LifecycleService lifecycle)
    {
        _graphService = graphService;
        _context = context;
        _employeeNumbers = employeeNumbers;
        _tenant = tenant;
        _lifecycle = lifecycle;
    }
    [HttpGet("employees")]
    public async Task<IActionResult> GetEmployees()
    {
        var employees = await _graphService.GetEmployeesAsync(_tenant.TenantId!);

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
    var users = await _graphService.GetEmployeesAsync(_tenant.TenantId!);
    var created = 0;
    var updated = 0;

    // Nye ansatte fra synken får onboarding, men ikke ved første synk i en tenant
    // (da er alle «nye», og de fleste har jobbet der lenge)
    var hadEmployees = await _context.Employees.AnyAsync();
    var newEmployees = new List<backend.Models.Employee>();

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
            newEmployees.Add(employee);
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

        // Ansatte uten nummer (nye, eller synket tidligere) får neste nummer
        if (string.IsNullOrWhiteSpace(employee.EmployeeNumber))
        {
            employee.EmployeeNumber = await _employeeNumbers.NextAsync();
        }
    }

    // Én samlet hendelse for synken; endringer per ansatt logges automatisk
    _context.AddAuditEvent("EmployeeSync", "entra", backend.Models.AuditActions.Synced, new
    {
        created,
        updated,
        totalFromEntra = users.Count
    });

    await _context.SaveChangesAsync();

    if (hadEmployees)
    {
        foreach (var employee in newEmployees)
            await _lifecycle.StartAsync(employee, backend.Models.LifecycleTypes.Onboarding, employee.StartDate);
    }

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
    var groupIds = await _graphService.GetUserGroupIdsAsync(_tenant.TenantId!, entraObjectId);

    return Ok(new
    {
        entraObjectId,
        groupIds
    });
}
}

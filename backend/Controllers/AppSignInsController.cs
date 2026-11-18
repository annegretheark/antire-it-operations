using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Services;

namespace backend.Controllers;

// Hvem som har brukt appen i innlogget tenant, sist sett først (Helpdesk.Admin)
[ApiController]
[Route("api/appsignins")]
[Authorize(Policy = AuthPolicies.HelpdeskAdmin)]
public class AppSignInsController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll() =>
        Ok(await db.AppSignIns.AsNoTracking()
            .OrderByDescending(s => s.LastSeenAt)
            .Select(s => new
            {
                s.EntraObjectId,
                s.Name,
                s.UserPrincipalName,
                s.FirstSeenAt,
                s.LastSeenAt,
                s.Visits,
            })
            .ToListAsync(HttpContext.RequestAborted));
}

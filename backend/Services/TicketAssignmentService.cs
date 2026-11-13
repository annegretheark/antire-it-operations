
using backend.Data;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public class TicketAssignmentService
{
    private readonly AppDbContext _db;
    private readonly GraphService _graphService;
    private readonly ILogger<TicketAssignmentService> _logger;

    public TicketAssignmentService(
        AppDbContext db,
        GraphService graphService,
        ILogger<TicketAssignmentService> logger)
    {
        _db = db;
        _graphService = graphService;
        _logger = logger;
    }

    public async Task<int?> FindAssigneeAsync(string category)
    {
        if (string.IsNullOrWhiteSpace(category))
            return null;

        category = category.Trim();

        // Hent godkjente Entra-grupper for kategorien
        var allowedGroupIds = await _db.TicketCategoryGroups
            .AsNoTracking()
            .Where(g => g.Category == category)
            .Select(g => g.EntraGroupId)
            .ToListAsync();

        // Ingen konfigurerte grupper = ingen tildeling
        if (allowedGroupIds.Count == 0)
            return null;

        var allowedGroups = new HashSet<string>(
            allowedGroupIds,
            StringComparer.OrdinalIgnoreCase);

        // Finn aktive ansatte med riktig ansvarsområde
        var candidates = await _db.EmployeeResponsibilities
            .AsNoTracking()
            .Where(r =>
                r.Category == category &&
                r.CanBeAssigned &&
                r.Employee.Status == "Aktiv" &&
                r.Employee.EntraObjectId != null)
            .Select(r => new
            {
                r.EmployeeId,
                r.Employee.EntraObjectId
            })
            .OrderBy(r => r.EmployeeId)
            .ToListAsync();

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.EntraObjectId))
                continue;

            try
            {
                // Kontroller faktisk gruppemedlemskap i Entra ID
                var userGroupIds =
                    await _graphService.GetUserGroupIdsAsync(
                        candidate.EntraObjectId);

                if (userGroupIds.Any(allowedGroups.Contains))
                {
                    return candidate.EmployeeId;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Kunne ikke kontrollere Entra-grupper for ansatt {EmployeeId}",
                    candidate.EmployeeId);

                // Ved feil skal vi aldri tildele uten bekreftet tilgang
            }
        }

        return null;
    }
}

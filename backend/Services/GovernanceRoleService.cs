using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;

namespace backend.Services;

// Hvilke endringsroller en person har (GovernanceRoleAssignments): tildelt den
// ansatte direkte, eller en Entra-gruppe personen er medlem av (sjekkes mot Graph).
// Slås opp på nytt for hver forespørsel; ingenting tas fra klienten.
public class GovernanceRoleService
{
    private readonly AppDbContext _db;
    private readonly GraphService _graph;
    private readonly ITenantContext _tenant;

    public GovernanceRoleService(AppDbContext db, GraphService graph, ITenantContext tenant)
    {
        _db = db;
        _graph = graph;
        _tenant = tenant;
    }

    // Én rolle personen har: global (ServiceId null) eller for én tjeneste
    public record RoleGrant(string Role, int? ServiceId);

    public record PersonRoles(string ObjectId, int? EmployeeId, IReadOnlyList<RoleGrant> Grants)
    {
        // Rollen gjelder hvis den er global, eller gjelder en av tjenestene
        public bool Has(string role, IReadOnlyCollection<int>? serviceIds = null) =>
            Grants.Any(g => g.Role == role &&
                (g.ServiceId == null || (serviceIds != null && serviceIds.Contains(g.ServiceId.Value))));

        public bool HasAny => Grants.Count > 0;
    }

    public async Task<PersonRoles> ForAsync(string? objectId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(objectId) || _tenant.TenantId is not { } tenantId)
            return new PersonRoles(objectId ?? "", null, []);

        var oid = objectId.ToLowerInvariant();

        var employeeId = await _db.Employees
            .Where(e => e.EntraObjectId != null && e.EntraObjectId.ToLower() == oid && e.AnonymizedAt == null)
            .Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var assignments = await _db.GovernanceRoleAssignments
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var grants = new List<RoleGrant>();

        if (employeeId != null)
        {
            grants.AddRange(assignments
                .Where(a => a.EmployeeId == employeeId)
                .Select(a => new RoleGrant(a.Role, a.ServiceCatalogItemId)));
        }

        // Grupper: ett kall per gruppe som har tildelinger (få grupper i praksis)
        foreach (var group in assignments.Where(a => a.EntraGroupId != null).GroupBy(a => a.EntraGroupId!))
        {
            bool member;
            try
            {
                member = await _graph.IsMemberOfAnyGroupAsync(tenantId, oid, [group.Key], cancellationToken);
            }
            catch (GraphUnavailableException)
            {
                // Uten svar fra Graph gis ingen rolle via gruppen (feil lukker, ikke åpner)
                member = false;
            }

            if (member)
                grants.AddRange(group.Select(a => new RoleGrant(a.Role, a.ServiceCatalogItemId)));
        }

        return new PersonRoles(oid, employeeId, grants.Distinct().ToList());
    }
}

using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;

namespace backend.Services;

// Resultat av et forsøk på å gi tilgang. Message vises til brukeren.
public record AccessGrantOutcome(bool Attempted, bool Granted, string Message);

// Tilgang via gruppe (2.4): en godkjent tilgangsbestilling på et system med
// Entra-gruppe legger den ansatte i gruppen og fullfører bestillingen.
// Feiler Graph, står godkjenningen, og feilen logges i revisjonsloggen.
public class AccessGrantService
{
    private readonly AppDbContext _db;
    private readonly GraphService _graph;
    private readonly ITenantContext _tenant;
    private readonly ILogger<AccessGrantService> _logger;

    public AccessGrantService(
        AppDbContext db,
        GraphService graph,
        ITenantContext tenant,
        ILogger<AccessGrantService> logger)
    {
        _db = db;
        _graph = graph;
        _tenant = tenant;
        _logger = logger;
    }

    // Bestillingen må være lagret med status Godkjent
    public async Task<AccessGrantOutcome> TryGrantAsync(
        AccessRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Status != AccessRequestStatuses.Approved)
            return new(false, false, "Bare godkjente bestillinger kan gis tilgang.");

        var groupId = request.ServiceCatalogItemId == null
            ? null
            : await _db.ServiceCatalogItems
                .AsNoTracking()
                .Where(i => i.Id == request.ServiceCatalogItemId)
                .Select(i => i.EntraGroupId)
                .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(groupId))
        {
            return new(false, false,
                "Systemet har ingen Entra-gruppe i tjenestekatalogen, så tilgangen må gis manuelt.");
        }

        var objectId = request.EmployeeId == null
            ? null
            : await _db.Employees
                .AsNoTracking()
                .Where(e => e.Id == request.EmployeeId)
                .Select(e => e.EntraObjectId)
                .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(objectId))
        {
            return await FailAsync(request, groupId,
                "Den ansatte er ikke koblet til en bruker i Entra ID (synk ansatte fra Entra først).",
                cancellationToken);
        }

        AddMemberResult result;

        try
        {
            result = await _graph.AddGroupMemberAsync(_tenant.TenantId!, groupId, objectId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Kunne ikke legge bruker i gruppe for tilgangsbestilling {Id}", request.Id);

            var message = ex is GraphUnavailableException graph
                ? graph.Message
                : "Kunne ikke kontakte Microsoft Graph.";

            return await FailAsync(request, groupId, message, cancellationToken);
        }

        request.Status = AccessRequestStatuses.Completed;
        _db.SetAuditAction(request, AuditActions.Completed);
        _db.AddAuditEvent(nameof(AccessRequest), request.Id.ToString(), AuditActions.AccessGranted, new
        {
            groupId,
            userObjectId = objectId,
            alreadyMember = result == AddMemberResult.AlreadyMember,
        });

        await _db.SaveChangesAsync(cancellationToken);

        return new(true, true, result == AddMemberResult.AlreadyMember
            ? "Brukeren var allerede medlem av gruppen. Bestillingen er fullført."
            : "Brukeren er lagt i Entra-gruppen. Bestillingen er fullført.");
    }

    private async Task<AccessGrantOutcome> FailAsync(
        AccessRequest request, string groupId, string error, CancellationToken cancellationToken)
    {
        _db.AddAuditEvent(nameof(AccessRequest), request.Id.ToString(), AuditActions.AccessGrantFailed, new
        {
            groupId,
            error,
        });

        await _db.SaveChangesAsync(cancellationToken);

        return new(true, false,
            $"{error} Bestillingen er godkjent, men tilgangen er ikke gitt. " +
            "Prøv igjen, eller gi tilgangen manuelt og marker bestillingen som fullført.");
    }
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;

namespace backend.Services;

public record AnonymizeResult(int EmployeeId, int AccessRequests, int AuditEventsRedacted);

// GDPR: innsyn (art. 15), anonymisering av ansatte som har sluttet (art. 17 og
// lagringsbegrensning) og standardinnhold i behandlingsprotokollen (art. 30).
public class GdprService
{
    // Erstatter innholdet i revisjonsrader om personen
    public const string RedactedChanges = "{\"anonymisert\":true}";

    private readonly AppDbContext _db;

    public GdprService(AppDbContext db)
    {
        _db = db;
    }

    // Alt appen har om personen, til innsynsbegjæring. Uttrekket logges.
    public async Task<object?> ExportAsync(int employeeId, CancellationToken cancellationToken = default)
    {
        var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId, cancellationToken);

        if (employee == null)
            return null;

        var objectId = employee.EntraObjectId;

        var ticketsReported = await _db.Tickets.AsNoTracking()
            .Where(t => t.ReportedByEmployeeId == employeeId)
            .OrderBy(t => t.CreatedAt)
            .Select(t => new { t.Id, t.Category, t.Description, t.Status, t.Priority, t.CreatedAt, t.ResolvedAt })
            .ToListAsync(cancellationToken);

        var ticketsAssigned = await _db.Tickets.AsNoTracking()
            .Where(t => t.AssignedToEmployeeId == employeeId)
            .OrderBy(t => t.CreatedAt)
            .Select(t => new { t.Id, t.Category, t.Status, t.CreatedAt })
            .ToListAsync(cancellationToken);

        var accessRequests = await _db.AccessRequests.AsNoTracking()
            .Where(r => r.EmployeeId == employeeId || (objectId != null && r.RequestedByObjectId == objectId))
            .OrderBy(r => r.CreatedAt)
            .Select(r => new { r.Id, r.EmployeeName, r.SystemName, r.Reason, r.ManagerName, r.Status, r.CreatedAt, r.ApprovedAt })
            .ToListAsync(cancellationToken);

        var assets = await _db.Assets.AsNoTracking()
            .Where(a => a.AssignedToEmployeeId == employeeId)
            .Select(a => new { a.Id, a.AssetTag, a.Type, a.Manufacturer, a.Model, a.SerialNumber, a.Status })
            .ToListAsync(cancellationToken);

        var processes = await _db.LifecycleProcesses.AsNoTracking()
            .Where(p => p.EmployeeId == employeeId)
            .Select(p => new
            {
                p.Id, p.Type, p.ReferenceDate, p.Status, p.CreatedAt, p.CompletedAt,
                Tasks = p.Tasks.Select(t => new { t.Title, t.DueDate, t.CompletedAt, t.Note }).ToList(),
            })
            .ToListAsync(cancellationToken);

        var employeeId2 = employeeId.ToString();
        var auditEvents = await _db.AuditEvents.AsNoTracking()
            .Where(e => (e.EntityType == nameof(Employee) && e.EntityId == employeeId2) ||
                (objectId != null && e.ActorObjectId == objectId))
            .OrderBy(e => e.Id)
            .Take(5000)
            .ToListAsync(cancellationToken);

        var appSignIn = objectId == null
            ? null
            : await _db.AppSignIns.AsNoTracking()
                .Where(s => s.EntraObjectId == objectId)
                .Select(s => new { s.Name, s.UserPrincipalName, s.FirstSeenAt, s.LastSeenAt, s.Visits })
                .FirstOrDefaultAsync(cancellationToken);

        _db.AddAuditEvent(nameof(Employee), employeeId2, AuditActions.GdprExported);
        await _db.SaveChangesAsync(cancellationToken);

        return new
        {
            exportedAt = DateTime.UtcNow,
            description = "Personopplysninger om den ansatte i Antire IT Operations (GDPR art. 15).",
            employee = new
            {
                employee.Id, employee.EmployeeNumber, employee.FirstName, employee.LastName, employee.Email,
                employee.Department, employee.JobTitle, employee.StartDate, employee.EndDate, employee.Status,
                employee.EntraObjectId, employee.AnonymizedAt,
            },
            ticketsReported,
            ticketsAssigned,
            accessRequests,
            assets,
            onboardingAndOffboarding = processes,
            appSignIn,
            auditEvents = auditEvents.Select(e => new
            {
                e.OccurredAt, e.ActorName, e.EntityType, e.EntityId, e.Action,
                Changes = e.Changes == null ? (JsonElement?)null : JsonDocument.Parse(e.Changes).RootElement.Clone(),
            }),
        };
    }

    // Fjerner personopplysningene om en ansatt som har sluttet. Raden, sakene og
    // revisjonsradene beholdes, men uten navn og identifikatorer. Gir en feilmelding
    // (string) hvis personen ikke kan anonymiseres ennå.
    public async Task<(AnonymizeResult? Result, string? Error)> AnonymizeAsync(
        int employeeId, CancellationToken cancellationToken = default)
    {
        var employee = await _db.Employees.FirstOrDefaultAsync(e => e.Id == employeeId, cancellationToken);

        if (employee == null)
            return (null, "Ukjent ansatt.");

        if (employee.AnonymizedAt != null)
            return (null, "Den ansatte er allerede anonymisert.");

        if (employee.Status != "Sluttet" || employee.EndDate == null || employee.EndDate.Value.Date > DateTime.UtcNow.Date)
            return (null, "Bare ansatte som har sluttet (sluttdato passert) kan anonymiseres.");

        if (await _db.Assets.AnyAsync(a => a.AssignedToEmployeeId == employeeId, cancellationToken))
            return (null, "Den ansatte har fortsatt utstyr. Registrer innlevering først.");

        if (await _db.LifecycleProcesses.AnyAsync(
                p => p.EmployeeId == employeeId && p.Status == LifecycleStatuses.Active, cancellationToken))
            return (null, "Offboarding eller onboarding pågår fortsatt. Fullfør eller avbryt den først.");

        var objectId = employee.EntraObjectId;
        var pseudonym = $"Anonymisert #{employee.Id}";

        employee.FirstName = "Anonymisert";
        employee.LastName = $"#{employee.Id}";
        employee.Email = $"anonymisert-{employee.Id}@invalid.local";
        employee.Department = "";
        employee.JobTitle = "";
        employee.EmployeeNumber = null;
        employee.EntraObjectId = null;
        employee.AnonymizedAt = DateTime.UtcNow;

        var requests = await _db.AccessRequests
            .Where(r => r.EmployeeId == employeeId || (objectId != null && r.RequestedByObjectId == objectId))
            .ToListAsync(cancellationToken);

        foreach (var request in requests)
        {
            if (request.EmployeeId == employeeId)
                request.EmployeeName = pseudonym;

            if (objectId != null && request.RequestedByObjectId == objectId)
                request.RequestedByObjectId = null;
        }

        await _db.SaveChangesAsync(cancellationToken);

        // Revisjonsloggen kan ellers ikke endres. Her vaskes bare innholdet i radene
        // om personen (direkte i databasen, utenom SaveChanges); radene beholdes.
        var employeeKey = employee.Id.ToString();
        var requestKeys = requests.Select(r => r.Id.ToString()).ToList();

        var redacted = await _db.AuditEvents
            .Where(e => (e.EntityType == nameof(Employee) && e.EntityId == employeeKey) ||
                (e.EntityType == nameof(AccessRequest) && requestKeys.Contains(e.EntityId)))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Changes, RedactedChanges), cancellationToken);

        if (objectId != null)
        {
            // Når personen brukte appen, trengs ikke lenger
            await _db.AppSignIns
                .Where(s => s.EntraObjectId == objectId)
                .ExecuteDeleteAsync(cancellationToken);

            redacted += await _db.AuditEvents
                .Where(e => e.ActorObjectId == objectId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.ActorObjectId, "anonymisert")
                    .SetProperty(e => e.ActorName, pseudonym), cancellationToken);
        }

        _db.AddAuditEvent(nameof(Employee), employeeKey, AuditActions.GdprAnonymized, new
        {
            accessRequests = requests.Count,
            auditEventsRedacted = redacted,
        });
        await _db.SaveChangesAsync(cancellationToken);

        return (new AnonymizeResult(employee.Id, requests.Count, redacted), null);
    }

    public static IReadOnlyList<ProcessingActivity> DefaultActivities() =>
    [
        new()
        {
            Name = "IT-drift og brukerstøtte",
            System = "Antire IT Operations",
            Purpose = "Håndtere saker, tilgangsbestillinger, IT-utstyr, onboarding og offboarding for ansatte, og føre revisjonslogg over endringer.",
            DataSubjects = "Ansatte og innleide",
            DataCategories = "Navn, e-post, ansattnummer, avdeling, stilling, start- og sluttdato, Entra-ID, saker og beskrivelser, tilganger, utstyr, logg over hvem som har endret hva, når hver bruker sist brukte appen",
            LegalBasis = LegalBases.Contract,
            Retention = "Så lenge arbeidsforholdet varer. Ansatte som har sluttet, anonymiseres etter fristen i GDPR-innstillingene. Saker og revisjonslogg beholdes uten personopplysninger.",
            Recipients = "Microsoft (Entra ID, Microsoft 365 og Azure) som databehandler",
            ThirdCountryTransfers = "Data lagres i Norge (Norway East). Microsoft kan behandle støttedata utenfor EØS etter EUs standardavtaler (SCC) og EU-U.S. Data Privacy Framework.",
            SecurityMeasures = "Innlogging med Entra ID og MFA, rollebasert tilgang, data adskilt per tenant, revisjonslogg, kryptering i overføring og lagring.",
            Owner = "IT-ansvarlig",
        },
    ];

    public async Task<List<ProcessingActivity>> GetActivitiesAsync(CancellationToken cancellationToken = default)
    {
        await GetSettingsAsync(cancellationToken);
        return await _db.ProcessingActivities.OrderBy(a => a.Name).ToListAsync(cancellationToken);
    }

    // Innstillingene for tenanten. Første gang legges også standardinnholdet i
    // behandlingsprotokollen inn (bare én gang, så slettede behandlinger ikke kommer tilbake).
    public async Task<GdprSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _db.GdprSettings.FirstOrDefaultAsync(cancellationToken);

        if (settings != null)
            return settings;

        settings = new GdprSettings();
        _db.GdprSettings.Add(settings);
        _db.ProcessingActivities.AddRange(DefaultActivities());

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // En annen forespørsel rakk å opprette innstillingene først
            _db.ChangeTracker.Clear();
            settings = await _db.GdprSettings.FirstAsync(cancellationToken);
        }

        return settings;
    }
}

using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;

namespace backend.Services;

// Onboarding og offboarding: maler, start av prosess per ansatt og avkrysning.
// En tenant uten maler får standardmalene første gang de trengs.
public class LifecycleService
{
    private readonly AppDbContext _db;
    private readonly ILogger<LifecycleService> _logger;

    public LifecycleService(AppDbContext db, ILogger<LifecycleService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public static IReadOnlyList<LifecycleTemplateTask> DefaultTemplates()
    {
        var order = 0;

        LifecycleTemplateTask On(string title, int days, string? category = null, string? description = null) =>
            new() { Type = LifecycleTypes.Onboarding, Title = title, OffsetDays = days, Category = category, Description = description, SortOrder = ++order };

        LifecycleTemplateTask Off(string title, int days, string? category = null, string? description = null, string? action = null) =>
            new() { Type = LifecycleTypes.Offboarding, Title = title, OffsetDays = days, Category = category, Description = description, Action = action, SortOrder = ++order };

        return
        [
            On("Opprett brukerkonto i Entra ID", -5, "Tilgangsbestilling"),
            On("Tildel lisenser", -3, "Tilgangsbestilling"),
            On("Klargjør PC og annet utstyr", -3, "Bestilling av utstyr", "Registrer utstyret under IT-utstyr og tildel det den ansatte."),
            On("Bestill tilganger til systemer", -2, "Tilgangsbestilling", "Bruk Tilgangsbestillinger, slik at tilgangene kan godkjennes og logges."),
            On("Gå gjennom IT-rutiner og sikkerhet", 0),
            On("Kontroller at MFA er registrert", 1, "Tilgangsbestilling"),

            Off("Overfør filer og e-post", -3, null, "Avtal med leder hva som skal overføres og til hvem."),
            Off("Deaktiver brukerkonto i Entra ID", 0, "Tilgangsbestilling",
                "Kan gjøres med knappen på oppgaven (Helpdesk.Admin med rolle i Entra, f.eks. User Administrator).",
                LifecycleTaskActions.DisableEntraAccount),
            Off("Fjern tilganger og gruppemedlemskap", 0, "Tilgangsbestilling", "Tilganger gitt via Entra-grupper kan fjernes direkte fra prosessen."),
            Off("Motta utstyr", 0, "Bestilling av utstyr", "Registrer innlevering av hvert utstyr («Mottatt»)."),
            Off("Fjern lisenser", 1, "Tilgangsbestilling"),
        ];
    }

    public async Task<List<LifecycleTemplateTask>> GetTemplatesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSeededAsync(cancellationToken);
        return await _db.LifecycleTemplateTasks
            .OrderBy(t => t.Type).ThenBy(t => t.SortOrder).ThenBy(t => t.Id)
            .ToListAsync(cancellationToken);
    }

    // Starter prosessen, eller gir den som allerede er aktiv
    public async Task<LifecycleProcess> StartAsync(
        Employee employee, string type, DateTime referenceDate, CancellationToken cancellationToken = default)
    {
        var existing = await _db.LifecycleProcesses
            .FirstOrDefaultAsync(p => p.EmployeeId == employee.Id && p.Type == type &&
                p.Status == LifecycleStatuses.Active, cancellationToken);

        if (existing != null)
            return existing;

        await EnsureSeededAsync(cancellationToken);

        var templates = await _db.LifecycleTemplateTasks
            .AsNoTracking()
            .Where(t => t.Type == type && t.IsActive)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Id)
            .ToListAsync(cancellationToken);

        // Ansatte uten dato (standardverdien) regnes fra i dag
        var date = DateTime.SpecifyKind(
            referenceDate.Year < 1900 ? DateTime.UtcNow.Date : referenceDate.Date, DateTimeKind.Utc);

        var process = new LifecycleProcess
        {
            EmployeeId = employee.Id,
            Type = type,
            ReferenceDate = date,
            Status = LifecycleStatuses.Active,
            CreatedAt = DateTime.UtcNow,
            Tasks = templates.Select(t => new LifecycleTask
            {
                Title = t.Title,
                Description = t.Description,
                Category = t.Category,
                DueDate = date.AddDays(t.OffsetDays),
                SortOrder = t.SortOrder,
                ServiceCatalogItemId = t.ServiceCatalogItemId,
                Action = t.Action,
            }).ToList(),
        };

        _db.LifecycleProcesses.Add(process);
        _db.SetAuditAction(process, AuditActions.LifecycleStarted);
        await _db.SaveChangesAsync(cancellationToken);

        return process;
    }

    // Krysser av (eller fjerner krysset). Prosessen fullføres når alle oppgaver er gjort.
    public async Task<LifecycleTask?> SetTaskDoneAsync(
        int taskId, bool done, string? note, string actorName, CancellationToken cancellationToken = default)
    {
        var task = await _db.LifecycleTasks.FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

        if (task == null)
            return null;

        var process = await _db.LifecycleProcesses
            .Include(p => p.Tasks)
            .FirstAsync(p => p.Id == task.ProcessId, cancellationToken);

        if (process.Status == LifecycleStatuses.Cancelled)
            throw new InvalidOperationException("Prosessen er avbrutt.");

        if (done && task.CompletedAt == null)
        {
            task.CompletedAt = DateTime.UtcNow;
            task.CompletedByName = actorName;
        }
        else if (!done)
        {
            task.CompletedAt = null;
            task.CompletedByName = null;
        }

        if (note != null)
            task.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        var allDone = process.Tasks.All(t => t.CompletedAt != null);

        if (allDone && process.Status == LifecycleStatuses.Active)
        {
            process.Status = LifecycleStatuses.Completed;
            process.CompletedAt = DateTime.UtcNow;
        }
        else if (!allDone && process.Status == LifecycleStatuses.Completed)
        {
            process.Status = LifecycleStatuses.Active;
            process.CompletedAt = null;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return task;
    }

    private async Task EnsureSeededAsync(CancellationToken cancellationToken)
    {
        if (await _db.LifecycleTemplateTasks.AnyAsync(cancellationToken))
            return;

        _db.LifecycleTemplateTasks.AddRange(DefaultTemplates());

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e)
        {
            _logger.LogInformation(e, "Standardmalene for onboarding fantes allerede.");

            foreach (var entry in _db.ChangeTracker.Entries<LifecycleTemplateTask>().ToList())
                entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        }
    }
}

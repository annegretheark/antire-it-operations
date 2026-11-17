using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;

namespace backend.Services;

// SLA-mål per tenant og frister på saker. En tenant uten mål får
// standardmålene første gang de trengs (samme mønster som tjenestekatalogen).
public class SlaService
{
    private readonly AppDbContext _db;
    private readonly ILogger<SlaService> _logger;

    public SlaService(AppDbContext db, ILogger<SlaService> logger)
    {
        _db = db;
        _logger = logger;
    }

    // Arbeidsdag = 8 timer (08–16)
    private const int WorkDay = 8 * 60;

    public static IReadOnlyList<SlaTarget> DefaultTargets() =>
    [
        new() { Priority = TicketPriorities.Critical, ResponseMinutes = 30, ResolutionMinutes = 4 * 60, TwentyFourSeven = true },
        new() { Priority = TicketPriorities.High, ResponseMinutes = 60, ResolutionMinutes = WorkDay },
        new() { Priority = TicketPriorities.Normal, ResponseMinutes = 4 * 60, ResolutionMinutes = 3 * WorkDay },
        new() { Priority = TicketPriorities.Low, ResponseMinutes = WorkDay, ResolutionMinutes = 5 * WorkDay },
    ];

    public async Task<List<SlaTarget>> GetTargetsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSeededAsync(cancellationToken);

        var targets = await _db.SlaTargets.ToListAsync(cancellationToken);

        return targets
            .OrderBy(t => Array.IndexOf(TicketPriorities.All, t.Priority))
            .ToList();
    }

    // Setter fristene ut fra prioriteten og når saken ble opprettet. Kall
    // EnsureSeededAsync før saken endres. Et tidligere markert brudd fjernes
    // hvis den nye fristen ikke er passert.
    public async Task ApplyDeadlinesAsync(Ticket ticket, CancellationToken cancellationToken = default)
    {
        var target = await _db.SlaTargets
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Priority == ticket.Priority, cancellationToken);

        if (target == null)
        {
            ticket.ResponseDueAt = null;
            ticket.ResolutionDueAt = null;
        }
        else
        {
            ticket.ResponseDueAt = SlaCalculator.AddTime(
                ticket.CreatedAt, target.ResponseMinutes, target.TwentyFourSeven);
            ticket.ResolutionDueAt = SlaCalculator.AddTime(
                ticket.CreatedAt, target.ResolutionMinutes, target.TwentyFourSeven);
        }

        var now = DateTime.UtcNow;

        if (ticket.ResponseDueAt == null || ticket.ResponseDueAt > now)
            ticket.ResponseBreachedAt = null;

        if (ticket.ResolutionDueAt == null || ticket.ResolutionDueAt > now)
            ticket.ResolutionBreachedAt = null;
    }

    // Oppretter standardmålene for en ny tenant. Lagrer, så kalles før andre endringer.
    public async Task EnsureSeededAsync(CancellationToken cancellationToken = default)
    {
        if (await _db.SlaTargets.AnyAsync(cancellationToken))
            return;

        _db.SlaTargets.AddRange(DefaultTargets());

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // En annen forespørsel rakk å opprette målene først (unik prioritet)
            _logger.LogInformation(ex, "Standard SLA-mål fantes allerede.");

            foreach (var entry in _db.ChangeTracker.Entries<SlaTarget>().ToList())
                entry.State = EntityState.Detached;
        }
    }
}

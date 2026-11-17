using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using backend.Models;
using backend.Services;

namespace backend.Data;

// Revisjonslogg: logger opprettelse, endring og sletting av entiteter merket med
// IAudited, fyller inn hvem og når på alle nye revisjonsrader, og stopper endring
// og sletting av revisjonsrader.
//
// Nye rader får ID først ved lagring, så hendelsene lagres i en andre runde
// (fra SavedChanges) i samme transaksjon som selve endringen.
public class AuditInterceptor : SaveChangesInterceptor
{
    // Behold æøå lesbart i databasen
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ICurrentUser? _currentUser;

    // Pågående lagring per kontekst (normalt én kontekst per forespørsel)
    private readonly ConditionalWeakTable<DbContext, SaveState> _states = new();

    public AuditInterceptor(ICurrentUser? currentUser = null)
    {
        _currentUser = currentUser;
    }

    private sealed class SaveState(List<PendingAudit> pending)
    {
        public List<PendingAudit> Pending { get; } = pending;
        public IDbContextTransaction? Transaction { get; set; }
    }

    // En endring som skal logges. Entry beholdes for å lese ID-en etter lagring.
    private sealed record PendingAudit(EntityEntry Entry, string Action, string? Changes);

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } context && BeforeSave(context) is { } state &&
            NeedsTransaction(context))
        {
            state.Transaction = context.Database.BeginTransaction();
        }

        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context && BeforeSave(context) is { } state &&
            NeedsTransaction(context))
        {
            state.Transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        }

        return result;
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData.Context is not { } context || !_states.TryGetValue(context, out var state))
            return result;

        _states.Remove(context);

        using (state.Transaction)
        {
            AddAuditEvents(context, state.Pending);
            context.SaveChanges();
            state.Transaction?.Commit();
        }

        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not { } context || !_states.TryGetValue(context, out var state))
            return result;

        _states.Remove(context);

        await using (state.Transaction)
        {
            AddAuditEvents(context, state.Pending);
            await context.SaveChangesAsync(cancellationToken);

            if (state.Transaction != null)
                await state.Transaction.CommitAsync(cancellationToken);
        }

        return result;
    }

    // Feilet eller avbrutt lagring: rull tilbake og glem endringene
    public override void SaveChangesFailed(DbContextErrorEventData eventData) =>
        Abort(eventData.Context);

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Abort(eventData.Context);
        return Task.CompletedTask;
    }

    public override void SaveChangesCanceled(DbContextEventData eventData) =>
        Abort(eventData.Context);

    public override Task SaveChangesCanceledAsync(
        DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        Abort(eventData.Context);
        return Task.CompletedTask;
    }

    private void Abort(DbContext? context)
    {
        if (context != null && _states.TryGetValue(context, out var state))
        {
            _states.Remove(context);
            state.Transaction?.Dispose();
        }
    }

    // Vern og innsamling før lagring. Gir null når ingenting skal logges.
    private SaveState? BeforeSave(DbContext context)
    {
        GuardAuditEvents(context);
        StampNewAuditEvents(context);

        var pending = CollectChanges(context);

        if (pending.Count == 0)
        {
            _states.Remove(context);
            return null;
        }

        var state = new SaveState(pending);
        _states.AddOrUpdate(context, state);
        return state;
    }

    private static bool NeedsTransaction(DbContext context) =>
        context.Database.IsRelational() && context.Database.CurrentTransaction == null;

    // Revisjonsloggen kan bare legges til, aldri endres eller slettes
    private static void GuardAuditEvents(DbContext context)
    {
        foreach (var entry in context.ChangeTracker.Entries<AuditEvent>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException(
                    "Revisjonsloggen kan ikke endres eller slettes.");
            }
        }
    }

    // Hvem og når settes alltid her, også for hendelser lagt til med AddAuditEvent
    private void StampNewAuditEvents(DbContext context)
    {
        foreach (var entry in context.ChangeTracker.Entries<AuditEvent>())
        {
            if (entry.State != EntityState.Added)
                continue;

            var audit = entry.Entity;
            audit.OccurredAt = DateTime.UtcNow;
            audit.ActorObjectId = _currentUser?.ObjectId ?? "system";
            audit.ActorName = _currentUser?.Name ?? _currentUser?.ObjectId ?? "system";
        }
    }

    private static List<PendingAudit> CollectChanges(DbContext context)
    {
        var pending = new List<PendingAudit>();
        var appContext = context as AppDbContext;

        foreach (var entry in context.ChangeTracker.Entries<IAudited>().ToList())
        {
            string? explicitAction = null;
            var hasAction = appContext?.TryTakeAuditAction(entry.Entity, out explicitAction) == true;

            var (defaultAction, changes) = entry.State switch
            {
                EntityState.Added => (AuditActions.Created, DescribeChanges(entry, useOriginal: false, useCurrent: true)),
                EntityState.Modified => (AuditActions.Updated, DescribeChanges(entry, useOriginal: true, useCurrent: true)),
                EntityState.Deleted => (AuditActions.Deleted, DescribeChanges(entry, useOriginal: true, useCurrent: false)),
                _ => (null, null),
            };

            if (defaultAction == null)
                continue;

            // En endring der ingen verdier faktisk endret seg, logges ikke
            if (changes == null && !hasAction)
                continue;

            pending.Add(new PendingAudit(
                entry,
                hasAction ? explicitAction! : defaultAction,
                changes == null ? null : JsonSerializer.Serialize(changes, JsonOptions)));
        }

        return pending;
    }

    // { "Felt": [før, etter] }. Id, TenantId og uendrede felter tas ikke med.
    private static Dictionary<string, object?[]>? DescribeChanges(
        EntityEntry entry, bool useOriginal, bool useCurrent)
    {
        var changes = new Dictionary<string, object?[]>();

        foreach (var property in entry.Properties)
        {
            var name = property.Metadata.Name;

            if (property.Metadata.IsPrimaryKey() || name == nameof(ITenantOwned.TenantId))
                continue;

            if (entry.State == EntityState.Modified && !property.IsModified)
                continue;

            var before = useOriginal ? property.OriginalValue : null;
            var after = useCurrent ? property.CurrentValue : null;

            if (Equals(before, after))
                continue;

            changes[name] = [before, after];
        }

        return changes.Count == 0 ? null : changes;
    }

    private static void AddAuditEvents(DbContext context, List<PendingAudit> pending)
    {
        foreach (var item in pending)
        {
            // Uten AcceptAllChanges ville andre runde lagret endringene på nytt
            if (item.Entry.State is not (EntityState.Unchanged or EntityState.Detached))
            {
                throw new NotSupportedException(
                    "Revisjonsloggen krever SaveChanges med acceptAllChangesOnSuccess = true.");
            }

            var key = item.Entry.Metadata.FindPrimaryKey()!.Properties
                .Select(p => item.Entry.Property(p.Name).CurrentValue?.ToString());

            context.Add(new AuditEvent
            {
                EntityType = item.Entry.Metadata.ClrType.Name,
                EntityId = string.Join(",", key),
                Action = item.Action,
                Changes = item.Changes,
            });
        }
    }
}

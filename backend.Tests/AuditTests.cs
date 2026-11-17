using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;
using backend.Services;

namespace backend.Tests;

// Revisjonsloggen i AppDbContext, testet mot SQLite i minnet (ingen Docker nødvendig).
public class AuditTests : IDisposable
{
    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";

    private readonly SqliteConnection _connection;

    public AuditTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        using var db = CreateContext(TenantA);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private AppDbContext CreateContext(
        string? tenantId, string? objectId = "user-oid", string? name = "Kari Nordmann")
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new AuditInterceptor(
                objectId == null ? null : new FixedUser(objectId, name)))
            .Options;

        return new AppDbContext(options, new FixedTenant(tenantId));
    }

    private static Dictionary<string, JsonElement[]> ChangesOf(AuditEvent e) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement[]>>(e.Changes!)!;

    private async Task<int> CreateTicketAsync(string tenantId = TenantA)
    {
        await using var db = CreateContext(tenantId);
        var ticket = new Ticket { Category = "Feil", Description = "Skriveren virker ikke" };
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();
        return ticket.Id;
    }

    [Fact]
    public async Task Creating_a_ticket_logs_created_with_the_new_id_and_actor()
    {
        var id = await CreateTicketAsync();

        await using var db = CreateContext(TenantA);
        var audit = Assert.Single(await db.AuditEvents.ToListAsync());

        Assert.Equal(AuditActions.Created, audit.Action);
        Assert.Equal(nameof(Ticket), audit.EntityType);
        Assert.Equal(id.ToString(), audit.EntityId);
        Assert.Equal("user-oid", audit.ActorObjectId);
        Assert.Equal("Kari Nordmann", audit.ActorName);
        Assert.Equal(TenantA, audit.TenantId);

        var changes = ChangesOf(audit);
        Assert.Equal("Feil", changes["Category"][1].GetString());
        Assert.False(changes.ContainsKey("Id"));
        Assert.False(changes.ContainsKey("TenantId"));
    }

    [Fact]
    public async Task Changing_status_logs_only_the_changed_field_with_before_and_after()
    {
        var id = await CreateTicketAsync();

        await using (var db = CreateContext(TenantA, "agent-oid", "Ola Agent"))
        {
            var ticket = await db.Tickets.SingleAsync(t => t.Id == id);
            ticket.Status = "Under behandling";
            ticket.Priority = "Normal"; // samme verdi som før
            await db.SaveChangesAsync();
        }

        await using var check = CreateContext(TenantA);
        var update = await check.AuditEvents.SingleAsync(e => e.Action == AuditActions.Updated);

        Assert.Equal("Ola Agent", update.ActorName);
        var changes = ChangesOf(update);
        Assert.Equal(["Status"], changes.Keys);
        Assert.Equal("Ny", changes["Status"][0].GetString());
        Assert.Equal("Under behandling", changes["Status"][1].GetString());
    }

    [Fact]
    public async Task Saving_without_real_changes_logs_nothing()
    {
        var id = await CreateTicketAsync();

        await using (var db = CreateContext(TenantA))
        {
            var ticket = await db.Tickets.SingleAsync(t => t.Id == id);
            ticket.Status = "Ny";
            await db.SaveChangesAsync();
        }

        await using var check = CreateContext(TenantA);
        Assert.Equal(1, await check.AuditEvents.CountAsync());
    }

    [Fact]
    public async Task Business_action_replaces_updated()
    {
        await using (var db = CreateContext(TenantA))
        {
            db.AccessRequests.Add(new AccessRequest
            {
                EmployeeName = "Kari", SystemName = "NetSuite", Reason = "Ny i økonomi", ManagerName = "Per",
            });
            await db.SaveChangesAsync();
        }

        await using (var db = CreateContext(TenantA, "approver-oid", "Per Leder"))
        {
            var request = await db.AccessRequests.SingleAsync();
            request.Status = "Godkjent";
            db.SetAuditAction(request, AuditActions.Approved);
            await db.SaveChangesAsync();
        }

        await using var check = CreateContext(TenantA);
        var approved = await check.AuditEvents.SingleAsync(e => e.Action == AuditActions.Approved);
        Assert.Equal(nameof(AccessRequest), approved.EntityType);
        Assert.Equal("Per Leder", approved.ActorName);
        Assert.Equal("Godkjent", ChangesOf(approved)["Status"][1].GetString());
    }

    [Fact]
    public async Task Explicit_event_is_saved_with_details()
    {
        await using (var db = CreateContext(TenantA))
        {
            db.AddAuditEvent("EmployeeSync", "entra", AuditActions.Synced, new { created = 2, updated = 5 });
            await db.SaveChangesAsync();
        }

        await using var check = CreateContext(TenantA);
        var sync = await check.AuditEvents.SingleAsync();
        Assert.Equal(AuditActions.Synced, sync.Action);
        Assert.Equal(2, JsonDocument.Parse(sync.Changes!).RootElement.GetProperty("created").GetInt32());
    }

    [Fact]
    public async Task Deleting_logs_deleted_with_previous_values()
    {
        await using (var db = CreateContext(TenantA))
        {
            db.TicketCategoryGroups.Add(new TicketCategoryGroup { Category = "Feil", EntraGroupId = "group-1" });
            await db.SaveChangesAsync();
        }

        await using (var db = CreateContext(TenantA))
        {
            db.TicketCategoryGroups.Remove(await db.TicketCategoryGroups.SingleAsync());
            await db.SaveChangesAsync();
        }

        await using var check = CreateContext(TenantA);
        var deleted = await check.AuditEvents.SingleAsync(e => e.Action == AuditActions.Deleted);
        var changes = ChangesOf(deleted);
        Assert.Equal("group-1", changes["EntraGroupId"][0].GetString());
        Assert.Equal(JsonValueKind.Null, changes["EntraGroupId"][1].ValueKind);
    }

    [Fact]
    public async Task Audit_events_cannot_be_changed_or_deleted()
    {
        await CreateTicketAsync();

        await using var db = CreateContext(TenantA);
        var audit = await db.AuditEvents.SingleAsync();

        audit.Action = "tampered";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

        db.Entry(audit).State = EntityState.Deleted;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Audit_events_are_separated_per_tenant()
    {
        await CreateTicketAsync(TenantA);
        await CreateTicketAsync(TenantB);

        await using var dbA = CreateContext(TenantA);
        await using var dbB = CreateContext(TenantB);

        Assert.All(await dbA.AuditEvents.ToListAsync(), e => Assert.Equal(TenantA, e.TenantId));
        Assert.All(await dbB.AuditEvents.ToListAsync(), e => Assert.Equal(TenantB, e.TenantId));
        Assert.Equal(1, await dbA.AuditEvents.CountAsync());
    }

    [Fact]
    public async Task Changes_without_signed_in_user_are_logged_as_system()
    {
        await using (var db = CreateContext(TenantA, objectId: null))
        {
            db.Tickets.Add(new Ticket { Category = "Annet", Description = "Bakgrunnsjobb" });
            await db.SaveChangesAsync();
        }

        await using var check = CreateContext(TenantA);
        var audit = await check.AuditEvents.SingleAsync();
        Assert.Equal("system", audit.ActorObjectId);
        Assert.Equal("system", audit.ActorName);
    }

    private sealed class FixedTenant(string? tenantId) : ITenantContext
    {
        public string? TenantId => tenantId;
    }

    private sealed class FixedUser(string objectId, string? name) : ICurrentUser
    {
        public string? ObjectId => objectId;
        public string? Name => name;
    }
}

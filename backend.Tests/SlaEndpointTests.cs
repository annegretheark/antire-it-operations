using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using backend.Models;
using backend.Services;

namespace backend.Tests;

// SLA gjennom API-et: frister, respons, løst, prioritetsendring, mål og bruddjobben
public class SlaEndpointTests(SqliteWebApplicationFactory factory)
    : IClassFixture<SqliteWebApplicationFactory>
{
    private const string TenantA = TestAuthHandler.AllowedTenantId;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Admin() => factory.CreateClientWithRoles(AppRoles.HelpdeskAdmin);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Json))!;

    private async Task<Ticket> CreateTicketAsync(string priority)
    {
        var client = Admin();
        var item = await client.PostAsJsonAsync("/api/servicecatalog", new
        {
            name = $"SLA {priority} {Guid.NewGuid():N}"[..30],
            category = "Feil",
            defaultPriority = priority,
        });
        Assert.Equal(HttpStatusCode.Created, item.StatusCode);
        var itemId = (await ReadAsync<ServiceCatalogItem>(item)).Id;

        var response = await client.PostAsJsonAsync("/api/tickets", new
        {
            serviceCatalogItemId = itemId, description = "SLA-test",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<Ticket>(response);
    }

    private async Task<Ticket> PatchAsync(int id, string endpoint, object body)
    {
        var response = await Admin().PatchAsJsonAsync($"/api/tickets/{id}/{endpoint}", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<Ticket>(response);
    }

    [Fact]
    public async Task Critical_ticket_gets_deadlines_around_the_clock()
    {
        var ticket = await CreateTicketAsync(TicketPriorities.Critical);

        Assert.Equal(TicketPriorities.Critical, ticket.Priority);
        Assert.Equal(TicketStatuses.New, ticket.Status);
        Assert.Equal(TimeSpan.FromMinutes(30), ticket.ResponseDueAt - ticket.CreatedAt);
        Assert.Equal(TimeSpan.FromHours(4), ticket.ResolutionDueAt - ticket.CreatedAt);
        Assert.Equal(SlaStatuses.Ok, ticket.SlaStatus);
    }

    [Fact]
    public async Task Normal_ticket_gets_deadlines_in_working_hours()
    {
        var ticket = await CreateTicketAsync(TicketPriorities.Normal);

        Assert.Equal(
            SlaCalculator.AddTime(ticket.CreatedAt, 4 * 60, twentyFourSeven: false),
            ticket.ResponseDueAt);
        Assert.Equal(
            SlaCalculator.AddTime(ticket.CreatedAt, 3 * 8 * 60, twentyFourSeven: false),
            ticket.ResolutionDueAt);
    }

    [Fact]
    public async Task Priority_change_recalculates_deadlines_from_creation()
    {
        var ticket = await CreateTicketAsync(TicketPriorities.Low);

        var updated = await PatchAsync(ticket.Id, "priority", new { priority = TicketPriorities.Critical });

        Assert.Equal(ticket.CreatedAt.AddMinutes(30), updated.ResponseDueAt);
        Assert.Equal(ticket.CreatedAt.AddHours(4), updated.ResolutionDueAt);
        Assert.Null(updated.FirstRespondedAt); // prioritet teller ikke som respons
    }

    [Fact]
    public async Task First_response_resolve_and_reopen_are_recorded()
    {
        var ticket = await CreateTicketAsync(TicketPriorities.Normal);

        var assigned = await PatchAsync(ticket.Id, "assign", new { assignedToEmployeeId = (int?)null });
        Assert.Null(assigned.FirstRespondedAt); // ingen endring, ingen respons

        var inProgress = await PatchAsync(ticket.Id, "status", new { status = TicketStatuses.InProgress });
        Assert.NotNull(inProgress.FirstRespondedAt);

        var resolved = await PatchAsync(ticket.Id, "status", new { status = TicketStatuses.Resolved });
        Assert.NotNull(resolved.ResolvedAt);
        Assert.Equal(inProgress.FirstRespondedAt, resolved.FirstRespondedAt);
        Assert.Equal(SlaStatuses.Met, resolved.SlaStatus);

        var reopened = await PatchAsync(ticket.Id, "status", new { status = TicketStatuses.New });
        Assert.Null(reopened.ResolvedAt);
        Assert.Equal(SlaStatuses.Ok, reopened.SlaStatus);
    }

    [Theory]
    [InlineData("status", "Løst")]
    [InlineData("priority", "Kritisk")]
    public async Task Old_norwegian_values_are_rejected(string endpoint, string value)
    {
        var ticket = await CreateTicketAsync(TicketPriorities.Normal);

        var response = await Admin().PatchAsJsonAsync(
            $"/api/tickets/{ticket.Id}/{endpoint}", new Dictionary<string, string> { [endpoint] = value });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Monitor_marks_breach_once_and_logs_it_in_history()
    {
        int ticketId;
        await using (var db = factory.CreateDbContext(TenantA))
        {
            var created = DateTime.UtcNow.AddDays(-2);
            var ticket = new Ticket
            {
                Category = "Feil",
                Description = "Glemt sak",
                Priority = TicketPriorities.Critical,
                CreatedAt = created,
                ResponseDueAt = created.AddMinutes(30),
                ResolutionDueAt = created.AddHours(4),
            };
            db.Tickets.Add(ticket);
            await db.SaveChangesAsync();
            ticketId = ticket.Id;
        }

        var monitor = factory.Services.GetRequiredService<SlaMonitorService>();

        Assert.True(await monitor.CheckTenantAsync(TenantA, DateTime.UtcNow) >= 1);
        Assert.Equal(0, await monitor.CheckTenantAsync(TenantA, DateTime.UtcNow));

        await using (var db = factory.CreateDbContext(TenantA))
        {
            var ticket = await db.Tickets.SingleAsync(t => t.Id == ticketId);
            Assert.Equal(ticket.ResponseDueAt, ticket.ResponseBreachedAt);
            Assert.Equal(ticket.ResolutionDueAt, ticket.ResolutionBreachedAt);

            var audit = await db.AuditEvents.SingleAsync(e =>
                e.EntityType == nameof(Ticket) && e.EntityId == ticketId.ToString() &&
                e.Action == AuditActions.SlaBreached);
            Assert.Equal("system", audit.ActorObjectId);
        }

        // Saken vises som brudd i API-et (for dashboard og filter)
        var tickets = await ReadAsync<List<Ticket>>(await Admin().GetAsync("/api/tickets"));
        Assert.Equal(SlaStatuses.Breached, tickets.Single(t => t.Id == ticketId).SlaStatus);
    }

    [Fact]
    public async Task Monitor_ignores_other_tenants()
    {
        await using (var db = factory.CreateDbContext(TestAuthHandler.OtherAllowedTenantId))
        {
            var created = DateTime.UtcNow.AddDays(-2);
            db.Tickets.Add(new Ticket
            {
                Category = "Feil", Description = "Annen tenant", CreatedAt = created,
                ResponseDueAt = created.AddMinutes(30), ResolutionDueAt = created.AddHours(4),
            });
            await db.SaveChangesAsync();
        }

        var monitor = factory.Services.GetRequiredService<SlaMonitorService>();
        await monitor.CheckTenantAsync(TenantA, DateTime.UtcNow);

        await using var check = factory.CreateDbContext(TestAuthHandler.OtherAllowedTenantId);
        var ticket = await check.Tickets.SingleAsync(t => t.Description == "Annen tenant");
        Assert.Null(ticket.ResolutionBreachedAt);
    }

    [Fact]
    public async Task Admin_sees_and_updates_sla_targets()
    {
        var client = Admin();

        var targets = await ReadAsync<List<SlaTarget>>(await client.GetAsync("/api/slatargets"));
        Assert.Equal(TicketPriorities.All, targets.Select(t => t.Priority));
        Assert.True(targets.Single(t => t.Priority == TicketPriorities.Critical).TwentyFourSeven);

        var response = await client.PutAsJsonAsync("/api/slatargets/low", new
        {
            responseMinutes = 600, resolutionMinutes = 4800, twentyFourSeven = false,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var low = (await ReadAsync<List<SlaTarget>>(await client.GetAsync("/api/slatargets")))
            .Single(t => t.Priority == TicketPriorities.Low);
        Assert.Equal(600, low.ResponseMinutes);
        Assert.Equal(4800, low.ResolutionMinutes);
    }

    [Theory]
    [InlineData("normal", 0, 60)]
    [InlineData("normal", 120, 60)]
    [InlineData("normal", 60, 100000)]
    public async Task Invalid_sla_target_is_rejected(string priority, int response, int resolution)
    {
        var result = await Admin().PutAsJsonAsync($"/api/slatargets/{priority}", new
        {
            responseMinutes = response, resolutionMinutes = resolution, twentyFourSeven = false,
        });

        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
    }

    [Fact]
    public async Task Unknown_priority_is_not_found()
    {
        var result = await Admin().PutAsJsonAsync("/api/slatargets/Kritisk", new
        {
            responseMinutes = 30, resolutionMinutes = 60, twentyFourSeven = true,
        });

        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
    }
}

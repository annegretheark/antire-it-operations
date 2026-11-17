using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using backend.Data;
using backend.Models;
using backend.Services;

namespace backend.Tests;

// API-et med SQLite i minnet i stedet for PostgreSQL, for tester som trenger data
public class SqliteWebApplicationFactory : TestWebApplicationFactory
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private bool _created;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        _connection.Open();

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
            services.AddDbContext<AppDbContext>((sp, options) => options
                .UseSqlite(_connection)
                .AddInterceptors(sp.GetRequiredService<AuditInterceptor>()));
        });
    }

    // Kontekst for oppsett av testdata i en bestemt tenant (lagres som "system")
    public AppDbContext CreateDbContext(string tenantId)
    {
        var options = Services.GetRequiredService<IServiceScopeFactory>()
            .CreateScope().ServiceProvider
            .GetRequiredService<DbContextOptions<AppDbContext>>();

        var db = new AppDbContext(options, new FixedTenant(tenantId));

        if (!_created)
        {
            db.Database.EnsureCreated();
            _created = true;
        }

        return db;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
            _connection.Dispose();
    }

    private sealed class FixedTenant(string tenantId) : ITenantContext
    {
        public string? TenantId => tenantId;
    }
}

// Historikk per sak og revisjonsloggen gjennom API-et
public class HistoryEndpointTests(SqliteWebApplicationFactory factory)
    : IClassFixture<SqliteWebApplicationFactory>
{
    private const string TenantA = TestAuthHandler.AllowedTenantId;
    private const string TenantB = TestAuthHandler.OtherAllowedTenantId;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record HistoryEvent(
        long Id,
        DateTime OccurredAt,
        string ActorObjectId,
        string ActorName,
        string EntityType,
        string EntityId,
        string Action,
        Dictionary<string, JsonElement>? Changes);

    private async Task<(int TicketId, int EmployeeId)> SeedTicketAsync(string tenantId = TenantA)
    {
        await using var db = factory.CreateDbContext(tenantId);

        var employee = new Employee
        {
            FirstName = "Ola", LastName = "Agent", Email = $"ola{Guid.NewGuid():N}@test.no",
        };
        db.Employees.Add(employee);

        var ticket = new Ticket { Category = "Feil", Description = "Skriveren virker ikke" };
        db.Tickets.Add(ticket);

        await db.SaveChangesAsync();
        return (ticket.Id, employee.Id);
    }

    private async Task<List<HistoryEvent>> GetHistoryAsync(HttpClient client, int ticketId)
    {
        var response = await client.GetAsync($"/api/tickets/{ticketId}/history");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<HistoryEvent>>(Json))!;
    }

    [Fact]
    public async Task Status_priority_and_assignee_changes_show_in_ticket_history_with_who_and_when()
    {
        var (ticketId, employeeId) = await SeedTicketAsync();
        var agent = factory.CreateClientWithRoles(AppRoles.HelpdeskAgent);
        var before = DateTime.UtcNow.AddSeconds(-1);

        Assert.True((await agent.PatchAsJsonAsync(
            $"/api/tickets/{ticketId}/status", new { status = "Under behandling" })).IsSuccessStatusCode);
        Assert.True((await agent.PatchAsJsonAsync(
            $"/api/tickets/{ticketId}/priority", new { priority = "Høy" })).IsSuccessStatusCode);
        Assert.True((await agent.PatchAsJsonAsync(
            $"/api/tickets/{ticketId}/assign", new { assignedToEmployeeId = employeeId })).IsSuccessStatusCode);

        var history = await GetHistoryAsync(agent, ticketId);

        Assert.Equal(AuditActions.Created, history[0].Action);
        Assert.Equal("system", history[0].ActorObjectId);

        var updates = history.Where(e => e.Action == AuditActions.Updated).ToList();
        Assert.Equal(3, updates.Count);

        Assert.All(updates, e =>
        {
            Assert.Equal(TestAuthHandler.TestObjectId, e.ActorObjectId);
            Assert.Equal("test-user", e.ActorName);
            Assert.InRange(e.OccurredAt, before, DateTime.UtcNow.AddSeconds(1));
            Assert.Single(e.Changes!);
        });

        Assert.Equal("Ny", updates[0].Changes!["Status"][0].GetString());
        Assert.Equal("Under behandling", updates[0].Changes!["Status"][1].GetString());
        Assert.Equal("Normal", updates[1].Changes!["Priority"][0].GetString());
        Assert.Equal("Høy", updates[1].Changes!["Priority"][1].GetString());
        Assert.Equal(JsonValueKind.Null, updates[2].Changes!["AssignedToEmployeeId"][0].ValueKind);
        Assert.Equal(employeeId, updates[2].Changes!["AssignedToEmployeeId"][1].GetInt32());
    }

    [Fact]
    public async Task User_without_role_cannot_see_history_of_others_ticket()
    {
        var (ticketId, _) = await SeedTicketAsync();
        var user = factory.CreateClientWithRoles();

        var response = await user.GetAsync($"/api/tickets/{ticketId}/history");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Reporter_can_see_history_of_own_ticket()
    {
        int ticketId;
        await using (var db = factory.CreateDbContext(TenantA))
        {
            var reporter = new Employee
            {
                FirstName = "Kari", LastName = "Bruker", Email = "kari@test.no",
                EntraObjectId = TestAuthHandler.TestObjectId,
            };
            db.Employees.Add(reporter);
            await db.SaveChangesAsync();

            var ticket = new Ticket
            {
                Category = "Feil", Description = "Egen sak", ReportedByEmployeeId = reporter.Id,
            };
            db.Tickets.Add(ticket);
            await db.SaveChangesAsync();
            ticketId = ticket.Id;
        }

        var history = await GetHistoryAsync(factory.CreateClientWithRoles(), ticketId);

        Assert.Equal(AuditActions.Created, Assert.Single(history).Action);
    }

    [Fact]
    public async Task Ticket_history_is_not_visible_from_other_tenant()
    {
        var (ticketId, _) = await SeedTicketAsync(TenantA);
        var otherTenantAdmin = factory.CreateClientInTenant(TenantB, AppRoles.HelpdeskAdmin);

        var response = await otherTenantAdmin.GetAsync($"/api/tickets/{ticketId}/history");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Admin_sees_audit_log_for_own_tenant_only()
    {
        var (ticketA, _) = await SeedTicketAsync(TenantA);
        var (ticketB, _) = await SeedTicketAsync(TenantB);
        var admin = factory.CreateClientWithRoles(AppRoles.HelpdeskAdmin);

        var response = await admin.GetAsync("/api/audit?entityType=Ticket&take=500");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var events = (await response.Content.ReadFromJsonAsync<List<HistoryEvent>>(Json))!;
        Assert.Contains(events, e => e.EntityId == ticketA.ToString());
        Assert.DoesNotContain(events, e => e.EntityId == ticketB.ToString());
        Assert.All(events, e => Assert.Equal(nameof(Ticket), e.EntityType));
    }

    [Theory]
    [InlineData("POST", "/api/audit")]
    [InlineData("PUT", "/api/audit/1")]
    [InlineData("PATCH", "/api/audit/1")]
    [InlineData("DELETE", "/api/audit/1")]
    public async Task Audit_log_has_no_endpoints_that_change_it(string method, string path)
    {
        await SeedTicketAsync();
        var admin = factory.CreateClientWithRoles(AppRoles.HelpdeskAdmin);

        var response = await admin.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = JsonContent.Create(new { action = "tampered" }),
        });

        Assert.Contains(response.StatusCode,
            new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
    }
}

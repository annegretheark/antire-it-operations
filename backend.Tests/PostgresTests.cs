using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using backend.Controllers;
using backend.Models;
using backend.Services;

namespace backend.Tests;

// Det SQLite-testene ikke kan bevise: migreringene, tenant-filteret, jsonb,
// tidssoner, sekvenser og indekser i ekte PostgreSQL (0.10b)
public class PostgresTests(PostgresWebApplicationFactory factory)
    : IClassFixture<PostgresWebApplicationFactory>
{
    private const string TenantA = TestAuthHandler.AllowedTenantId;
    private const string TenantB = TestAuthHandler.OtherAllowedTenantId;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Admin(string tenantId = TenantA) =>
        factory.CreateClientInTenant(tenantId, AppRoles.HelpdeskAdmin);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private async Task<Ticket> CreateTicketAsync(string priority, string tenantId = TenantA)
    {
        var client = Admin(tenantId);
        var item = await ReadAsync<ServiceCatalogItem>(await client.PostAsJsonAsync("/api/servicecatalog", new
        {
            name = $"PG {Guid.NewGuid():N}"[..30], category = "Feil", defaultPriority = priority,
        }));

        return await ReadAsync<Ticket>(await client.PostAsJsonAsync("/api/tickets", new
        {
            serviceCatalogItemId = item.Id, description = "PostgreSQL-test",
        }));
    }

    [PostgresFact]
    public async Task Migrations_build_the_current_model()
    {
        factory.CreateClient(); // starter API-et og kjører migreringene
        await using var db = factory.CreateDbContext(TenantA);

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [PostgresFact]
    public async Task Tenant_filter_separates_data()
    {
        var ticketA = await CreateTicketAsync(TicketPriorities.Normal, TenantA);
        var ticketB = await CreateTicketAsync(TicketPriorities.Normal, TenantB);

        var listA = await ReadAsync<List<Ticket>>(await Admin(TenantA).GetAsync("/api/tickets"));
        Assert.Contains(listA, t => t.Id == ticketA.Id);
        Assert.DoesNotContain(listA, t => t.Id == ticketB.Id);

        Assert.Equal(HttpStatusCode.NotFound,
            (await Admin(TenantA).GetAsync($"/api/tickets/{ticketB.Id}/history")).StatusCode);
    }

    [PostgresFact]
    public async Task Audit_history_is_stored_as_jsonb_and_read_back()
    {
        var ticket = await CreateTicketAsync(TicketPriorities.Normal);

        await ReadAsync<Ticket>(await Admin().PatchAsJsonAsync(
            $"/api/tickets/{ticket.Id}/status", new { status = TicketStatuses.InProgress }));

        var history = await ReadAsync<List<AuditEventDto>>(
            await Admin().GetAsync($"/api/tickets/{ticket.Id}/history"));

        var update = history.Last(e => e.Action == AuditActions.Updated);
        Assert.Equal("in_progress", update.Changes!.Value.GetProperty("Status")[1].GetString());
    }

    [PostgresFact]
    public async Task Critical_ticket_deadlines_are_stored_as_utc()
    {
        var ticket = await CreateTicketAsync(TicketPriorities.Critical);

        await using var db = factory.CreateDbContext(TenantA);
        var stored = await db.Tickets.SingleAsync(t => t.Id == ticket.Id);

        Assert.Equal(DateTimeKind.Utc, stored.ResponseDueAt!.Value.Kind);
        Assert.Equal(TimeSpan.FromMinutes(30), stored.ResponseDueAt - stored.CreatedAt);
        Assert.Equal(TimeSpan.FromHours(4), stored.ResolutionDueAt - stored.CreatedAt);
    }

    [PostgresFact]
    public async Task Employee_numbers_come_from_the_sequence()
    {
        var client = factory.CreateClientWithRoles(AppRoles.EmployeeSyncAdmin);

        async Task<string> CreateAsync() =>
            (await ReadAsync<Employee>(await client.PostAsJsonAsync("/api/employees", new
            {
                firstName = "Pia", lastName = "Postgres", email = $"{Guid.NewGuid():N}@t.no",
                department = "IT", jobTitle = "Test", startDate = "2026-01-01",
            }))).EmployeeNumber!;

        var first = await CreateAsync();
        var second = await CreateAsync();

        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.NotEqual(first, second);
    }

    [PostgresFact]
    public async Task Asset_tags_are_unique_regardless_of_case()
    {
        var tag = $"PG-{Guid.NewGuid():N}"[..14];
        var client = factory.CreateClientWithRoles(AppRoles.HelpdeskAgent);

        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync("/api/assets", new { assetTag = tag, type = "laptop" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync("/api/assets", new { assetTag = tag.ToLowerInvariant(), type = "laptop" })).StatusCode);
    }

    [PostgresFact]
    public async Task Simulated_alert_becomes_ticket_with_jsonb_payload()
    {
        var result = await ReadAsync<AlertIngestResult>(await Admin().PostAsJsonAsync("/api/alerts/simulate", new
        {
            source = "azure-monitor", severity = "Sev0", title = "Database nede",
        }));

        Assert.Equal(AlertOutcomes.TicketCreated, result.Outcome);

        await using var db = factory.CreateDbContext(TenantA);
        var alert = await db.Alerts.SingleAsync(a => a.Id == result.AlertId);
        Assert.Contains("simulated", alert.RawPayload);
        Assert.Equal(TicketPriorities.Critical, (await db.Tickets.SingleAsync(t => t.Id == result.TicketId)).Priority);
    }

    [PostgresFact]
    public async Task Duplicate_catalog_name_is_rejected()
    {
        await ReadAsync<List<ServiceCatalogItem>>(await Admin().GetAsync("/api/servicecatalog"));

        var response = await Admin().PostAsJsonAsync("/api/servicecatalog", new { name = "Feil", category = "Feil" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}

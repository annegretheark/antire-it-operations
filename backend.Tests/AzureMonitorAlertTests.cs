using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using backend.Models;
using backend.Services;

namespace backend.Tests;

// Fase 4.2: varsler fra Azure Monitor blir saker (henting med Reader)
public class AzureMonitorAlertTests(SqliteWebApplicationFactory factory)
    : IClassFixture<SqliteWebApplicationFactory>
{
    private const string TenantA = TestAuthHandler.AllowedTenantId;
    private const string TenantB = TestAuthHandler.OtherAllowedTenantId;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Admin(string tenantId = TenantA) =>
        factory.CreateClientInTenant(tenantId, AppRoles.HelpdeskAdmin);

    private AlertMonitorService Monitor => factory.Services.GetRequiredService<AlertMonitorService>();

    private async Task SyncSubscriptionAsync()
    {
        factory.Azure.Subscriptions[TenantA] = [new("d3f46a10-2d76-465d-837c-a283331837d5", "Aitrening", "Enabled")];
        var response = await Admin().PostAsync("/api/azure/subscriptions/sync", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static AlertSignal Signal(string key, string state, DateTime firedAt, string severity = "Sev1") =>
        new(AlertSources.AzureMonitor, key, "CPU over 90 % (vm-web)", severity,
            "/subscriptions/x/resourceGroups/rg-web/providers/Microsoft.Compute/virtualMachines/vm-web",
            "https://portal.azure.com/#view/x", state, firedAt, "{}");

    private async Task<Alert> AlertAsync(string key)
    {
        await using var db = factory.CreateDbContext(TenantA);
        return await db.Alerts.SingleAsync(a => a.ExternalId == key);
    }

    private async Task PollAsync(params AlertSignal[] signals)
    {
        factory.Azure.MonitorAlerts[TenantA] = [.. signals];
        await Monitor.RunTenantAsync(TenantA);
    }

    [Fact]
    public async Task Fired_alert_becomes_ticket_and_firing_again_reopens_it()
    {
        await SyncSubscriptionAsync();
        var key = $"rule-{Guid.NewGuid():N}|vm-web";
        var t0 = DateTime.UtcNow.AddHours(-3);

        await PollAsync(Signal(key, "fired", t0));
        await PollAsync(Signal(key, "fired", t0)); // samme forekomst hentet igjen

        var alert = await AlertAsync(key);
        Assert.Equal(1, alert.Occurrences);
        var ticketId = alert.TicketId!.Value;

        await using (var db = factory.CreateDbContext(TenantA))
        {
            var ticket = await db.Tickets.SingleAsync(t => t.Id == ticketId);
            Assert.Equal(TicketPriorities.High, ticket.Priority);
            Assert.Equal("Feil", ticket.Category);
            Assert.Equal(1, await db.Tickets.CountAsync(t => t.AlertId == alert.Id));
        }

        // Løst hos kilden, og IT løser saken
        await PollAsync(Signal(key, "resolved", t0));
        Assert.Equal(AlertStates.Resolved, (await AlertAsync(key)).State);
        await Admin().PatchAsJsonAsync($"/api/tickets/{ticketId}/status", new { status = "resolved" });

        // Ny forekomst av samme regel på samme ressurs: saken gjenåpnes, ingen ny sak
        await PollAsync(Signal(key, "fired", t0.AddHours(2)));

        alert = await AlertAsync(key);
        Assert.Equal(2, alert.Occurrences);
        Assert.Equal(ticketId, alert.TicketId);

        await using (var db = factory.CreateDbContext(TenantA))
        {
            Assert.Equal(TicketStatuses.New, (await db.Tickets.SingleAsync(t => t.Id == ticketId)).Status);
            Assert.Equal(1, await db.Tickets.CountAsync(t => t.AlertId == alert.Id));
        }
    }

    [Fact]
    public async Task Old_resolved_and_new_fired_occurrence_does_not_flap()
    {
        await SyncSubscriptionAsync();
        var key = $"rule-{Guid.NewGuid():N}|vm-db";
        var now = DateTime.UtcNow;

        // Begge forekomstene er med i hver henting (siste 7 dager)
        for (var i = 0; i < 3; i++)
            await PollAsync(Signal(key, "resolved", now.AddDays(-2)), Signal(key, "fired", now.AddHours(-1)));

        var alert = await AlertAsync(key);
        Assert.Equal(AlertStates.Fired, alert.State);
        Assert.Equal(1, alert.Occurrences);

        await using var db = factory.CreateDbContext(TenantA);
        Assert.False(await db.AuditEvents.AnyAsync(e =>
            e.EntityType == nameof(Ticket) && e.EntityId == alert.TicketId.ToString() &&
            e.Action == AuditActions.AlertResolved));
    }

    [Fact]
    public async Task Status_explains_missing_subscriptions_and_access()
    {
        // Tenant B har ingen synkede abonnementer i denne testklassen
        await Monitor.RunTenantAsync(TenantB);
        var statusB = await Admin(TenantB).GetFromJsonAsync<List<AlertSourceStatus>>("/api/alerts/sources", Json);
        Assert.Contains("Ingen Azure-abonnementer", statusB!.Single(s => s.Source == AlertSources.AzureMonitor).Message);

        await SyncSubscriptionAsync();
        factory.Azure.MonitorFailure = new AzureUnavailableException(
            "Appen har ikke tilgang i Azure. Gi appen rollen Reader på abonnementene.", 403);

        try
        {
            await Monitor.RunTenantAsync(TenantA);
            var statusA = await Admin().GetFromJsonAsync<List<AlertSourceStatus>>("/api/alerts/sources", Json);
            var azure = statusA!.Single(s => s.Source == AlertSources.AzureMonitor);
            Assert.False(azure.Ok);
            Assert.Contains("Reader", azure.Message);
        }
        finally
        {
            factory.Azure.MonitorFailure = null;
        }
    }
}

// Tolkning av svaret fra Alerts Management API
public class AzureMonitorParseTests
{
    private const string Page = """
    {
      "value": [
        {
          "id": "/subscriptions/s1/providers/Microsoft.AlertsManagement/alerts/11111111-1111-1111-1111-111111111111",
          "name": "CPU over 90",
          "properties": { "essentials": {
            "severity": "Sev1",
            "monitorCondition": "Fired",
            "alertState": "New",
            "alertRule": "/subscriptions/s1/resourceGroups/rg/providers/microsoft.insights/metricAlerts/CPU over 90",
            "targetResource": "/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm-web",
            "targetResourceName": "vm-web",
            "startDateTime": "2026-11-17T10:15:00Z"
          } }
        },
        {
          "id": "/subscriptions/s1/providers/Microsoft.AlertsManagement/alerts/22222222-2222-2222-2222-222222222222",
          "name": "Disk nesten full",
          "properties": { "essentials": {
            "severity": "Sev2",
            "monitorCondition": "Resolved",
            "alertRule": "/subscriptions/s1/resourceGroups/rg/providers/microsoft.insights/metricAlerts/Disk",
            "targetResource": "/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm-db",
            "startDateTime": "2026-11-16T08:00:00Z"
          } }
        },
        { "id": "uten-essentials", "properties": {} }
      ],
      "nextLink": null
    }
    """;

    [Fact]
    public void Parses_severity_state_resource_and_stable_id()
    {
        var signals = AzureService.ParseMonitorAlerts(JsonDocument.Parse(Page).RootElement);

        Assert.Equal(2, signals.Count);

        var cpu = signals[0];
        Assert.Equal(AlertSources.AzureMonitor, cpu.Source);
        Assert.Equal("Sev1", cpu.Severity);
        Assert.Equal(AlertStates.Fired, cpu.State);
        Assert.Equal("CPU over 90 (vm-web)", cpu.Title);
        Assert.EndsWith("virtualMachines/vm-web", cpu.ResourceId);
        Assert.Equal(new DateTime(2026, 11, 17, 10, 15, 0, DateTimeKind.Utc), cpu.FiredAt);
        Assert.StartsWith("https://portal.azure.com/", cpu.PortalUrl);

        // Regel + ressurs, ikke forekomstens ID
        Assert.DoesNotContain("11111111", cpu.ExternalId);
        Assert.Contains("metricalerts/cpu over 90|", cpu.ExternalId);

        Assert.Equal(AlertStates.Resolved, signals[1].State);
    }

    [Fact]
    public void Very_long_ids_are_hashed_to_fit()
    {
        var longRule = new string('r', 600);
        var page = $$"""
        { "value": [ { "id": "a1", "name": "x", "properties": { "essentials": {
            "severity": "Sev3", "alertRule": "{{longRule}}", "targetResource": "t" } } } ] }
        """;

        var signal = Assert.Single(AzureService.ParseMonitorAlerts(JsonDocument.Parse(page).RootElement));

        Assert.StartsWith("sha256:", signal.ExternalId);
        Assert.True(signal.ExternalId.Length <= 500);
    }
}

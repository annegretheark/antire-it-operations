using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using backend.Controllers;
using backend.Models;
using backend.Services;

namespace backend.Tests;

// Fase 4: varsler blir saker (4.1), Defender (4.3) og Teams (4.4)
public class AlertTests(SqliteWebApplicationFactory factory)
    : IClassFixture<SqliteWebApplicationFactory>
{
    private const string TenantA = TestAuthHandler.AllowedTenantId;
    private const string TenantB = TestAuthHandler.OtherAllowedTenantId;
    private const string WebhookUrl = "https://prod-01.westeurope.logic.azure.com:443/workflows/abc/triggers/manual/paths/invoke?sig=hemmelig123";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Admin(string tenantId = TenantA) =>
        factory.CreateClientInTenant(tenantId, AppRoles.HelpdeskAdmin);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Json))!;

    private async Task<AlertIngestResult> SimulateAsync(
        string source, string severity, string? externalId = null, string state = "fired",
        string tenantId = TenantA)
    {
        var response = await Admin(tenantId).PostAsJsonAsync("/api/alerts/simulate", new
        {
            source, severity, externalId, state, title = "Mistenkelig pålogging", resourceId = "vm-web",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<AlertIngestResult>(response);
    }

    private async Task<Ticket> TicketAsync(int id, string tenantId = TenantA)
    {
        await using var db = factory.CreateDbContext(tenantId);
        return await db.Tickets.SingleAsync(t => t.Id == id);
    }

    private async Task<List<AuditEvent>> TicketAuditAsync(int id)
    {
        await using var db = factory.CreateDbContext(TenantA);
        return await db.AuditEvents
            .Where(e => e.EntityType == nameof(Ticket) && e.EntityId == id.ToString())
            .ToListAsync();
    }

    private async Task SetWebhookAsync(string? url, bool notifyCritical = false)
    {
        var response = await Admin().PutAsJsonAsync("/api/teams/settings", new
        {
            webhookUrl = url, notifyCriticalManualTickets = notifyCritical, removeWebhook = url == null,
        });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static string Id() => $"ext-{Guid.NewGuid():N}";

    [Fact]
    public async Task Defender_alert_becomes_ticket_with_mapped_priority_and_sla()
    {
        var result = await SimulateAsync("defender", "high");

        Assert.Equal(AlertOutcomes.TicketCreated, result.Outcome);
        var ticket = await TicketAsync(result.TicketId!.Value);
        Assert.Equal(TicketPriorities.High, ticket.Priority);
        Assert.Equal("Sikkerhet", ticket.Category);
        Assert.Equal(TicketSources.Alert, ticket.Source);
        Assert.Equal(result.AlertId, ticket.AlertId);
        Assert.NotNull(ticket.ResponseDueAt);
        Assert.Contains("[Microsoft Defender] Mistenkelig pålogging", ticket.Description);
        Assert.Contains("Ressurs: vm-web", ticket.Description);
    }

    [Theory]
    [InlineData("azure-monitor", "Sev0", TicketPriorities.Critical)]
    [InlineData("azure-monitor", "Sev1", TicketPriorities.High)]
    [InlineData("azure-monitor", "Sev2", TicketPriorities.Normal)]
    [InlineData("defender", "medium", TicketPriorities.Normal)]
    public async Task Severity_maps_to_priority(string source, string severity, string priority)
    {
        var result = await SimulateAsync(source, severity);

        Assert.Equal(priority, (await TicketAsync(result.TicketId!.Value)).Priority);
    }

    [Theory]
    [InlineData("azure-monitor", "Sev3")]
    [InlineData("defender", "low")]
    public async Task Low_severity_is_stored_without_ticket(string source, string severity)
    {
        var result = await SimulateAsync(source, severity);

        Assert.Equal(AlertOutcomes.StoredOnly, result.Outcome);
        Assert.Null(result.TicketId);
    }

    [Fact]
    public async Task Same_alert_again_gives_no_new_ticket_but_counts_occurrence()
    {
        var externalId = Id();
        var first = await SimulateAsync("defender", "high", externalId);
        var again = await SimulateAsync("defender", "high", externalId);

        Assert.Equal(AlertOutcomes.Recurred, again.Outcome);
        Assert.Equal(first.TicketId, again.TicketId);
        Assert.Equal(first.AlertId, again.AlertId);

        await using var db = factory.CreateDbContext(TenantA);
        Assert.Equal(2, (await db.Alerts.SingleAsync(a => a.Id == first.AlertId)).Occurrences);
        Assert.Contains(await TicketAuditAsync(first.TicketId!.Value), e => e.Action == AuditActions.AlertRecurred);
    }

    [Fact]
    public async Task Fetching_the_same_occurrence_again_changes_nothing()
    {
        using var scope = factory.Services.GetRequiredService<IServiceScopeFactory>().CreateTenantScope(TenantA);
        var ingestion = scope.ServiceProvider.GetRequiredService<AlertIngestionService>();
        var signal = new AlertSignal("defender", Id(), "Samme", "high", null, null, "fired", DateTime.UtcNow, null);

        var first = await ingestion.IngestAsync(signal);
        var auditBefore = (await TicketAuditAsync(first.TicketId!.Value)).Count;
        var second = await ingestion.IngestAsync(signal);

        Assert.Equal(AlertOutcomes.Unchanged, second.Outcome);
        Assert.Equal(auditBefore, (await TicketAuditAsync(first.TicketId!.Value)).Count);
    }

    [Fact]
    public async Task Alert_after_resolved_ticket_reopens_it()
    {
        var externalId = Id();
        var first = await SimulateAsync("azure-monitor", "Sev1", externalId);
        var ticketId = first.TicketId!.Value;

        var resolved = await Admin().PatchAsJsonAsync($"/api/tickets/{ticketId}/status", new { status = "resolved" });
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);

        var again = await SimulateAsync("azure-monitor", "Sev1", externalId);

        Assert.Equal(AlertOutcomes.TicketReopened, again.Outcome);
        var ticket = await TicketAsync(ticketId);
        Assert.Equal(TicketStatuses.New, ticket.Status);
        Assert.Null(ticket.ResolvedAt);
        Assert.Contains(await TicketAuditAsync(ticketId), e => e.Action == AuditActions.ReopenedByAlert);
    }

    [Fact]
    public async Task Resolved_at_source_is_logged_but_ticket_stays_open()
    {
        var externalId = Id();
        var first = await SimulateAsync("defender", "high", externalId);

        var resolved = await SimulateAsync("defender", "high", externalId, state: "resolved");

        Assert.Equal(AlertOutcomes.Resolved, resolved.Outcome);
        Assert.Equal(TicketStatuses.New, (await TicketAsync(first.TicketId!.Value)).Status);
        Assert.Contains(await TicketAuditAsync(first.TicketId!.Value), e => e.Action == AuditActions.AlertResolved);
    }

    [Fact]
    public async Task Same_external_id_in_other_tenant_is_a_separate_alert()
    {
        var externalId = Id();
        var inA = await SimulateAsync("defender", "high", externalId);
        var inB = await SimulateAsync("defender", "high", externalId, tenantId: TenantB);

        Assert.Equal(AlertOutcomes.TicketCreated, inB.Outcome);
        Assert.NotEqual(inA.AlertId, inB.AlertId);
        Assert.Equal(TenantB, (await TicketAsync(inB.TicketId!.Value, TenantB)).TenantId);

        var listA = await ReadAsync<List<AlertDto>>(await Admin().GetAsync("/api/alerts"));
        Assert.DoesNotContain(listA, a => a.Id == inB.AlertId);
    }

    [Fact]
    public async Task Rules_control_threshold_priority_and_ticket_creation()
    {
        var rules = await ReadAsync<List<AlertRule>>(await Admin(TenantB).GetAsync("/api/alertrules"));
        Assert.Equal("Sev2", rules.Single(r => r.Source == "azure-monitor").MinSeverity);
        Assert.Equal("medium", rules.Single(r => r.Source == "defender").MinSeverity);

        Assert.Equal(HttpStatusCode.OK, (await Admin(TenantB).PutAsJsonAsync("/api/alertrules/defender", new
        {
            minSeverity = "high", category = "Sikkerhet", priorityOverride = "critical",
            notifyTeams = false, createTickets = true,
        })).StatusCode);

        var medium = await SimulateAsync("defender", "medium", tenantId: TenantB);
        Assert.Equal(AlertOutcomes.StoredOnly, medium.Outcome);

        var high = await SimulateAsync("defender", "high", tenantId: TenantB);
        Assert.Equal(TicketPriorities.Critical, (await TicketAsync(high.TicketId!.Value, TenantB)).Priority);

        Assert.Equal(HttpStatusCode.BadRequest, (await Admin(TenantB).PutAsJsonAsync("/api/alertrules/defender", new
        {
            minSeverity = "Sev1", category = "Sikkerhet", notifyTeams = true, createTickets = true,
        })).StatusCode);

        // Tilbake til standard for de andre testene i tenant B
        await Admin(TenantB).PutAsJsonAsync("/api/alertrules/defender", new
        {
            minSeverity = "medium", category = "Sikkerhet", notifyTeams = true, createTickets = true,
        });
    }

    [Fact]
    public async Task Monitor_ingests_defender_alerts_and_reports_missing_permission()
    {
        var monitor = factory.Services.GetRequiredService<AlertMonitorService>();
        var externalId = Id();
        factory.Graph.DefenderAlerts[TenantA] =
        [
            new("defender", externalId, "Skadevare oppdaget", "high", null, "https://security.microsoft.com/alerts/1",
                "fired", DateTime.UtcNow, "{}"),
        ];

        try
        {
            await monitor.RunTenantAsync(TenantA);
            await monitor.RunTenantAsync(TenantA); // samme varsel igjen: ingen ny sak

            await using (var db = factory.CreateDbContext(TenantA))
            {
                var alert = await db.Alerts.SingleAsync(a => a.ExternalId == externalId);
                Assert.Equal(1, alert.Occurrences);
                Assert.Equal(1, await db.Tickets.CountAsync(t => t.AlertId == alert.Id));
            }

            factory.Graph.DefenderFailure = new GraphUnavailableException(
                "Appen mangler rettigheten SecurityAlert.Read.All i Entra ID (admin-samtykke).", 403);
            await monitor.RunTenantAsync(TenantA);

            var sources = await ReadAsync<List<AlertSourceStatus>>(await Admin().GetAsync("/api/alerts/sources"));
            var defender = sources.Single(s => s.Source == "defender");
            Assert.False(defender.Ok);
            Assert.Contains("SecurityAlert.Read.All", defender.Message);
        }
        finally
        {
            factory.Graph.DefenderAlerts.TryRemove(TenantA, out _);
            factory.Graph.DefenderFailure = null;
        }
    }

    [Fact]
    public async Task Teams_gets_card_for_new_alert_ticket_and_retries_after_failure()
    {
        await SetWebhookAsync(WebhookUrl);

        try
        {
            // Hele adressen sendes aldri tilbake
            var settings = await ReadAsync<TeamsSettingsDto>(await Admin().GetAsync("/api/teams/settings"));
            Assert.True(settings.Configured);
            Assert.DoesNotContain("hemmelig", settings.MaskedWebhookUrl);
            Assert.EndsWith("123", settings.MaskedWebhookUrl);

            var ok = await SimulateAsync("defender", "high");
            Assert.Contains(factory.Teams.Sent, m =>
                m.Url == WebhookUrl && m.Payload.Contains($"Ny sak #{ok.TicketId} fra varsel"));

            // Teams feiler: saken lages likevel, og meldingen ligger i kø
            factory.Teams.Failure = new TeamsDeliveryException("Teams svarte med feil (500).");
            var failed = await SimulateAsync("defender", "high");
            Assert.Equal(AlertOutcomes.TicketCreated, failed.Outcome);

            await using (var db = factory.CreateDbContext(TenantA))
            {
                var pending = await db.TeamsNotifications.SingleAsync(n => n.TicketId == failed.TicketId);
                Assert.Equal(TeamsNotificationStatuses.Pending, pending.Status);
                Assert.Contains("500", pending.LastError);
            }

            factory.Teams.Failure = null;
            await factory.Services.GetRequiredService<AlertMonitorService>().RunTenantAsync(TenantA);

            await using (var db = factory.CreateDbContext(TenantA))
            {
                var sent = await db.TeamsNotifications.SingleAsync(n => n.TicketId == failed.TicketId);
                Assert.Equal(TeamsNotificationStatuses.Sent, sent.Status);
                Assert.Equal(2, sent.Attempts);
            }
        }
        finally
        {
            factory.Teams.Failure = null;
            await SetWebhookAsync(null);
        }
    }

    [Fact]
    public async Task Critical_manual_ticket_notifies_teams_only_when_enabled()
    {
        await SetWebhookAsync(WebhookUrl, notifyCritical: false);

        try
        {
            var item = await ReadAsync<ServiceCatalogItem>(await Admin().PostAsJsonAsync("/api/servicecatalog", new
            {
                name = $"Kritisk {Guid.NewGuid():N}"[..30], category = "Feil", defaultPriority = "critical",
            }));

            async Task<int> CreateCriticalAsync() =>
                (await ReadAsync<Ticket>(await Admin().PostAsJsonAsync("/api/tickets", new
                {
                    serviceCatalogItemId = item.Id, description = "Alt er nede",
                }))).Id;

            var silent = await CreateCriticalAsync();
            Assert.DoesNotContain(factory.Teams.Sent, m => m.Payload.Contains($"#{silent}\""));

            await SetWebhookAsync(WebhookUrl, notifyCritical: true);
            var loud = await CreateCriticalAsync();
            Assert.Contains(factory.Teams.Sent, m => m.Payload.Contains($"Ny kritisk sak #{loud}"));
        }
        finally
        {
            await SetWebhookAsync(null);
        }
    }

    [Theory]
    [InlineData("http://prod-01.westeurope.logic.azure.com/workflows/abc")]
    [InlineData("https://example.com/webhook")]
    [InlineData("https://logic.azure.com.evil.no/x")]
    public async Task Only_teams_workflow_urls_are_accepted(string url)
    {
        var response = await Admin().PutAsJsonAsync("/api/teams/settings", new
        {
            webhookUrl = url, notifyCriticalManualTickets = false,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Test_message_reports_missing_webhook_and_secret_is_masked_in_audit()
    {
        await SetWebhookAsync(null);
        var missing = await Admin().PostAsync("/api/teams/test", null);
        Assert.Equal(HttpStatusCode.BadGateway, missing.StatusCode);

        await SetWebhookAsync(WebhookUrl);

        try
        {
            Assert.Equal(HttpStatusCode.OK, (await Admin().PostAsync("/api/teams/test", null)).StatusCode);
            Assert.Contains(factory.Teams.Sent, m => m.Payload.Contains("Testmelding"));

            await using var db = factory.CreateDbContext(TenantA);
            var audit = await db.AuditEvents.Where(e => e.EntityType == nameof(TeamsSettings)).ToListAsync();
            Assert.NotEmpty(audit);
            Assert.All(audit, e => Assert.DoesNotContain("CfDJ", e.Changes ?? ""));
            Assert.All(audit, e => Assert.DoesNotContain("hemmelig", e.Changes ?? ""));
        }
        finally
        {
            await SetWebhookAsync(null);
        }
    }
}

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using backend.Models;
using backend.Services;

namespace backend.Tests;

// API-et med den ukentlige jobben slått på, men uten kjøring ved oppstart
public class OpsReportJobWebApplicationFactory : SqliteWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("OpsReport:Enabled", "true");
        builder.UseSetting("OpsReport:RunOnStart", "false");
    }
}

// Ukentlig driftsrapport og Teams-kort, steg 4 (FASE5_DRIFTSRAPPORT.md kap. 8).
// Testene i klassen kjører etter hverandre og bruker hver sin uke.
public class OpsReportJobTests(OpsReportJobWebApplicationFactory factory) : IClassFixture<OpsReportJobWebApplicationFactory>
{
    private const string WebhookUrl = "https://prod-01.westeurope.logic.azure.com:443/workflows/abc/triggers/manual/paths/invoke?sig=hemmelig123";

    private OpsReportMonitorService Job => factory.Services.GetRequiredService<OpsReportMonitorService>();

    private static DateTime Utc(int y, int mo, int d, int h, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    private async Task<int> ReportsAsync(string tenant, DateTime periodStart)
    {
        await using var db = factory.CreateDbContext(tenant);
        return await db.OpsReports.CountAsync(r => r.PeriodStart == periodStart);
    }

    [Fact]
    public async Task Job_runs_on_monday_after_seven_once_per_week_and_tenant()
    {
        // Mandag 10. mars 2025: kl. 06:30 og 07:30 norsk tid (UTC+1)
        var before = Utc(2025, 3, 10, 5, 30);
        var after = Utc(2025, 3, 10, 6, 30);
        var (start, _) = OpsReportService.PreviousWeek(after);

        Assert.False(Job.IsDue(before));
        Assert.Equal(0, await Job.RunAsync(before));
        Assert.Equal(0, await ReportsAsync(TestAuthHandler.AllowedTenantId, start));

        Assert.Equal(2, await Job.RunAsync(after)); // begge tillatte tenanter
        Assert.Equal(0, await Job.RunAsync(after.AddHours(1))); // som etter en omstart

        Assert.Equal(1, await ReportsAsync(TestAuthHandler.AllowedTenantId, start));
        Assert.Equal(1, await ReportsAsync(TestAuthHandler.OtherAllowedTenantId, start));

        await using var db = factory.CreateDbContext(TestAuthHandler.AllowedTenantId);
        Assert.Equal(OpsReportService.SystemActor, (await db.OpsReports.SingleAsync(r => r.PeriodStart == start)).CreatedBy);
    }

    [Fact]
    public async Task Missing_report_is_made_later_in_the_week()
    {
        // Onsdag 19. mars 2025 (appen var nede mandag)
        var wednesday = Utc(2025, 3, 19, 12);
        var (start, _) = OpsReportService.PreviousWeek(wednesday);

        Assert.True(Job.IsDue(wednesday));
        await Job.RunAsync(wednesday);
        Assert.Equal(1, await ReportsAsync(TestAuthHandler.AllowedTenantId, start));
    }

    [Fact]
    public async Task Teams_card_is_queued_for_a_new_report_only()
    {
        var admin = factory.CreateClientWithRoles(AppRoles.HelpdeskAdmin);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/teams/settings", new
        {
            webhookUrl = WebhookUrl, notifyCriticalManualTickets = false, removeWebhook = false,
        })).StatusCode);

        var monday = Utc(2025, 3, 24, 9);
        await Job.RunAsync(monday);
        await Job.RunAsync(monday.AddHours(2));

        await using var db = factory.CreateDbContext(TestAuthHandler.AllowedTenantId);
        var (start, _) = OpsReportService.PreviousWeek(monday);
        var report = await db.OpsReports.SingleAsync(r => r.PeriodStart == start);

        var queued = Assert.Single(await db.TeamsNotifications
            .Where(n => n.Kind == "ops_report" && n.Payload.Contains($"?driftsrapport={report.Id}")).ToListAsync());
        Assert.Null(queued.TicketId);
        var card = System.Text.RegularExpressions.Regex.Unescape(queued.Payload); // Å og ø er unicode-kodet i JSON
        Assert.Contains("Åpne rapporten", card);
        Assert.Contains("Nøkkeltallene er beregnet", card);
        Assert.DoesNotContain("Skrevet av AI", card); // AI er av her
    }

    [Fact]
    public void Card_shows_ai_text_only_when_ai_succeeded()
    {
        var metrics = new OpsReportMetrics(DateTime.UtcNow,
            new OpsTicketMetrics(3, 2, new Dictionary<string, int> { [TicketPriorities.Critical] = 1 }, 0),
            new OpsSlaMetrics(1, 0, 0), new OpsAlertMetrics([], [], []), new OpsAccessMetrics(0, null),
            new OpsLifecycleMetrics(0, 0), new OpsM365Metrics(null, "Mangler lisens"));

        var ok = new OpsReport
        {
            Id = 7, PeriodStart = Utc(2025, 3, 2, 23), PeriodEnd = Utc(2025, 3, 9, 23),
            AiStatus = OpsReportAiStatuses.Ok, AiSummary = "Rolig uke.", AiActions = "[\"Følg opp SLA\"]",
        };
        var okCard = System.Text.RegularExpressions.Regex.Unescape(TeamsCards.ForOpsReport(ok, metrics, "https://app/?driftsrapport=7"));
        Assert.Contains("Skrevet av AI: Rolig uke.", okCard);
        Assert.Contains("Følg opp SLA", okCard);
        Assert.Contains("3.3. – 9.3.2025", okCard);

        var failed = new OpsReport { Id = 8, PeriodStart = ok.PeriodStart, PeriodEnd = ok.PeriodEnd, AiStatus = OpsReportAiStatuses.Failed };
        Assert.DoesNotContain("Skrevet av AI", TeamsCards.ForOpsReport(failed, metrics, "https://app/?driftsrapport=8"));
    }
}

// Av som standard: jobben lager ingen rapporter
public class OpsReportJobDisabledTests(SqliteWebApplicationFactory factory) : IClassFixture<SqliteWebApplicationFactory>
{
    [Fact]
    public async Task Job_is_off_by_default()
    {
        var job = factory.Services.GetRequiredService<OpsReportMonitorService>();
        Assert.False(job.Enabled);
        Assert.Equal(0, await job.RunAsync(new DateTime(2025, 3, 10, 9, 0, 0, DateTimeKind.Utc)));
    }
}

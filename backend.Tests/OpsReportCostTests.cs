using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using backend.Models;
using backend.Services;

namespace backend.Tests;

// Kostnader fra Azure Cost Management i driftsrapporten, steg 5 (FASE5_DRIFTSRAPPORT.md).
// Den falske Azure-tjenesten gir kostnadene; hver test bruker egne tenant-ID-er.
public class OpsReportCostTests(SqliteWebApplicationFactory factory) : IClassFixture<SqliteWebApplicationFactory>
{
    private static readonly DateTime WeekStart = new(2025, 3, 2, 23, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WeekEnd = new(2025, 3, 9, 23, 0, 0, DateTimeKind.Utc);

    private static string NewTenant() => Guid.NewGuid().ToString();

    private async Task<string> SubscriptionAsync(string tenant, string name = "Abonnement-Hemmelig")
    {
        var id = Guid.NewGuid().ToString();
        await using var db = factory.CreateDbContext(tenant);
        db.AzureSubscriptions.Add(new AzureSubscription { SubscriptionId = id, DisplayName = name, State = "Enabled" });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<OpsReport> GenerateAsync(string tenant)
    {
        using var scope = factory.Services.GetRequiredService<IServiceScopeFactory>().CreateTenantScope(tenant);
        var (report, _) = await scope.ServiceProvider.GetRequiredService<OpsReportService>()
            .GenerateAsync(WeekStart, WeekEnd, WeekEnd.AddHours(8), OpsReportService.SystemActor);
        return report;
    }

    [Fact]
    public async Task Costs_are_summed_over_subscriptions_and_services()
    {
        var tenant = NewTenant();
        var a = await SubscriptionAsync(tenant);
        var b = await SubscriptionAsync(tenant, "Abonnement-To");
        var denied = await SubscriptionAsync(tenant, "Uten-tilgang");
        factory.Azure.CostDenied.Add(denied);

        factory.Azure.Costs[(a, WeekStart)] = new() { ["Virtual Machines"] = 600m, ["Storage"] = 50m, ["Bandwidth"] = 1m };
        factory.Azure.Costs[(b, WeekStart)] = new() { ["Virtual Machines"] = 200m, ["Azure Database for PostgreSQL"] = 300m,
            ["Key Vault"] = 2m, ["Log Analytics"] = 20m };
        factory.Azure.Costs[(a, WeekStart.AddDays(-7))] = new() { ["Virtual Machines"] = 1000m };

        var costs = OpsReportService.ReadMetrics(await GenerateAsync(tenant))!.Costs!;

        Assert.Null(costs.UnavailableReason);
        Assert.Equal("NOK", costs.Currency);
        Assert.Equal(1173m, costs.Total);
        Assert.Equal(1000m, costs.PreviousTotal);
        Assert.Equal(17.3m, costs.ChangePercent);
        Assert.Equal(2, costs.SubscriptionsCounted);
        Assert.Equal(1, costs.SubscriptionsSkipped);
        Assert.Equal(OpsReportService.TopServiceCount, costs.TopServices.Count);
        Assert.Equal(new OpsServiceCost("Virtual Machines", 800m), costs.TopServices[0]);
        Assert.DoesNotContain(costs.TopServices, s => s.Service == "Bandwidth"); // nummer 6
    }

    [Fact]
    public async Task Change_is_empty_when_last_week_was_zero()
    {
        var tenant = NewTenant();
        var a = await SubscriptionAsync(tenant);
        factory.Azure.Costs[(a, WeekStart)] = new() { ["Storage"] = 10m };

        var costs = OpsReportService.ReadMetrics(await GenerateAsync(tenant))!.Costs!;

        Assert.Equal(10m, costs.Total);
        Assert.Equal(0m, costs.PreviousTotal);
        Assert.Null(costs.ChangePercent);
    }

    [Fact]
    public async Task Report_is_made_when_cost_management_fails_or_nothing_is_synced()
    {
        var empty = NewTenant();
        Assert.Contains("synket", OpsReportService.ReadMetrics(await GenerateAsync(empty))!.Costs!.UnavailableReason);

        var tenant = NewTenant();
        await SubscriptionAsync(tenant);
        factory.Azure.CostFailure = new AzureUnavailableException("Azure begrenser antall kall akkurat nå.", 429);
        try
        {
            var report = await GenerateAsync(tenant);
            var costs = OpsReportService.ReadMetrics(report)!.Costs!;
            Assert.Null(costs.Total);
            Assert.Contains("begrenser", costs.UnavailableReason);
        }
        finally
        {
            factory.Azure.CostFailure = null;
        }
    }

    [Fact]
    public async Task Metrics_hold_no_subscription_names_or_ids()
    {
        var tenant = NewTenant();
        var a = await SubscriptionAsync(tenant);
        factory.Azure.Costs[(a, WeekStart)] = new() { ["Virtual Machines"] = 5m };

        var report = await GenerateAsync(tenant);

        Assert.DoesNotContain("Abonnement-Hemmelig", report.Metrics);
        Assert.DoesNotContain(a, report.Metrics);
        Assert.Contains("Virtual Machines", report.Metrics);
    }

    [Fact]
    public void Cost_rows_are_read_by_column_name()
    {
        var page = JsonDocument.Parse("""
            { "properties": {
                "columns": [ { "name": "Cost", "type": "Number" }, { "name": "ServiceName", "type": "String" },
                             { "name": "Currency", "type": "String" } ],
                "rows": [ [ 12.5, "Storage", "NOK" ], [ 3, "", "NOK" ] ] } }
            """).RootElement;

        var rows = AzureService.ParseCostRows(page);

        Assert.Equal(("Storage", 12.5m, "NOK"), rows[0]);
        Assert.Equal("Ukjent", rows[1].Service);
    }

    [Fact]
    public void Teams_card_shows_cost_and_change()
    {
        var metrics = new OpsReportMetrics(DateTime.UtcNow,
            new OpsTicketMetrics(0, 0, new Dictionary<string, int>(), 0), new OpsSlaMetrics(0, 0, 0),
            new OpsAlertMetrics([], [], []), new OpsAccessMetrics(0, null), new OpsLifecycleMetrics(0, 0),
            new OpsM365Metrics(null, null),
            new OpsCostMetrics("NOK", 1173m, 1000m, 17.3m, [], 2, 0, null));
        var report = new OpsReport { Id = 1, PeriodStart = WeekStart, PeriodEnd = WeekEnd, AiStatus = OpsReportAiStatuses.Disabled };

        var card = Regex.Unescape(TeamsCards.ForOpsReport(report, metrics, "https://app/?driftsrapport=1"));

        Assert.Contains("Kostnad Azure", card);
        Assert.Contains("NOK (+17.3 %)", card);
    }
}

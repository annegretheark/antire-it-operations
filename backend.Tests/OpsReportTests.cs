using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using backend.Models;
using backend.Services;

namespace backend.Tests;

// Driftsrapporten, steg 1 (FASE5_DRIFTSRAPPORT.md kap. 8): nøkkeltall, tenant-skille,
// én rapport per uke og rapport uten AI. Hver test bruker egne tenant-ID-er, slik at
// data fra andre tester i samme database ikke påvirker tallene.
public class OpsReportTests(SqliteWebApplicationFactory factory)
    : IClassFixture<SqliteWebApplicationFactory>
{
    // Uken mandag 16. til søndag 22. november 2026 (norsk tid, UTC+1)
    private static readonly DateTime WeekStart = Utc(2026, 11, 15, 23);
    private static readonly DateTime WeekEnd = Utc(2026, 11, 22, 23);
    // Mandag 23. november kl. 08:00 norsk tid
    private static readonly DateTime Now = Utc(2026, 11, 23, 7);

    private static DateTime Utc(int y, int mo, int d, int h = 0, int mi = 0) =>
        new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    private static string NewTenant() => Guid.NewGuid().ToString();

    private async Task<(OpsReport Report, bool Created)> GenerateAsync(string tenantId)
    {
        using var scope = factory.Services.GetRequiredService<IServiceScopeFactory>().CreateTenantScope(tenantId);
        var service = scope.ServiceProvider.GetRequiredService<OpsReportService>();
        return await service.GenerateAsync(WeekStart, WeekEnd, Now, OpsReportService.SystemActor);
    }

    private static Alert NewAlert(string source, string severity, DateTime firstSeen, DateTime? lastFired = null) => new()
    {
        Source = source,
        ExternalId = Guid.NewGuid().ToString(),
        Title = "Varsel om disk.full på vm-hemmelig",
        Severity = severity,
        ResourceId = "/subscriptions/x/resourceGroups/rg/providers/vm-hemmelig",
        FirstSeenAt = firstSeen,
        LastSeenAt = lastFired ?? firstSeen,
        LastFiredAt = lastFired ?? firstSeen,
    };

    private static M365User Guest(string name, DateTimeOffset? lastSignIn, DateTimeOffset created, string type = "Guest") =>
        new(Guid.NewGuid().ToString(), name, $"{name}@ekstern.no", $"{name}@ekstern.no", true, type,
            null, null, created, [], lastSignIn, null, lastSignIn);

    [Fact]
    public void Previous_week_is_monday_to_monday_in_norwegian_time()
    {
        Assert.Equal((WeekStart, WeekEnd), OpsReportService.PreviousWeek(Now));

        // Mandag 00:00 norsk tid hører til den nye uken
        Assert.Equal((WeekStart, WeekEnd), OpsReportService.PreviousWeek(Utc(2026, 11, 22, 23)));

        // Søndag 23:30 norsk tid: uken før
        Assert.Equal((Utc(2026, 11, 8, 23), WeekStart), OpsReportService.PreviousWeek(Utc(2026, 11, 22, 22, 30)));
    }

    [Fact]
    public void Previous_week_handles_daylight_saving_changes()
    {
        // Sommertid fra søndag 29. mars 2026: uken er én time kortere
        Assert.Equal(
            (Utc(2026, 3, 22, 23), Utc(2026, 3, 29, 22)),
            OpsReportService.PreviousWeek(Utc(2026, 3, 30, 7)));

        // Vintertid fra søndag 25. oktober 2026: uken er én time lengre
        Assert.Equal(
            (Utc(2026, 10, 18, 22), Utc(2026, 10, 25, 23)),
            OpsReportService.PreviousWeek(Utc(2026, 10, 26, 8)));
    }

    [Fact]
    public async Task Metrics_are_computed_for_a_known_week_and_saved_without_ai()
    {
        var tenant = NewTenant();

        await using (var db = factory.CreateDbContext(tenant))
        {
            db.Tickets.AddRange(
                // 1: ny i perioden, åpen, høy, uten SLA
                new Ticket { Description = "Ola Hemmelig får ikke logget på", Priority = TicketPriorities.High,
                    CreatedAt = Utc(2026, 11, 17, 9) },
                // 2: eldre, løst i perioden
                new Ticket { Status = TicketStatuses.Resolved, CreatedAt = Utc(2026, 11, 1),
                    ResolvedAt = Utc(2026, 11, 18) },
                // 3: åpen, eldre enn 14 dager
                new Ticket { CreatedAt = Utc(2026, 11, 1) },
                // 4: åpen, kritisk, responsfrist gikk ut i perioden uten svar
                new Ticket { Priority = TicketPriorities.Critical, CreatedAt = Utc(2026, 11, 10),
                    ResponseDueAt = Utc(2026, 11, 17, 10), ResolutionDueAt = Utc(2026, 12, 1) },
                // 5: ny i perioden, lav, svar kom for sent
                new Ticket { Priority = TicketPriorities.Low, Status = TicketStatuses.InProgress,
                    CreatedAt = Utc(2026, 11, 16, 12), ResponseDueAt = Utc(2026, 11, 17, 8),
                    FirstRespondedAt = Utc(2026, 11, 17, 9), ResolutionDueAt = Utc(2026, 11, 30) },
                // 6: ny og løst i perioden innen begge frister
                new Ticket { Status = TicketStatuses.Resolved, CreatedAt = Utc(2026, 11, 18, 8),
                    ResponseDueAt = Utc(2026, 11, 18, 12), FirstRespondedAt = Utc(2026, 11, 18, 11),
                    ResolutionDueAt = Utc(2026, 11, 20), ResolvedAt = Utc(2026, 11, 19) },
                // 7: løst i perioden etter løsningsfristen
                new Ticket { Status = TicketStatuses.Resolved, CreatedAt = Utc(2026, 11, 12),
                    FirstRespondedAt = Utc(2026, 11, 12, 9), ResolutionDueAt = Utc(2026, 11, 19),
                    ResolvedAt = Utc(2026, 11, 20) },
                // 8: laget etter perioden, «i fare» nå (60 av 70 minutter brukt)
                new Ticket { CreatedAt = Utc(2026, 11, 23, 6), ResolutionDueAt = Utc(2026, 11, 23, 7, 10) });

            db.Alerts.AddRange(
                NewAlert(AlertSources.AzureMonitor, "Sev1", Utc(2026, 11, 17)),
                NewAlert(AlertSources.AzureMonitor, "Sev1", Utc(2026, 11, 18)),
                NewAlert(AlertSources.Defender, "high", Utc(2026, 11, 19)),
                // Fra før perioden, fyrte igjen i perioden
                NewAlert(AlertSources.AzureMonitor, "Sev2", Utc(2026, 10, 1), Utc(2026, 11, 20)),
                // Fra før perioden, fyrte ikke i perioden
                NewAlert(AlertSources.Defender, "medium", Utc(2026, 10, 1), Utc(2026, 10, 5)),
                // Etter perioden
                NewAlert(AlertSources.AzureMonitor, "Sev0", Utc(2026, 11, 23, 6)));

            db.AccessRequests.AddRange(
                new AccessRequest { EmployeeName = "Kari Hemmelig", SystemName = "Visma",
                    Status = AccessRequestStatuses.Pending, CreatedAt = Utc(2026, 11, 13, 7) },
                new AccessRequest { Status = AccessRequestStatuses.Pending, CreatedAt = Utc(2026, 11, 20) },
                new AccessRequest { Status = AccessRequestStatuses.Approved, CreatedAt = Utc(2026, 10, 1) });

            var employees = Enumerable.Range(0, 3)
                .Select(i => new Employee { FirstName = "Per", LastName = $"Hemmelig{i}",
                    Email = $"per.hemmelig{i}@firma.no" })
                .ToList();
            db.Employees.AddRange(employees);
            await db.SaveChangesAsync();

            db.LifecycleProcesses.AddRange(
                new LifecycleProcess { EmployeeId = employees[0].Id, Type = LifecycleTypes.Onboarding,
                    ReferenceDate = Utc(2026, 11, 30), CreatedAt = Now },
                new LifecycleProcess { EmployeeId = employees[1].Id, Type = LifecycleTypes.Onboarding,
                    ReferenceDate = Utc(2026, 12, 20), CreatedAt = Now },
                new LifecycleProcess { EmployeeId = employees[2].Id, Type = LifecycleTypes.Offboarding,
                    ReferenceDate = Utc(2026, 11, 25), CreatedAt = Now },
                new LifecycleProcess { EmployeeId = employees[0].Id, Type = LifecycleTypes.Offboarding,
                    Status = LifecycleStatuses.Completed, ReferenceDate = Utc(2026, 11, 26), CreatedAt = Now });
            await db.SaveChangesAsync();
        }

        var registry = factory.Services.GetRequiredService<AlertSourceStatusRegistry>();
        registry.Set(tenant, new AlertSourceStatus(AlertSources.AzureMonitor, Now, true, "OK", 3));
        registry.Set(tenant, new AlertSourceStatus(AlertSources.Defender, Now, false, "Mangler lisens", 0));

        factory.Graph.UserOverview = new M365UserOverview(
        [
            Guest("gjest.inaktiv", Utc(2026, 8, 1), Utc(2025, 1, 1)),
            Guest("gjest.aktiv", Utc(2026, 11, 20), Utc(2025, 1, 1)),
            Guest("gjest.aldri", null, Utc(2026, 1, 1)),
            Guest("medlem.inaktiv", Utc(2026, 1, 1), Utc(2025, 1, 1), type: "Member"),
        ], null, null, null, DateTimeOffset.UtcNow);

        var (report, created) = await GenerateAsync(tenant);

        Assert.True(created);
        Assert.Equal(WeekStart, report.PeriodStart);
        Assert.Equal(WeekEnd, report.PeriodEnd);
        Assert.Equal(OpsReportAiStatuses.Disabled, report.AiStatus);
        Assert.Null(report.AiSummary);
        Assert.Null(report.AiActions);
        Assert.Equal(OpsReportService.SystemActor, report.CreatedBy);

        var m = OpsReportService.ReadMetrics(report)!;

        Assert.Equal(3, m.Tickets.Created);
        Assert.Equal(3, m.Tickets.Resolved);
        Assert.Equal(1, m.Tickets.OpenByPriority[TicketPriorities.Low]);
        Assert.Equal(2, m.Tickets.OpenByPriority[TicketPriorities.Normal]);
        Assert.Equal(1, m.Tickets.OpenByPriority[TicketPriorities.High]);
        Assert.Equal(1, m.Tickets.OpenByPriority[TicketPriorities.Critical]);
        Assert.Equal(1, m.Tickets.OpenOlderThan14Days);

        Assert.Equal(2, m.Sla.ResponseBreaches);
        Assert.Equal(1, m.Sla.ResolutionBreaches);
        Assert.Equal(1, m.Sla.AtRisk);

        Assert.Equal(
            [new OpsAlertCount(AlertSources.AzureMonitor, "Sev1", 2), new OpsAlertCount(AlertSources.Defender, "high", 1)],
            m.Alerts.New);
        Assert.Equal([new OpsAlertCount(AlertSources.AzureMonitor, "Sev2", 1)], m.Alerts.Recurred);
        Assert.Equal([AlertSources.Defender], m.Alerts.FailingSources);

        Assert.Equal(2, m.Access.PendingApproval);
        Assert.Equal(10, m.Access.OldestPendingDays);

        Assert.Equal(1, m.Lifecycle.OnboardingNext14Days);
        Assert.Equal(1, m.Lifecycle.OffboardingNext14Days);

        Assert.Equal(2, m.M365.InactiveGuests);
        Assert.Null(m.M365.UnavailableReason);

        // Nøkkeltallene inneholder bare tall og kategorinavn
        foreach (var secret in new[] { "Hemmelig", "@", "vm-hemmelig", "gjest.", "Visma", "/subscriptions/" })
            Assert.DoesNotContain(secret, report.Metrics, StringComparison.OrdinalIgnoreCase);

        await using var check = factory.CreateDbContext(tenant);
        var saved = await check.OpsReports.SingleAsync();
        Assert.Equal(report.Id, saved.Id);
        Assert.Equal(tenant, saved.TenantId);
    }

    [Fact]
    public async Task Report_in_one_tenant_never_counts_data_from_another()
    {
        var tenantA = NewTenant();
        var tenantB = NewTenant();

        await using (var db = factory.CreateDbContext(tenantA))
        {
            db.Tickets.Add(new Ticket { CreatedAt = Utc(2026, 11, 17) });
            db.Alerts.Add(NewAlert(AlertSources.AzureMonitor, "Sev1", Utc(2026, 11, 17)));
            db.AccessRequests.Add(new AccessRequest { Status = AccessRequestStatuses.Pending, CreatedAt = Utc(2026, 11, 20) });
            await db.SaveChangesAsync();
        }

        await using (var db = factory.CreateDbContext(tenantB))
        {
            db.Tickets.AddRange(
                new Ticket { CreatedAt = Utc(2026, 11, 17) },
                new Ticket { CreatedAt = Utc(2026, 11, 18), Priority = TicketPriorities.Critical,
                    ResponseDueAt = Utc(2026, 11, 18, 10) });
            db.Alerts.AddRange(
                NewAlert(AlertSources.AzureMonitor, "Sev1", Utc(2026, 11, 17)),
                NewAlert(AlertSources.Defender, "high", Utc(2026, 11, 18)));
            db.AccessRequests.Add(new AccessRequest { Status = AccessRequestStatuses.Pending, CreatedAt = Utc(2026, 10, 1) });
            await db.SaveChangesAsync();
        }

        var registry = factory.Services.GetRequiredService<AlertSourceStatusRegistry>();
        registry.Set(tenantB, new AlertSourceStatus(AlertSources.Defender, Now, false, "Feil", 0));

        var (report, _) = await GenerateAsync(tenantA);
        var m = OpsReportService.ReadMetrics(report)!;

        Assert.Equal(1, m.Tickets.Created);
        Assert.Equal(1, m.Tickets.OpenByPriority.Values.Sum());
        Assert.Equal(0, m.Sla.ResponseBreaches);
        Assert.Equal([new OpsAlertCount(AlertSources.AzureMonitor, "Sev1", 1)], m.Alerts.New);
        Assert.Empty(m.Alerts.FailingSources);
        Assert.Equal(1, m.Access.PendingApproval);
        Assert.Equal(3, m.Access.OldestPendingDays);

        // Tenant B ser ikke rapporten til tenant A
        await using var other = factory.CreateDbContext(tenantB);
        Assert.Empty(await other.OpsReports.ToListAsync());
    }

    [Fact]
    public async Task Same_week_is_not_generated_twice()
    {
        var tenant = NewTenant();

        var (first, firstCreated) = await GenerateAsync(tenant);

        await using (var db = factory.CreateDbContext(tenant))
        {
            db.Tickets.Add(new Ticket { CreatedAt = Utc(2026, 11, 17) });
            await db.SaveChangesAsync();
        }

        // Nytt scope og ny tjeneste, som etter en omstart
        var (second, secondCreated) = await GenerateAsync(tenant);

        Assert.True(firstCreated);
        Assert.False(secondCreated);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Metrics, second.Metrics);

        await using var check = factory.CreateDbContext(tenant);
        Assert.Equal(1, await check.OpsReports.CountAsync());
    }

    [Fact]
    public async Task Report_is_saved_when_microsoft_365_is_unavailable()
    {
        var tenant = NewTenant();

        await using (var db = factory.CreateDbContext(tenant))
        {
            db.Tickets.Add(new Ticket { CreatedAt = Utc(2026, 11, 17) });
            await db.SaveChangesAsync();
        }

        factory.Graph.Failure = new InvalidOperationException("Graph er nede");
        try
        {
            var (report, created) = await GenerateAsync(tenant);
            var m = OpsReportService.ReadMetrics(report)!;

            Assert.True(created);
            Assert.Null(m.M365.InactiveGuests);
            Assert.NotNull(m.M365.UnavailableReason);
            Assert.Equal(1, m.Tickets.Created);
        }
        finally
        {
            factory.Graph.Failure = null;
        }
    }
}

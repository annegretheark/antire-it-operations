using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using backend.Controllers;
using backend.Models;
using backend.Services;

namespace backend.Tests;

// API-et for driftsrapporter (FASE5_DRIFTSRAPPORT.md, steg 2): lesing for agenter,
// generering for admin, tenant-skille og at en eksisterende rapport ikke overskrives.
// Tenant A brukes til generering, tenant B til listen.
public class OpsReportsControllerTests(SqliteWebApplicationFactory factory)
    : IClassFixture<SqliteWebApplicationFactory>
{
    private const string TenantA = TestAuthHandler.AllowedTenantId;
    private const string TenantB = TestAuthHandler.OtherAllowedTenantId;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Agent(string tenantId) => factory.CreateClientInTenant(tenantId, AppRoles.HelpdeskAgent);

    private HttpClient Admin(string tenantId) => factory.CreateClientInTenant(tenantId, AppRoles.HelpdeskAdmin);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    [Fact]
    public async Task Admin_generates_last_week_once_and_agent_can_read_it()
    {
        var first = await Admin(TenantA).PostAsync("/api/opsreports/generate", null);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var created = await ReadAsync<OpsReportDto>(first);

        var (start, end) = OpsReportService.PreviousWeek(DateTime.UtcNow);
        Assert.Equal(start, created.PeriodStart);
        Assert.Equal(end, created.PeriodEnd);
        Assert.Equal(OpsReportAiStatuses.Disabled, created.AiStatus);
        Assert.Equal("test-user", created.CreatedBy);
        Assert.NotNull(created.Metrics);

        // Andre gang: samme rapport, ikke overskrevet
        var second = await Admin(TenantA).PostAsync("/api/opsreports/generate", null);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(created.Id, (await ReadAsync<OpsReportDto>(second)).Id);

        await using (var db = factory.CreateDbContext(TenantA))
            Assert.Equal(1, await db.OpsReports.CountAsync(r => r.PeriodStart == start));

        // Agenten leser rapporten, med nøkkeltallene som objekt
        var response = await Agent(TenantA).GetAsync($"/api/opsreports/{created.Id}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        var metrics = doc.RootElement.GetProperty("metrics");
        Assert.Equal(JsonValueKind.Object, metrics.ValueKind);
        Assert.Equal(JsonValueKind.Number, metrics.GetProperty("tickets").GetProperty("created").ValueKind);
        Assert.Equal(JsonValueKind.Number,
            metrics.GetProperty("tickets").GetProperty("openByPriority").GetProperty("critical").ValueKind);

        // Rapporten finnes ikke i en annen tenant
        Assert.Equal(HttpStatusCode.NotFound, (await Agent(TenantB).GetAsync($"/api/opsreports/{created.Id}")).StatusCode);
    }

    [Fact]
    public async Task Agent_cannot_generate()
    {
        var response = await Agent(TenantA).PostAsync("/api/opsreports/generate", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task List_shows_the_newest_twelve_in_own_tenant()
    {
        // 14 uker i 2020 i tenant B (lagt inn direkte)
        var firstMonday = new DateTime(2020, 1, 5, 23, 0, 0, DateTimeKind.Utc);
        await using (var db = factory.CreateDbContext(TenantB))
        {
            for (var week = 0; week < 14; week++)
            {
                db.OpsReports.Add(new OpsReport
                {
                    PeriodStart = firstMonday.AddDays(7 * week),
                    PeriodEnd = firstMonday.AddDays(7 * (week + 1)),
                    CreatedAt = firstMonday.AddDays(7 * (week + 1)),
                    CreatedBy = OpsReportService.SystemActor,
                });
            }
            await db.SaveChangesAsync();
        }

        // En rapport i tenant A som ikke skal vises i B
        await using (var db = factory.CreateDbContext(TenantA))
        {
            db.OpsReports.Add(new OpsReport
            {
                PeriodStart = new DateTime(2019, 12, 29, 23, 0, 0, DateTimeKind.Utc),
                PeriodEnd = firstMonday,
                CreatedAt = firstMonday,
                CreatedBy = OpsReportService.SystemActor,
            });
            await db.SaveChangesAsync();
        }

        var list = await ReadAsync<List<OpsReportSummaryDto>>(await Agent(TenantB).GetAsync("/api/opsreports"));

        Assert.Equal(OpsReportsController.ListSize, list.Count);
        Assert.Equal(firstMonday.AddDays(7 * 13), list[0].PeriodStart);
        Assert.Equal(firstMonday.AddDays(7 * 2), list[^1].PeriodStart);
        Assert.Equal(list.OrderByDescending(r => r.PeriodStart).Select(r => r.Id), list.Select(r => r.Id));
    }
}

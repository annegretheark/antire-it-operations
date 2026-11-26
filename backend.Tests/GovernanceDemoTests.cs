using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using backend.Models;
using backend.Services;

namespace backend.Tests;

// API-et med demodata slått på for testtenant A (C9)
public class DemoWebApplicationFactory : SqliteWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Demo:Enabled", "true");
        builder.UseSetting("Demo:TenantId", TestAuthHandler.AllowedTenantId);
    }
}

// Syntetiske demodata, C9 (ENDRING_OG_RISIKO.md). Testene i klassen kjører etter hverandre.
public class GovernanceDemoTests(DemoWebApplicationFactory factory) : IClassFixture<DemoWebApplicationFactory>
{
    private const string TenantA = TestAuthHandler.AllowedTenantId;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] Aliases = ["test.requester", "test.changemanager", "test.systemowner", "test.implementer", "test.reviewer"];

    private HttpClient Admin() => factory.CreateClientWithRoles(AppRoles.HelpdeskAdmin);

    private async Task EnsureTestUsersAsync()
    {
        await using var db = factory.CreateDbContext(TenantA);
        foreach (var alias in Aliases)
        {
            if (!await db.Employees.AnyAsync(e => e.Email.StartsWith(alias + "@")))
            {
                db.Employees.Add(new Employee
                {
                    FirstName = "Test", LastName = alias, Email = $"{alias}@demo.invalid", EntraObjectId = Guid.NewGuid().ToString(),
                });
            }
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Seed_creates_changes_and_risks_through_the_workflow_once()
    {
        await EnsureTestUsersAsync();

        var first = await (await Admin().PostAsync("/api/governance/demo-data", null)).Content.ReadFromJsonAsync<DemoSeedResult>(Json);
        Assert.Equal(10, first!.ChangesCreated);
        Assert.Equal(10, first.RisksCreated);

        await using (var db = factory.CreateDbContext(TenantA))
        {
            var statuses = await db.ChangeRequests.Where(c => c.Title.StartsWith(GovernanceDemoSeeder.Prefix))
                .ToDictionaryAsync(c => c.Title[GovernanceDemoSeeder.Prefix.Length..], c => c.Status);

            Assert.Equal(ChangeStatuses.Draft, statuses["Oppgrader lastbalanserer i produksjon"]);
            Assert.Equal(ChangeStatuses.PendingApproval, statuses["Ny regel for passordpolicy"]);
            Assert.Equal(ChangeStatuses.Approved, statuses["Flytt logglagring til ny konto"]);
            Assert.Equal(ChangeStatuses.Scheduled, statuses["Oppdater operativsystem på appserver"]);
            Assert.Equal(ChangeStatuses.Implementation, statuses["Innfør ny overvåkingsregel"]);
            Assert.Equal(ChangeStatuses.Closed, statuses["Bytt DNS-leverandør for testdomene"]);
            Assert.Equal(ChangeStatuses.Rejected, statuses["Åpne ny port mot internett"]);
            Assert.Equal(ChangeStatuses.RolledBack, statuses["Oppgrader biblioteker i API-et"]);
            Assert.Equal(ChangeStatuses.Completed, statuses["Steng sårbar tjeneste umiddelbart"]);
            Assert.Equal(ChangeStatuses.Scheduled, statuses["Månedlig patching av testservere"]);

            var riskStatuses = await db.Risks.Where(r => r.Title.StartsWith(GovernanceDemoSeeder.Prefix))
                .Select(r => r.Status).ToListAsync();
            Assert.Contains(RiskStatuses.Accepted, riskStatuses);
            Assert.Contains(RiskStatuses.Monitoring, riskStatuses);
            Assert.Contains(RiskStatuses.Closed, riskStatuses);

            // Ekte historikk: godkjenninger og statusendringer med testbrukerne som aktører
            Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == AuditActions.ChangeApproved && e.ActorName.Contains("test.systemowner")));
            Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == AuditActions.RiskAccepted && e.ActorName.Contains("test.changemanager")));
            Assert.True(await db.ChangeApprovals.AnyAsync());
        }

        var second = await (await Admin().PostAsync("/api/governance/demo-data", null)).Content.ReadFromJsonAsync<DemoSeedResult>(Json);
        Assert.Equal(0, second!.ChangesCreated);
        Assert.Equal(0, second.RisksCreated);
        Assert.Equal(20, second.Skipped.Count);
    }

    [Fact]
    public async Task Remove_only_touches_demo_rows()
    {
        await EnsureTestUsersAsync();
        await Admin().PostAsync("/api/governance/demo-data", null);

        int realId;
        await using (var db = factory.CreateDbContext(TenantA))
        {
            var real = new ChangeRequest
            {
                Number = $"CHG-R{Random.Shared.Next(100000, 999999)}", Title = "Ekte endring", Description = "x",
                RequestedByObjectId = "x", RequestedByName = "x", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.ChangeRequests.Add(real);
            await db.SaveChangesAsync();
            realId = real.Id;
        }

        var result = await (await Admin().DeleteAsync("/api/governance/demo-data")).Content.ReadFromJsonAsync<DemoRemoveResult>(Json);
        Assert.True(result!.Deleted > 0);

        await using var check = factory.CreateDbContext(TenantA);
        Assert.True(await check.ChangeRequests.AnyAsync(c => c.Id == realId));

        // Det som ikke kunne slettes (har godkjenninger eller aksept), er avsluttet
        var demoChanges = await check.ChangeRequests.Where(c => c.Title.StartsWith(GovernanceDemoSeeder.Prefix)).ToListAsync();
        Assert.All(demoChanges, c => Assert.Contains(c.Status, new[] { ChangeStatuses.Closed, ChangeStatuses.Cancelled, ChangeStatuses.Rejected }));
        Assert.All(await check.Risks.Where(r => r.Title.StartsWith(GovernanceDemoSeeder.Prefix)).ToListAsync(),
            r => Assert.Equal(RiskStatuses.Closed, r.Status));
        Assert.True(await check.ChangeApprovals.AnyAsync()); // beslutninger slettes aldri
    }

    [Fact]
    public async Task Only_helpdesk_admin_in_the_demo_tenant()
    {
        Assert.Equal(HttpStatusCode.Forbidden,
            (await factory.CreateClientWithRoles(AppRoles.HelpdeskAgent).PostAsync("/api/governance/demo-data", null)).StatusCode);

        var otherTenant = factory.CreateClientInTenant(TestAuthHandler.OtherAllowedTenantId, AppRoles.HelpdeskAdmin);
        Assert.Equal(HttpStatusCode.NotFound, (await otherTenant.PostAsync("/api/governance/demo-data", null)).StatusCode);
    }
}

// Uten Demo:Enabled (standard) finnes ikke demodata
public class GovernanceDemoDisabledTests(SqliteWebApplicationFactory factory) : IClassFixture<SqliteWebApplicationFactory>
{
    [Fact]
    public async Task Demo_data_is_off_by_default()
    {
        var admin = factory.CreateClientWithRoles(AppRoles.HelpdeskAdmin);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/governance/demo-data")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/governance/demo-data", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync("/api/governance/demo-data")).StatusCode);
    }
}

// Uten testbrukerne sier endepunktet hvilke som mangler
public class GovernanceDemoMissingUsersTests(DemoWebApplicationFactory factory) : IClassFixture<DemoWebApplicationFactory>
{
    [Fact]
    public async Task Missing_test_users_are_named()
    {
        var response = await factory.CreateClientWithRoles(AppRoles.HelpdeskAdmin).PostAsync("/api/governance/demo-data", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("test.requester", await response.Content.ReadAsStringAsync());
    }
}

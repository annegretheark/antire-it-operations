using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using backend.Controllers;
using backend.Models;
using backend.Services;

namespace backend.Tests;

// Fase 3: abonnementer og ressurser med falsk Azure
public class AzureTests(SqliteWebApplicationFactory factory)
    : IClassFixture<SqliteWebApplicationFactory>
{
    private const string TenantA = TestAuthHandler.AllowedTenantId;
    private const string TenantB = TestAuthHandler.OtherAllowedTenantId;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Admin(string tenantId = TenantA) =>
        factory.CreateClientInTenant(tenantId, AppRoles.HelpdeskAdmin);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Json))!;

    private static string Sub() => Guid.NewGuid().ToString();

    private async Task<AzureSyncResult> SyncAsync(string tenantId = TenantA)
    {
        var response = await Admin(tenantId).PostAsync("/api/azure/subscriptions/sync", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<AzureSyncResult>(response);
    }

    private static AzureResource Resource(
        string subscriptionId, string name, string type, string location, string group,
        Dictionary<string, string>? tags = null) =>
        new($"/subscriptions/{subscriptionId}/resourceGroups/{group}/providers/{type}/{name}",
            name, type, location, group, subscriptionId, tags ?? []);

    [Fact]
    public async Task Sync_adds_updates_and_hides_subscriptions()
    {
        // Tenant B brukes bare i denne testen
        var (keep, drop) = (Sub(), Sub());
        factory.Azure.Subscriptions[TenantB] =
        [
            new(keep.ToUpperInvariant(), "Aitrening", "Enabled"),
            new(drop, "Gammelt", "Enabled"),
        ];

        Assert.Equal(new AzureSyncResult(2, 0, 0, 2), await SyncAsync(TenantB));

        factory.Azure.Subscriptions[TenantB] = [new(keep, "Aitrening (omdøpt)", "Warned")];
        Assert.Equal(new AzureSyncResult(0, 1, 1, 1), await SyncAsync(TenantB));

        var list = await ReadAsync<List<AzureSubscription>>(
            await Admin(TenantB).GetAsync("/api/azure/subscriptions"));
        var kept = Assert.Single(list, s => s.SubscriptionId == keep);
        Assert.Equal("Aitrening (omdøpt)", kept.DisplayName);
        Assert.Equal("Warned", kept.State);
        Assert.True(kept.IsVisible);
        Assert.False(Assert.Single(list, s => s.SubscriptionId == drop).IsVisible);

        // Ikke synlig fra en annen tenant
        var inA = await ReadAsync<List<AzureSubscription>>(await Admin().GetAsync("/api/azure/subscriptions"));
        Assert.DoesNotContain(inA, s => s.SubscriptionId == keep);

        await using var db = factory.CreateDbContext(TenantB);
        Assert.Equal(2, await db.AuditEvents.CountAsync(e => e.EntityType == "AzureSubscriptionSync"));
    }

    [Fact]
    public async Task Missing_reader_role_gives_readable_error()
    {
        factory.Azure.Failure = new AzureUnavailableException(
            "Appen har ikke tilgang i Azure. Gi appen rollen Reader på abonnementene.", 403);

        try
        {
            var response = await Admin().PostAsync("/api/azure/subscriptions/sync", null);

            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            var body = await ReadAsync<JsonElement>(response);
            Assert.Contains("Reader", body.GetProperty("message").GetString());
        }
        finally
        {
            factory.Azure.Failure = null;
        }
    }

    [Fact]
    public async Task Resources_are_listed_filtered_and_cached()
    {
        var (visible, hidden) = (Sub(), Sub());
        factory.Azure.Subscriptions[TenantA] = [new(visible, "Prod", "Enabled"), new(hidden, "Borte", "Enabled")];
        await SyncAsync();
        factory.Azure.Subscriptions[TenantA] = [new(visible, "Prod", "Enabled")];
        await SyncAsync();

        factory.Azure.Resources.AddRange(
        [
            Resource(visible, "vm-web", "microsoft.compute/virtualmachines", "norwayeast", "rg-web",
                new() { ["miljø"] = "prod" }),
            Resource(visible, "stweb", "microsoft.storage/storageaccounts", "norwayeast", "rg-web"),
            Resource(visible, "kv-ops", "microsoft.keyvault/vaults", "westeurope", "rg-ops"),
            Resource(hidden, "vm-borte", "microsoft.compute/virtualmachines", "norwayeast", "rg-x"),
        ]);

        var before = factory.Azure.ResourceQueries;
        var all = await ReadAsync<AzureResourceList>(await Admin().GetAsync("/api/azure/resources?refresh=true"));

        // Bare synlige abonnementer spørres
        Assert.Equal([visible], factory.Azure.LastQueriedSubscriptions);
        Assert.Equal(3, all.Total);
        Assert.Equal(["norwayeast", "westeurope"], all.Locations);
        Assert.Equal(["rg-ops", "rg-web"], all.ResourceGroups);
        Assert.Null(all.Message);

        var vms = await ReadAsync<AzureResourceList>(await Admin().GetAsync(
            "/api/azure/resources?type=microsoft.compute/virtualmachines"));
        Assert.Equal("vm-web", Assert.Single(vms.Resources).Name);

        var byTag = await ReadAsync<AzureResourceList>(await Admin().GetAsync("/api/azure/resources?search=PROD"));
        Assert.Equal("vm-web", Assert.Single(byTag.Resources).Name);

        var byGroupAndLocation = await ReadAsync<AzureResourceList>(await Admin().GetAsync(
            "/api/azure/resources?resourceGroup=rg-web&location=norwayeast"));
        Assert.Equal(2, byGroupAndLocation.Resources.Count);

        // Filtrene over ble besvart fra cachen
        Assert.Equal(before + 1, factory.Azure.ResourceQueries);
    }

    [Fact]
    public async Task No_synced_subscriptions_gives_guidance()
    {
        // Tenant B uten synlige abonnementer (egen fabrikk i hver testklasse,
        // men denne testen kan kjøre før eller etter synk-testen)
        await using (var db = factory.CreateDbContext(TenantB))
        {
            foreach (var s in await db.AzureSubscriptions.ToListAsync())
                s.IsVisible = false;
            await db.SaveChangesAsync();
        }

        var result = await ReadAsync<AzureResourceList>(await Admin(TenantB).GetAsync("/api/azure/resources"));

        Assert.Empty(result.Resources);
        Assert.Contains("Reader", result.Message);
    }
}

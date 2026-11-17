using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using backend.Models;
using backend.Services;

namespace backend.Tests;

// Tjenestekatalogen, saker og tilgangsbestillinger fra katalogen, og ansvarsområder
public class ServiceCatalogTests(SqliteWebApplicationFactory factory)
    : IClassFixture<SqliteWebApplicationFactory>
{
    private const string TenantA = TestAuthHandler.AllowedTenantId;
    private const string TenantB = TestAuthHandler.OtherAllowedTenantId;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Admin(string tenantId = TenantA) =>
        factory.CreateClientInTenant(tenantId, AppRoles.HelpdeskAdmin);

    private HttpClient User(string tenantId = TenantA) =>
        factory.CreateClientInTenant(tenantId);

    private static string Unique(string prefix) => $"{prefix} {Guid.NewGuid():N}"[..30];

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Json))!;

    private static async Task<List<ServiceCatalogItem>> GetCatalogAsync(
        HttpClient client, string query = "")
    {
        var response = await client.GetAsync($"/api/servicecatalog{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<List<ServiceCatalogItem>>(response);
    }

    private async Task<ServiceCatalogItem> CreateItemAsync(object request)
    {
        var response = await Admin().PostAsJsonAsync("/api/servicecatalog", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<ServiceCatalogItem>(response);
    }

    [Fact]
    public async Task New_tenant_gets_default_catalog_once()
    {
        // Tenant B brukes bare her, så katalogen er tom før første henting
        var first = await GetCatalogAsync(User(TenantB));
        var second = await GetCatalogAsync(User(TenantB));

        var expected = ServiceCatalogService.DefaultItems().Select(i => i.Name).Order();
        Assert.Equal(expected, first.Select(i => i.Name).Order());
        Assert.Equal(first.Select(i => i.Id).Order(), second.Select(i => i.Id).Order());

        Assert.Contains(first, i => i.Name == "Feil" && i.Type == ServiceCatalogTypes.Incident);
        Assert.Equal(5, first.Count(i => i.Type == ServiceCatalogTypes.Access));
        Assert.All(first, i => Assert.Equal(TenantB, i.TenantId));
    }

    [Fact]
    public async Task Type_filter_returns_only_that_type()
    {
        var access = await GetCatalogAsync(User(), "?type=access");

        Assert.NotEmpty(access);
        Assert.All(access, i => Assert.Equal(ServiceCatalogTypes.Access, i.Type));
    }

    [Fact]
    public async Task Inactive_items_are_hidden_except_for_admin_asking_for_them()
    {
        var item = await CreateItemAsync(new
        {
            name = Unique("Gammel tjeneste"), category = "Annet", isActive = false,
        });

        Assert.DoesNotContain(await GetCatalogAsync(User()), i => i.Id == item.Id);
        Assert.DoesNotContain(await GetCatalogAsync(User(), "?includeInactive=true"), i => i.Id == item.Id);
        Assert.DoesNotContain(await GetCatalogAsync(Admin()), i => i.Id == item.Id);
        Assert.Contains(await GetCatalogAsync(Admin(), "?includeInactive=true"), i => i.Id == item.Id);
    }

    [Fact]
    public async Task Admin_can_create_and_update_item()
    {
        var groupId = Guid.NewGuid().ToString();
        var item = await CreateItemAsync(new
        {
            name = Unique("NetSuite"),
            category = "Tilgangsbestilling",
            type = "access",
            defaultPriority = "high",
            requiresApproval = true,
            entraGroupId = groupId.ToUpperInvariant(),
        });

        Assert.Equal(groupId, item.EntraGroupId);
        Assert.Equal("high", item.DefaultPriority);

        var response = await Admin().PutAsJsonAsync($"/api/servicecatalog/{item.Id}", new
        {
            name = item.Name,
            category = "Tilgangsbestilling",
            description = "Økonomisystemet",
            type = "access",
            defaultPriority = "normal",
            requiresApproval = false,
            isActive = true,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await ReadAsync<ServiceCatalogItem>(response);
        Assert.Equal("Økonomisystemet", updated.Description);
        Assert.Null(updated.EntraGroupId);
        Assert.False(updated.RequiresApproval);
    }

    [Theory]
    [InlineData("", "Feil", "incident", "normal", null)]
    [InlineData("Navn", "", "incident", "normal", null)]
    [InlineData("Navn", "Feil", "ukjent", "normal", null)]
    [InlineData("Navn", "Feil", "incident", "Middels", null)]
    [InlineData("Navn", "Feil", "incident", "normal", "6b3c1f2e-0000-0000-0000-000000000001")]
    [InlineData("Navn", "Feil", "access", "normal", "ikke-en-guid")]
    public async Task Invalid_item_is_rejected(
        string name, string category, string type, string priority, string? groupId)
    {
        var response = await Admin().PostAsJsonAsync("/api/servicecatalog", new
        {
            name, category, type, defaultPriority = priority, entraGroupId = groupId,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Duplicate_name_is_rejected()
    {
        await GetCatalogAsync(User()); // sørger for startinnholdet

        var response = await Admin().PostAsJsonAsync("/api/servicecatalog", new
        {
            name = "Feil", category = "Feil",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Ticket_from_catalog_gets_category_priority_and_automatic_assignee()
    {
        var category = Unique("Nettverk");
        var item = await CreateItemAsync(new
        {
            name = Unique("VPN virker ikke"), category, type = "incident", defaultPriority = "high",
        });

        // Ansvarlig: aktiv ansatt med ansvarsområdet, medlem av kategorigruppen
        var objectId = Guid.NewGuid().ToString();
        int employeeId;
        await using (var db = factory.CreateDbContext(TenantA))
        {
            var employee = new Employee
            {
                FirstName = "Nina", LastName = "Nett", Email = $"{objectId}@test.no", EntraObjectId = objectId,
            };
            db.Employees.Add(employee);
            await db.SaveChangesAsync();
            employeeId = employee.Id;

            db.TicketCategoryGroups.Add(new TicketCategoryGroup
            {
                Category = category, EntraGroupId = Guid.NewGuid().ToString(),
            });
            await db.SaveChangesAsync();
        }

        var responsibility = await Admin().PostAsJsonAsync("/api/employeeresponsibilities", new
        {
            employeeId, category,
        });
        Assert.Equal(HttpStatusCode.Created, responsibility.StatusCode);

        factory.GroupMembers.Add(objectId);

        var response = await User().PostAsJsonAsync("/api/tickets", new
        {
            serviceCatalogItemId = item.Id, category = "Ignoreres", description = "Får ikke koblet til",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var ticket = await ReadAsync<Ticket>(response);
        Assert.Equal(category, ticket.Category);
        Assert.Equal("high", ticket.Priority);
        Assert.Equal(item.Id, ticket.ServiceCatalogItemId);
        Assert.Equal(employeeId, ticket.AssignedToEmployeeId);
    }

    [Fact]
    public async Task Ticket_with_category_only_still_works()
    {
        var response = await User().PostAsJsonAsync("/api/tickets", new
        {
            category = "Feil", description = "Uten katalog",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var ticket = await ReadAsync<Ticket>(response);
        Assert.Equal("Feil", ticket.Category);
        Assert.Equal("normal", ticket.Priority);
        Assert.Null(ticket.ServiceCatalogItemId);
    }

    [Fact]
    public async Task Ticket_cannot_use_inactive_or_other_tenants_item()
    {
        var inactive = await CreateItemAsync(new
        {
            name = Unique("Avviklet"), category = "Annet", isActive = false,
        });
        var otherTenantItem = (await GetCatalogAsync(User(TenantB))).First();

        foreach (var itemId in new[] { inactive.Id, otherTenantItem.Id })
        {
            var response = await User().PostAsJsonAsync("/api/tickets", new
            {
                serviceCatalogItemId = itemId, description = "Test",
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task Access_request_takes_system_name_from_catalog()
    {
        var system = (await GetCatalogAsync(User(), "?type=access")).First();

        var response = await User().PostAsJsonAsync("/api/accessrequests", new
        {
            employeeName = "Kari",
            serviceCatalogItemId = system.Id,
            systemName = "Fritekst ignoreres",
            reason = "Ny i teamet",
            managerName = "Per",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var request = await ReadAsync<AccessRequest>(response);
        Assert.Equal(system.Name, request.SystemName);
        Assert.Equal(system.Id, request.ServiceCatalogItemId);
    }

    [Fact]
    public async Task Access_request_requires_access_item_from_catalog()
    {
        var incident = (await GetCatalogAsync(User(), "?type=incident")).First();

        foreach (int? itemId in new int?[] { null, incident.Id })
        {
            var response = await User().PostAsJsonAsync("/api/accessrequests", new
            {
                employeeName = "Kari",
                serviceCatalogItemId = itemId,
                systemName = "Fritekst",
                reason = "Ny i teamet",
                managerName = "Per",
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task Responsibilities_can_be_listed_changed_and_removed()
    {
        int activeId, terminatedId;
        await using (var db = factory.CreateDbContext(TenantA))
        {
            var active = new Employee { FirstName = "Ali", LastName = "Aktiv", Email = $"{Guid.NewGuid():N}@t.no" };
            var terminated = new Employee
            {
                FirstName = "Siri", LastName = "Sluttet", Email = $"{Guid.NewGuid():N}@t.no", Status = "Sluttet",
            };
            db.Employees.AddRange(active, terminated);
            await db.SaveChangesAsync();
            (activeId, terminatedId) = (active.Id, terminated.Id);
        }

        var admin = Admin();
        var category = Unique("Utstyr");

        var created = await admin.PostAsJsonAsync("/api/employeeresponsibilities", new
        {
            employeeId = activeId, category,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync(
            "/api/employeeresponsibilities", new { employeeId = activeId, category })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(
            "/api/employeeresponsibilities", new { employeeId = terminatedId, category })).StatusCode);

        var list = await ReadAsync<List<JsonElement>>(await admin.GetAsync("/api/employeeresponsibilities"));
        var row = list.Single(r => r.GetProperty("category").GetString() == category);
        Assert.Equal("Ali Aktiv", row.GetProperty("employeeName").GetString());
        Assert.True(row.GetProperty("canBeAssigned").GetBoolean());

        var id = row.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PatchAsJsonAsync(
            $"/api/employeeresponsibilities/{id}", new { canBeAssigned = false })).StatusCode);

        await using (var db = factory.CreateDbContext(TenantA))
            Assert.False((await db.EmployeeResponsibilities.SingleAsync(r => r.Id == id)).CanBeAssigned);

        // En annen tenant finner ikke raden
        Assert.Equal(HttpStatusCode.NotFound,
            (await Admin(TenantB).DeleteAsync($"/api/employeeresponsibilities/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.DeleteAsync($"/api/employeeresponsibilities/{id}")).StatusCode);
    }
}

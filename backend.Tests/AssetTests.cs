using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using backend.Controllers;
using backend.Models;
using backend.Services;

namespace backend.Tests;

// IT-utstyr gjennom API-et: registrering, tildeling, innlevering, saker og CSV-import
public class AssetTests(SqliteWebApplicationFactory factory)
    : IClassFixture<SqliteWebApplicationFactory>
{
    private const string TenantA = TestAuthHandler.AllowedTenantId;
    private const string TenantB = TestAuthHandler.OtherAllowedTenantId;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Agent(string tenantId = TenantA) =>
        factory.CreateClientInTenant(tenantId, AppRoles.HelpdeskAgent);

    private HttpClient User() => factory.CreateClientWithRoles();

    private static string Tag() => $"PC-{Guid.NewGuid():N}"[..14];

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Json))!;

    private async Task<Employee> CreateEmployeeAsync(
        string? objectId = null, string status = "Aktiv", string tenantId = TenantA)
    {
        await using var db = factory.CreateDbContext(tenantId);
        var employee = new Employee
        {
            FirstName = "Ola",
            LastName = "Utstyr",
            Email = $"{Guid.NewGuid():N}@test.no",
            EntraObjectId = objectId,
            Status = status,
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return employee;
    }

    // Den innloggede testbrukeren som ansatt (én per tenant)
    private async Task<int> MeAsync()
    {
        await using (var db = factory.CreateDbContext(TenantA))
        {
            var id = await db.Employees
                .Where(e => e.EntraObjectId == TestAuthHandler.TestObjectId)
                .Select(e => (int?)e.Id)
                .FirstOrDefaultAsync();

            if (id != null)
                return id.Value;
        }

        return (await CreateEmployeeAsync(TestAuthHandler.TestObjectId)).Id;
    }

    private async Task<AssetDto> CreateAssetAsync(object body, string tenantId = TenantA)
    {
        var response = await Agent(tenantId).PostAsJsonAsync("/api/assets", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<AssetDto>(response);
    }

    [Fact]
    public async Task Asset_can_be_registered_assigned_and_returned()
    {
        var employee = await CreateEmployeeAsync();
        var asset = await CreateAssetAsync(new
        {
            assetTag = Tag(), type = "laptop", manufacturer = "Lenovo", model = "T14",
            purchaseDate = "2026-01-15", warrantyEndsAt = "2029-01-15",
        });

        Assert.Equal(AssetStatuses.InStock, asset.Status);
        Assert.Equal(AssetSources.Manual, asset.Source);

        var assigned = await ReadAsync<AssetDto>(await Agent().PatchAsJsonAsync(
            $"/api/assets/{asset.Id}/assign", new { employeeId = employee.Id }));
        Assert.Equal(employee.Id, assigned.AssignedToEmployeeId);
        Assert.Equal("Ola Utstyr", assigned.AssignedToName);
        Assert.Equal(AssetStatuses.InUse, assigned.Status);

        // Vises på den ansatte
        var onEmployee = await ReadAsync<List<AssetDto>>(
            await Agent().GetAsync($"/api/assets?employeeId={employee.Id}"));
        Assert.Equal(asset.Id, Assert.Single(onEmployee).Id);

        var returned = await ReadAsync<AssetDto>(await Agent().PatchAsJsonAsync(
            $"/api/assets/{asset.Id}/assign", new { employeeId = (int?)null }));
        Assert.Null(returned.AssignedToEmployeeId);
        Assert.Equal(AssetStatuses.InStock, returned.Status);
    }

    [Fact]
    public async Task Invalid_assets_are_rejected()
    {
        var tag = Tag();
        await CreateAssetAsync(new { assetTag = tag, type = "monitor" });

        var duplicate = await Agent().PostAsJsonAsync("/api/assets", new { assetTag = tag.ToLower(), type = "monitor" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var badType = await Agent().PostAsJsonAsync("/api/assets", new { assetTag = Tag(), type = "Bærbar" });
        Assert.Equal(HttpStatusCode.BadRequest, badType.StatusCode);

        var badDates = await Agent().PostAsJsonAsync("/api/assets", new
        {
            assetTag = Tag(), type = "laptop", purchaseDate = "2026-05-01", warrantyEndsAt = "2025-05-01",
        });
        Assert.Equal(HttpStatusCode.BadRequest, badDates.StatusCode);

        var terminated = await CreateEmployeeAsync(status: "Sluttet");
        var toTerminated = await Agent().PostAsJsonAsync("/api/assets", new
        {
            assetTag = Tag(), type = "laptop", assignedToEmployeeId = terminated.Id,
        });
        Assert.Equal(HttpStatusCode.BadRequest, toTerminated.StatusCode);

        var active = await CreateEmployeeAsync();
        var retiredButAssigned = await Agent().PostAsJsonAsync("/api/assets", new
        {
            assetTag = Tag(), type = "laptop", status = "retired", assignedToEmployeeId = active.Id,
        });
        Assert.Equal(HttpStatusCode.BadRequest, retiredButAssigned.StatusCode);
    }

    [Fact]
    public async Task User_sees_only_own_assets_and_can_report_ticket_on_them()
    {
        var me = await MeAsync();
        var other = await CreateEmployeeAsync();
        var mine = await CreateAssetAsync(new { assetTag = Tag(), type = "laptop", assignedToEmployeeId = me });
        var theirs = await CreateAssetAsync(new { assetTag = Tag(), type = "laptop", assignedToEmployeeId = other.Id });

        var visible = await ReadAsync<List<AssetDto>>(await User().GetAsync("/api/assets"));
        Assert.Contains(visible, a => a.Id == mine.Id);
        Assert.DoesNotContain(visible, a => a.Id == theirs.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await User().GetAsync($"/api/assets/{theirs.Id}")).StatusCode);

        var ownTicket = await User().PostAsJsonAsync("/api/tickets", new
        {
            category = "Feil", description = "Tastaturet virker ikke", assetId = mine.Id,
        });
        Assert.Equal(HttpStatusCode.Created, ownTicket.StatusCode);
        var ticket = await ReadAsync<Ticket>(ownTicket);
        Assert.Equal(mine.Id, ticket.AssetId);

        var othersTicket = await User().PostAsJsonAsync("/api/tickets", new
        {
            category = "Feil", description = "Ikke min", assetId = theirs.Id,
        });
        Assert.Equal(HttpStatusCode.BadRequest, othersTicket.StatusCode);

        // Saken vises på utstyret
        var details = await ReadAsync<AssetDetailsDto>(await User().GetAsync($"/api/assets/{mine.Id}"));
        Assert.Equal(ticket.Id, Assert.Single(details.Tickets).Id);
    }

    [Fact]
    public async Task Agent_can_report_ticket_on_any_asset()
    {
        var asset = await CreateAssetAsync(new { assetTag = Tag(), type = "monitor" });

        var response = await Agent().PostAsJsonAsync("/api/tickets", new
        {
            category = "Feil", description = "Skjermen flimrer", assetId = asset.Id,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Assets_are_separated_per_tenant()
    {
        var assetB = await CreateAssetAsync(new { assetTag = Tag(), type = "mobile" }, TenantB);

        var inA = await ReadAsync<List<AssetDto>>(await Agent().GetAsync("/api/assets"));
        Assert.DoesNotContain(inA, a => a.Id == assetB.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await Agent().GetAsync($"/api/assets/{assetB.Id}")).StatusCode);

        var ticket = await Agent().PostAsJsonAsync("/api/tickets", new
        {
            category = "Feil", description = "Feil tenant", assetId = assetB.Id,
        });
        Assert.Equal(HttpStatusCode.BadRequest, ticket.StatusCode);
    }

    [Fact]
    public async Task Csv_import_creates_and_updates_with_norwegian_headers()
    {
        var employee = await CreateEmployeeAsync();
        var (tag1, tag2) = (Tag(), Tag());

        var csv =
            "﻿Utstyrsnummer;Type;Produsent;Modell;Serienummer;Status;Ansatt;Kjøpsdato;Garanti til;Notater\r\n" +
            $"{tag1};Bærbar PC;Dell;Latitude 5440;SN1;;{employee.Email};15.01.2026;2029-01-15;\"Lader; ekstra\"\r\n" +
            $"{tag2};monitor;LG;27UL500;SN2;På lager;;;;\r\n";

        var first = await Agent().PostAsJsonAsync("/api/assets/import", new { csv });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var result = await ReadAsync<AssetImportResult>(first);
        Assert.Equal(2, result.Created);
        Assert.Equal(0, result.Updated);

        var assets = await ReadAsync<List<AssetDto>>(await Agent().GetAsync($"/api/assets?search={tag1}"));
        var laptop = Assert.Single(assets);
        Assert.Equal(AssetTypes.Laptop, laptop.Type);
        Assert.Equal(AssetStatuses.InUse, laptop.Status);
        Assert.Equal(employee.Id, laptop.AssignedToEmployeeId);
        Assert.Equal(new DateTime(2026, 1, 15), laptop.PurchaseDate!.Value.Date);
        Assert.Equal("Lader; ekstra", laptop.Notes);

        // Ny import med færre kolonner oppdaterer bare dem
        var update = $"AssetTag,Status\n{tag2},repair\n";
        var second = await ReadAsync<AssetImportResult>(
            await Agent().PostAsJsonAsync("/api/assets/import", new { csv = update }));
        Assert.Equal(0, second.Created);
        Assert.Equal(1, second.Updated);

        var monitor = Assert.Single(await ReadAsync<List<AssetDto>>(
            await Agent().GetAsync($"/api/assets?search={tag2}")));
        Assert.Equal(AssetStatuses.Repair, monitor.Status);
        Assert.Equal("LG", monitor.Manufacturer);
    }

    [Fact]
    public async Task Csv_import_with_errors_saves_nothing()
    {
        var (good, bad) = (Tag(), Tag());
        var csv =
            "AssetTag;Type;AssignedToEmail;PurchaseDate\n" +
            $"{good};laptop;;2026-01-01\n" +
            $"{bad};nettbrett;ukjent@test.no;31.02.2026\n";

        var response = await Agent().PostAsJsonAsync("/api/assets/import", new { csv });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var result = await ReadAsync<AssetImportResult>(response);
        var error = Assert.Single(result.Errors);
        Assert.StartsWith("Rad 3:", error);
        Assert.Contains("nettbrett", error);
        Assert.Contains("ukjent@test.no", error);
        Assert.Contains("31.02.2026", error);

        await using var db = factory.CreateDbContext(TenantA);
        Assert.False(await db.Assets.AnyAsync(a => a.AssetTag == good));
    }

    [Theory]
    [InlineData("AssetTag;Farge\nPC-1;rød\n", "Ukjente kolonner: Farge")]
    [InlineData("Type\nlaptop\n", "Utstyrsnummer")]
    [InlineData("AssetTag\n", "ingen rader")]
    public async Task Csv_import_rejects_bad_files(string csv, string expected)
    {
        var response = await Agent().PostAsJsonAsync("/api/assets/import", new { csv });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, Assert.Single((await ReadAsync<AssetImportResult>(response)).Errors));
    }
}

public class CsvReaderTests
{
    [Fact]
    public void Reads_semicolon_file_with_quotes_bom_and_crlf()
    {
        var rows = CsvReader.Read("﻿A;B;C\r\n1;\"to; tre\";\"si \"\"hei\"\"\"\r\n\r\n4;5\r\n");

        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows[0].Row);
        Assert.Equal("to; tre", rows[0].Values["B"]);
        Assert.Equal("si \"hei\"", rows[0].Values["c"]);
        Assert.Equal(4, rows[1].Row);
        Assert.Equal("", rows[1].Values["C"]);
    }

    [Fact]
    public void Detects_comma_and_keeps_newlines_in_quoted_fields()
    {
        var rows = CsvReader.Read("Navn,Notat\nPC,\"linje 1\nlinje 2\"\n");

        Assert.Equal("linje 1\nlinje 2", Assert.Single(rows).Values["Notat"]);
    }
}

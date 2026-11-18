using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using backend.Controllers;
using backend.Models;
using backend.Services;

namespace backend.Tests;

// GDPR: innsyn, anonymisering (manuelt og automatisk) og behandlingsprotokoll
public class GdprTests(SqliteWebApplicationFactory factory)
    : IClassFixture<SqliteWebApplicationFactory>
{
    private const string TenantA = TestAuthHandler.AllowedTenantId;
    private const string TenantB = TestAuthHandler.OtherAllowedTenantId;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Admin(string tenantId = TenantA) =>
        factory.CreateClientInTenant(tenantId, AppRoles.HelpdeskAdmin);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    // Ansatt med navn, Entra-ID, sak, tilgangsbestilling og endringer i revisjonsloggen.
    // Med endDate er personen registrert som sluttet.
    private async Task<(Employee Employee, string ObjectId)> PersonAsync(DateTime? endDate = null, string tenantId = TenantA)
    {
        var objectId = Guid.NewGuid().ToString();
        var unique = Guid.NewGuid().ToString("N")[..8];

        await using var db = factory.CreateDbContext(tenantId);
        var employee = new Employee
        {
            FirstName = $"Gunnhild{unique}", LastName = "Personvern", Email = $"gunnhild{unique}@t.no",
            Department = "Økonomi", JobTitle = "Controller", EmployeeNumber = $"E{unique}",
            EntraObjectId = objectId, StartDate = DateTime.UtcNow.AddYears(-3),
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        employee.JobTitle = "Senior controller"; // gir en «updated»-hendelse med stillingen
        db.Tickets.Add(new Ticket { Category = "Feil", Description = "Skriver", ReportedByEmployeeId = employee.Id });
        db.AccessRequests.Add(new AccessRequest
        {
            EmployeeId = employee.Id, EmployeeName = $"Gunnhild{unique} Personvern", SystemName = "NetSuite",
            Reason = "Regnskap", ManagerName = "Per", RequestedByObjectId = objectId,
        });

        if (endDate != null)
        {
            employee.Status = "Sluttet";
            employee.EndDate = DateTime.SpecifyKind(endDate.Value, DateTimeKind.Utc);
        }

        await db.SaveChangesAsync();

        // En hendelse der personen selv er aktør
        db.AuditEvents.Add(new AuditEvent
        {
            EntityType = nameof(Ticket), EntityId = "1", Action = AuditActions.Updated,
            ActorObjectId = objectId, ActorName = $"Gunnhild{unique} Personvern", OccurredAt = DateTime.UtcNow,
        });

        // Personen har brukt appen
        db.AppSignIns.Add(new AppSignIn
        {
            EntraObjectId = objectId, Name = $"Gunnhild{unique} Personvern",
            FirstSeenAt = DateTime.UtcNow.AddYears(-1), LastSeenAt = DateTime.UtcNow.AddMonths(-3), Visits = 12,
        });
        await db.SaveChangesAsync();

        return (employee, objectId);
    }

    [Fact]
    public async Task Export_contains_everything_about_the_person_and_is_logged()
    {
        var (employee, _) = await PersonAsync();

        var response = await Admin().GetAsync($"/api/gdpr/employees/{employee.Id}/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("innsyn-ansatt", response.Content.Headers.ContentDisposition!.FileName);

        var export = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(employee.FirstName, export.GetProperty("employee").GetProperty("firstName").GetString());
        Assert.Equal(1, export.GetProperty("ticketsReported").GetArrayLength());
        Assert.Equal(1, export.GetProperty("accessRequests").GetArrayLength());
        Assert.True(export.GetProperty("auditEvents").GetArrayLength() >= 2);
        Assert.Equal(12, export.GetProperty("appSignIn").GetProperty("visits").GetInt32());

        await using var db = factory.CreateDbContext(TenantA);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.EntityType == nameof(Employee) &&
            e.EntityId == employee.Id.ToString() && e.Action == AuditActions.GdprExported));
    }

    [Fact]
    public async Task Anonymize_removes_personal_data_everywhere_but_keeps_the_rows()
    {
        var (employee, objectId) = await PersonAsync(DateTime.UtcNow.AddMonths(-2));

        var result = await ReadAsync<AnonymizeResult>(
            await Admin().PostAsync($"/api/gdpr/employees/{employee.Id}/anonymize", null));
        Assert.Equal(1, result.AccessRequests);
        Assert.True(result.AuditEventsRedacted >= 3);

        await using var db = factory.CreateDbContext(TenantA);
        var stored = await db.Employees.SingleAsync(e => e.Id == employee.Id);
        Assert.Equal("Anonymisert", stored.FirstName);
        Assert.Null(stored.EntraObjectId);
        Assert.Null(stored.EmployeeNumber);
        Assert.NotNull(stored.AnonymizedAt);

        var request = await db.AccessRequests.SingleAsync(r => r.EmployeeId == employee.Id);
        Assert.Equal($"Anonymisert #{employee.Id}", request.EmployeeName);
        Assert.Null(request.RequestedByObjectId);

        // Saken finnes fortsatt
        Assert.True(await db.Tickets.AnyAsync(t => t.ReportedByEmployeeId == employee.Id));

        // Når personen brukte appen, er slettet
        Assert.False(await db.AppSignIns.AnyAsync(s => s.EntraObjectId == objectId));

        // Ingen revisjonsrader inneholder navnet eller Entra-ID-en lenger
        var audit = await db.AuditEvents.AsNoTracking().ToListAsync();
        Assert.DoesNotContain(audit, e => (e.Changes ?? "").Contains(employee.FirstName) ||
            e.ActorName.Contains(employee.FirstName) || e.ActorObjectId == objectId);
        Assert.Contains(audit, e => e.EntityId == employee.Id.ToString() && e.Action == AuditActions.GdprAnonymized);
    }

    [Fact]
    public async Task Anonymize_is_refused_until_the_person_has_left_and_returned_everything()
    {
        var (active, _) = await PersonAsync();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Admin().PostAsync($"/api/gdpr/employees/{active.Id}/anonymize", null)).StatusCode);

        var (withAsset, _) = await PersonAsync(DateTime.UtcNow.AddDays(-10));
        await using (var db = factory.CreateDbContext(TenantA))
        {
            db.Assets.Add(new Asset { AssetTag = $"G-{Guid.NewGuid():N}"[..12], Type = "laptop",
                Status = AssetStatuses.InUse, AssignedToEmployeeId = withAsset.Id });
            await db.SaveChangesAsync();
        }

        var response = await Admin().PostAsync($"/api/gdpr/employees/{withAsset.Id}/anonymize", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("utstyr", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Automatic_anonymization_follows_the_deadline()
    {
        // Tenant B: egne innstillinger
        Assert.Equal(HttpStatusCode.OK, (await Admin(TenantB).PutAsJsonAsync("/api/gdpr/settings",
            new { anonymizeAfterMonths = 12 })).StatusCode);

        var (old, _) = await PersonAsync(DateTime.UtcNow.AddMonths(-13), TenantB);
        var (recent, _) = await PersonAsync(DateTime.UtcNow.AddMonths(-1), TenantB);

        var candidates = await ReadAsync<List<GdprCandidateDto>>(await Admin(TenantB).GetAsync("/api/gdpr/candidates"));
        Assert.NotNull(candidates.Single(c => c.EmployeeId == recent.Id).AutomaticAt);

        var monitor = factory.Services.GetRequiredService<GdprMonitorService>();
        Assert.Equal(1, await monitor.RunTenantAsync(TenantB, DateTime.UtcNow));

        await using var db = factory.CreateDbContext(TenantB);
        Assert.NotNull((await db.Employees.SingleAsync(e => e.Id == old.Id)).AnonymizedAt);
        Assert.Null((await db.Employees.SingleAsync(e => e.Id == recent.Id)).AnonymizedAt);

        // Aktøren er systemet
        Assert.Equal("system", (await db.AuditEvents.SingleAsync(e =>
            e.EntityId == old.Id.ToString() && e.Action == AuditActions.GdprAnonymized)).ActorObjectId);

        Assert.Equal(HttpStatusCode.BadRequest, (await Admin(TenantB).PutAsJsonAsync("/api/gdpr/settings",
            new { anonymizeAfterMonths = 0 })).StatusCode);
    }

    [Fact]
    public async Task Processing_protocol_has_default_entry_once_and_can_be_edited()
    {
        var admin = Admin();
        var list = await ReadAsync<List<ProcessingActivity>>(await admin.GetAsync("/api/gdpr/processing"));
        var standard = Assert.Single(list, a => a.Name == "IT-drift og brukerstøtte");

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/gdpr/processing/{standard.Id}")).StatusCode);
        list = await ReadAsync<List<ProcessingActivity>>(await admin.GetAsync("/api/gdpr/processing"));
        Assert.DoesNotContain(list, a => a.Name == "IT-drift og brukerstøtte");

        var created = await ReadAsync<ProcessingActivity>(await admin.PostAsJsonAsync("/api/gdpr/processing", new
        {
            name = "Lønn", purpose = "Utbetale lønn", dataSubjects = "Ansatte",
            dataCategories = "Navn, kontonummer", legalBasis = "legal_obligation", retention = "5 år",
        }));
        Assert.Equal(LegalBases.LegalObligation, created.LegalBasis);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/gdpr/processing", new
        {
            name = "X", purpose = "Y", dataSubjects = "Z", dataCategories = "W", legalBasis = "fordi", retention = "1 år",
        })).StatusCode);
    }
}

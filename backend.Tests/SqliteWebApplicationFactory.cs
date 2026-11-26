using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.DataProtection;
using backend.Data;
using backend.Services;

namespace backend.Tests;

// API-et med SQLite i minnet i stedet for PostgreSQL, for tester som trenger data.
// Graph, Azure og Teams er byttet ut med falske tjenester. PostgresWebApplicationFactory
// bruker samme oppsett mot en ekte PostgreSQL-database.
public class SqliteWebApplicationFactory : TestWebApplicationFactory
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    // Den falske Graph-tjenesten (samme instans som API-et bruker)
    public FakeGraphService Graph => (FakeGraphService)Services.GetRequiredService<GraphService>();

    // Den falske Teams-mottakeren
    public FakeTeamsService Teams => (FakeTeamsService)Services.GetRequiredService<TeamsService>();

    // Den falske Azure-tjenesten (samme instans som API-et bruker)
    public FakeAzureService Azure => (FakeAzureService)Services.GetRequiredService<AzureService>();

    // Entra objekt-ID-er som regnes som medlem av alle grupper
    public HashSet<string> GroupMembers => Graph.Members;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
            services.AddDbContext<AppDbContext>((sp, options) =>
                UseDatabase(options).AddInterceptors(sp.GetRequiredService<AuditInterceptor>()));

            services.RemoveAll<GraphService>();
            services.AddSingleton<GraphService>(sp =>
                new FakeGraphService(sp.GetRequiredService<IConfiguration>()));

            services.RemoveAll<TeamsService>();
            services.AddSingleton<TeamsService>(sp =>
                new FakeTeamsService(sp.GetRequiredService<IHttpClientFactory>()));

            // Krypteringsnøkler bare i minnet, så testene ikke skriver til brukerprofilen
            services.AddDataProtection().UseEphemeralDataProtectionProvider();

            ReplaceEmployeeNumbers(services);

            services.RemoveAll<AzureService>();
            services.AddSingleton<AzureService>(sp => new FakeAzureService(
                sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<IConfiguration>()));
        });
    }

    // Databasen opprettes når test-API-et starter, før første forespørsel
    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        using var db = CreateDbContext(host.Services, TestAuthHandler.AllowedTenantId);
        PrepareDatabase(db);

        return host;
    }

    // Databasen API-et bruker (SQLite i minnet her)
    protected virtual DbContextOptionsBuilder UseDatabase(DbContextOptionsBuilder options)
    {
        if (_connection.State != System.Data.ConnectionState.Open)
            _connection.Open();

        return options.UseSqlite(_connection);
    }

    // SQLite har ikke sekvenser (PostgreSQL-oppsettet beholder de ekte tjenestene):
    // ansattnummer og løpenumre for endringer og risikoer
    protected virtual void ReplaceEmployeeNumbers(IServiceCollection services)
    {
        services.RemoveAll<EmployeeNumberService>();
        services.AddScoped<EmployeeNumberService, CountingEmployeeNumberService>();

        services.RemoveAll<GovernanceNumberService>();
        services.AddScoped<GovernanceNumberService, CountingGovernanceNumberService>();
    }

    // Lager tabellene før første forespørsel
    protected virtual void PrepareDatabase(AppDbContext db) => db.Database.EnsureCreated();

    // Kontekst for oppsett av testdata i en bestemt tenant (lagres som "system")
    public AppDbContext CreateDbContext(string tenantId) =>
        CreateDbContext(Services, tenantId);

    private static AppDbContext CreateDbContext(IServiceProvider services, string tenantId)
    {
        var options = services.GetRequiredService<IServiceScopeFactory>()
            .CreateScope().ServiceProvider
            .GetRequiredService<DbContextOptions<AppDbContext>>();

        return new AppDbContext(options, new FixedTenant(tenantId));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
            _connection.Dispose();
    }

    private sealed class FixedTenant(string tenantId) : ITenantContext
    {
        public string? TenantId => tenantId;
    }

}

// SQLite har ikke sekvenser; ansattnummer fra en teller (ekte sekvens testes i PostgresTests)
public class CountingEmployeeNumberService(AppDbContext db) : EmployeeNumberService(db)
{
    private static int _next = 5000;

    public override Task<string> NextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Interlocked.Increment(ref _next).ToString());
}

// SQLite har ikke sekvenser; løpenumre for endringer og risikoer fra tellere
// (ekte sekvenser testes i PostgresTests)
public class CountingGovernanceNumberService(AppDbContext db) : GovernanceNumberService(db)
{
    private static long _changes;
    private static long _risks;

    protected override Task<long> NextAsync(string sequence, CancellationToken cancellationToken) =>
        Task.FromResult(sequence == "change_number_seq"
            ? Interlocked.Increment(ref _changes)
            : Interlocked.Increment(ref _risks));
}

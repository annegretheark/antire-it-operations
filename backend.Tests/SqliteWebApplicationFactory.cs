using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using backend.Data;
using backend.Services;

namespace backend.Tests;

// API-et med SQLite i minnet i stedet for PostgreSQL, for tester som trenger data.
// Graph er byttet ut med FakeGraphService.
public class SqliteWebApplicationFactory : TestWebApplicationFactory
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    // Den falske Graph-tjenesten (samme instans som API-et bruker)
    public FakeGraphService Graph => (FakeGraphService)Services.GetRequiredService<GraphService>();

    // Den falske Azure-tjenesten (samme instans som API-et bruker)
    public FakeAzureService Azure => (FakeAzureService)Services.GetRequiredService<AzureService>();

    // Entra objekt-ID-er som regnes som medlem av alle grupper
    public HashSet<string> GroupMembers => Graph.Members;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        _connection.Open();

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
            services.AddDbContext<AppDbContext>((sp, options) => options
                .UseSqlite(_connection)
                .AddInterceptors(sp.GetRequiredService<AuditInterceptor>()));

            services.RemoveAll<GraphService>();
            services.AddSingleton<GraphService>(sp =>
                new FakeGraphService(sp.GetRequiredService<IConfiguration>()));

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
        db.Database.EnsureCreated();

        return host;
    }

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

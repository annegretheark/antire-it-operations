using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using backend.Data;

namespace backend.Services;

// Det som trengs for å kjøre appen i Azure (steg K). Alt styres av konfigurasjon;
// uten innstillingene under oppfører appen seg som lokalt.
//
//   Azure:ManagedIdentityClientId      Brukertildelt managed identity (client ID)
//   Database:UseEntraAuth              PostgreSQL med Entra-token i stedet for passord
//   DataProtection:BlobUri             Nøkkelring i Blob Storage (felles for alle instanser)
//   DataProtection:KeyVaultKeyUri      Key Vault-nøkkel som beskytter nøkkelringen
//   Frontend: bygde filer i wwwroot serveres fra samme adresse som API-et
public static class AzureHosting
{
    // Token-omfang for Azure Database for PostgreSQL
    private const string PostgresScope = "https://ossrdbms-aad.database.windows.net/.default";

    // Innlogging mot Azure-ressurser: managed identity i Azure, Azure CLI lokalt
    public static TokenCredential AzureCredential(IConfiguration configuration) =>
        new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = configuration["Azure:ManagedIdentityClientId"],
        });

    public static void AddAppDatabase(this WebApplicationBuilder builder)
    {
        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

        // Med Entra-innlogging brukes en egen datakilde der passordet er et token fra
        // managed identity. Den registreres ikke i DI: EF for Npgsql tar i bruk en
        // registrert datakilde automatisk, også der den ikke skal brukes (f.eks. testene).
        NpgsqlDataSource? entraDataSource = null;

        if (builder.Configuration.GetValue("Database:UseEntraAuth", false))
        {
            var credential = AzureCredential(builder.Configuration);
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);

            // Nytt token før det gamle går ut (tokenene varer ca. en time)
            dataSourceBuilder.UsePeriodicPasswordProvider(
                async (_, cancellationToken) =>
                    (await credential.GetTokenAsync(new TokenRequestContext([PostgresScope]), cancellationToken)).Token,
                TimeSpan.FromMinutes(50),
                TimeSpan.FromSeconds(10));

            entraDataSource = dataSourceBuilder.Build();
        }

        builder.Services.AddDbContext<AppDbContext>((services, options) =>
            (entraDataSource != null
                ? options.UseNpgsql(entraDataSource)
                : options.UseNpgsql(connectionString))
            .AddInterceptors(services.GetRequiredService<AuditInterceptor>()));
    }

    // Data Protection-nøklene (krypterer bl.a. Teams-adressen) må overleve omstart og
    // være like på alle instanser. Lokalt ligger de i brukerprofilen som før.
    public static void AddAppDataProtection(this WebApplicationBuilder builder)
    {
        var dataProtection = builder.Services.AddDataProtection()
            .SetApplicationName("antire-it-operations");

        var blobUri = builder.Configuration["DataProtection:BlobUri"];
        var keyUri = builder.Configuration["DataProtection:KeyVaultKeyUri"];

        if (string.IsNullOrWhiteSpace(blobUri))
            return;

        var credential = AzureCredential(builder.Configuration);
        dataProtection.PersistKeysToAzureBlobStorage(new Uri(blobUri), credential);

        if (!string.IsNullOrWhiteSpace(keyUri))
            dataProtection.ProtectKeysWithAzureKeyVault(new Uri(keyUri), credential);
    }

    public static void AddAppHealthChecks(this WebApplicationBuilder builder) =>
        builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");

    // App Service sitter foran appen og sender videre med X-Forwarded-*
    public static void UseAppForwardedHeaders(this WebApplication app)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        };

        // Proxyen i App Service har ikke en fast adresse
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();

        app.UseForwardedHeaders(options);
    }

    // Frontend fra wwwroot på samme adresse som API-et. Ukjente adresser utenfor
    // /api og /health gir index.html (sidene håndteres i frontend). Finnes ikke
    // wwwroot (lokal utvikling med Vite), gjøres ingenting.
    public static void UseFrontend(this WebApplication app)
    {
        var index = Path.Combine(app.Environment.WebRootPath ?? "", "index.html");

        if (string.IsNullOrEmpty(app.Environment.WebRootPath) || !File.Exists(index))
            return;

        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapFallbackToFile("{*path:regex(^(?!api/|health).*$)}", "index.html").AllowAnonymous();
    }
}

// Sjekker at databasen svarer (GET /health)
public class DatabaseHealthCheck(AppDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Databasen svarer ikke.");
        }
        catch (Exception e)
        {
            return HealthCheckResult.Unhealthy("Databasen svarer ikke.", e);
        }
    }
}

using Microsoft.EntityFrameworkCore;
using Npgsql;
using backend.Data;

namespace backend.Tests;

// API-et mot en ekte PostgreSQL-database (0.10b), med de ekte migreringene.
//
// Testene kjøres bare når miljøvariabelen ITOPS_TEST_POSTGRES er satt til en
// tilkobling til en egen testdatabase. Databasenavnet må slutte på «_test»: hele
// skjemaet slettes og bygges opp på nytt før testene, så dev-databasen skal aldri
// brukes her. Uten variabelen hoppes testene over.
public class PostgresWebApplicationFactory : SqliteWebApplicationFactory
{
    public const string EnvironmentVariable = "ITOPS_TEST_POSTGRES";

    public static string? ConnectionString => Environment.GetEnvironmentVariable(EnvironmentVariable);

    public static string? SkipReason =>
        string.IsNullOrWhiteSpace(ConnectionString)
            ? $"Sett {EnvironmentVariable} til en PostgreSQL-testdatabase for å kjøre databasetestene."
            : null;

    protected override DbContextOptionsBuilder UseDatabase(DbContextOptionsBuilder options)
    {
        var connectionString = ConnectionString
            ?? throw new InvalidOperationException($"{EnvironmentVariable} er ikke satt.");

        var database = new NpgsqlConnectionStringBuilder(connectionString).Database ?? "";

        if (!database.EndsWith("_test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Databasen «{database}» slutter ikke på _test. Testene sletter hele skjemaet, " +
                "så de kjøres bare mot en egen testdatabase.");
        }

        return options.UseNpgsql(connectionString);
    }

    // Tomt skjema og de ekte migreringene, slik produksjonsdatabasen bygges
    protected override void PrepareDatabase(AppDbContext db)
    {
        db.Database.ExecuteSqlRaw("DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;");
        db.Database.Migrate();
    }
}

// Fact som hoppes over når testdatabasen ikke er satt opp
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        Skip = PostgresWebApplicationFactory.SkipReason;
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using backend.Data;

namespace backend.Tests;

// API-et mot en ekte PostgreSQL-database (0.10b), med de ekte migreringene.
//
// Testene kjøres bare når miljøvariabelen ITOPS_TEST_POSTGRES er satt. Den må ha
// «Search Path» med ett eget testskjema som slutter på «_test», f.eks.
//   Host=127.0.0.1;Database=antire_it_operations;Username=antire_app;Password=…;Search Path=itops_test
// Testskjemaet slettes og bygges opp på nytt før testene. Appens egne tabeller
// (skjemaet public) røres aldri, så testene kan kjøre i samme database som dev.
// Uten variabelen hoppes testene over.
public class PostgresWebApplicationFactory : SqliteWebApplicationFactory
{
    public const string EnvironmentVariable = "ITOPS_TEST_POSTGRES";

    public static string? ConnectionString => Environment.GetEnvironmentVariable(EnvironmentVariable);

    public static string? SkipReason =>
        string.IsNullOrWhiteSpace(ConnectionString)
            ? $"Sett {EnvironmentVariable} (med Search Path=<skjema>_test) for å kjøre databasetestene."
            : null;

    private string? _schema;

    protected override DbContextOptionsBuilder UseDatabase(DbContextOptionsBuilder options)
    {
        var connectionString = ConnectionString
            ?? throw new InvalidOperationException($"{EnvironmentVariable} er ikke satt.");

        _schema = TestSchema(connectionString);

        // Migreringshistorikken i testskjemaet, ikke appens i public
        return options.UseNpgsql(connectionString, npgsql =>
            npgsql.MigrationsHistoryTable(HistoryRepository.DefaultTableName, _schema));
    }

    // Tomt testskjema og de ekte migreringene, slik produksjonsdatabasen bygges
    protected override void PrepareDatabase(AppDbContext db)
    {
        var schema = _schema ?? throw new InvalidOperationException("Testskjemaet er ikke satt.");

        db.Database.ExecuteSqlRaw($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE; CREATE SCHEMA \"{schema}\";");
        db.Database.Migrate();
    }

    // Ett skjema som slutter på _test; aldri public
    private static string TestSchema(string connectionString)
    {
        var searchPath = new NpgsqlConnectionStringBuilder(connectionString).SearchPath ?? "";
        var schemas = searchPath.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (schemas.Length != 1 ||
            !schemas[0].EndsWith("_test", StringComparison.Ordinal) ||
            !schemas[0].All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
        {
            throw new InvalidOperationException(
                $"{EnvironmentVariable} må ha «Search Path» med nøyaktig ett skjema som slutter på _test " +
                "(små bokstaver, tall og _). Testene sletter hele skjemaet, så appens skjema kan ikke brukes.");
        }

        return schemas[0];
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

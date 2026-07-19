using Vela.Postgres;
using Xunit;

namespace Vela.Postgres.Runtime.Tests;

public sealed class PostgresDatabaseTests
{
    [Fact]
    public void Open_RejectsEmptyConnectionString()
    {
        var exception = Assert.Throws<ArgumentException>(() => VelaPostgres.Open(string.Empty));
        Assert.Equal("connectionString", exception.ParamName);
    }

    [Fact]
    public void MigrationFiles_AreSortedByName()
    {
        var migrationsDirectory = Path.Combine(Path.GetTempPath(), $"vela-pg-migration-order-{Guid.NewGuid():N}");
        Directory.CreateDirectory(migrationsDirectory);
        try
        {
            File.WriteAllText(Path.Combine(migrationsDirectory, "010_second.sql"), "-- second");
            File.WriteAllText(Path.Combine(migrationsDirectory, "002_first.sql"), "-- first");
            File.WriteAllText(Path.Combine(migrationsDirectory, "100_third.sql"), "-- third");

            var files = Directory.GetFiles(migrationsDirectory, "*.sql")
                .Select(Path.GetFileName)
                .Where(static name => name is not null)
                .Cast<string>()
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(["002_first.sql", "010_second.sql", "100_third.sql"], files);
        }
        finally
        {
            Directory.Delete(migrationsDirectory, recursive: true);
        }
    }

    [Fact]
    public void OpenExecuteQueryMigrate_SmokeTest()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        using var database = VelaPostgres.Open(connectionString);
        VelaPostgres.Execute(database, "CREATE TABLE IF NOT EXISTS vela_pg_smoke(id INTEGER PRIMARY KEY, label TEXT NOT NULL);");
        VelaPostgres.Execute(database, "DELETE FROM vela_pg_smoke;");
        Assert.Equal(1, VelaPostgres.Execute(database, "INSERT INTO vela_pg_smoke(label) VALUES ('ok');"));

        var rows = VelaPostgres.Query(database, "SELECT id, label FROM vela_pg_smoke ORDER BY id;");
        Assert.Contains("\"label\":\"ok\"", rows, StringComparison.Ordinal);

        var migrationsDirectory = Path.Combine(Path.GetTempPath(), $"vela-pg-migrations-{Guid.NewGuid():N}");
        Directory.CreateDirectory(migrationsDirectory);
        try
        {
            File.WriteAllText(Path.Combine(migrationsDirectory, "001_audit.sql"), "CREATE TABLE IF NOT EXISTS vela_pg_audit(note TEXT);");
            VelaPostgres.Migrate(database, migrationsDirectory);

            var migrationRows = VelaPostgres.Query(database, "SELECT name FROM _vela_migrations ORDER BY name;");
            Assert.Contains("\"name\":\"001_audit.sql\"", migrationRows, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(migrationsDirectory, recursive: true);
        }
    }
}

using Vela.Sqlite;
using Xunit;

namespace Vela.Sqlite.Runtime.Tests;

public sealed class SqliteDatabaseTests
{
    [Fact]
    public void OpenExecuteQueryMigrate_Roundtrip()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"vela-sqlite-{Guid.NewGuid():N}.db");
        try
        {
            using (var database = VelaSqlite.Open(databasePath))
            {
                Assert.Equal(0, VelaSqlite.Execute(database, "CREATE TABLE message(id INTEGER PRIMARY KEY, text TEXT NOT NULL);"));
                Assert.Equal(1, VelaSqlite.Execute(database, "INSERT INTO message(text) VALUES ('hello');"));
                Assert.Equal(1, VelaSqlite.Execute(database, "INSERT INTO message(text) VALUES ('world');"));

                var rows = VelaSqlite.Query(database, "SELECT id, text FROM message ORDER BY id;");
                Assert.Contains("\"id\":1", rows, StringComparison.Ordinal);
                Assert.Contains("\"text\":\"hello\"", rows, StringComparison.Ordinal);
                Assert.Contains("\"text\":\"world\"", rows, StringComparison.Ordinal);

                var migrationsDirectory = Path.Combine(Path.GetTempPath(), $"vela-migrations-{Guid.NewGuid():N}");
                Directory.CreateDirectory(migrationsDirectory);
                File.WriteAllText(Path.Combine(migrationsDirectory, "002_add.sql"), "ALTER TABLE message ADD COLUMN tag TEXT;");
                File.WriteAllText(Path.Combine(migrationsDirectory, "001_init.sql"), "CREATE TABLE IF NOT EXISTS audit(id INTEGER PRIMARY KEY, note TEXT);");

                VelaSqlite.Migrate(database, migrationsDirectory);
                VelaSqlite.Migrate(database, migrationsDirectory);

                var auditRows = VelaSqlite.Query(database, "SELECT note FROM audit;");
                Assert.Equal("[]", auditRows);

                var migrationRows = VelaSqlite.Query(database, "SELECT name FROM _vela_migrations ORDER BY name;");
                Assert.Contains("\"name\":\"001_init.sql\"", migrationRows, StringComparison.Ordinal);
                Assert.Contains("\"name\":\"002_add.sql\"", migrationRows, StringComparison.Ordinal);
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public void MigrationFiles_AreSortedByName()
    {
        var migrationsDirectory = Path.Combine(Path.GetTempPath(), $"vela-migration-order-{Guid.NewGuid():N}");
        Directory.CreateDirectory(migrationsDirectory);
        try
        {
            File.WriteAllText(Path.Combine(migrationsDirectory, "010_second.sql"), "-- second");
            File.WriteAllText(Path.Combine(migrationsDirectory, "002_first.sql"), "-- first");
            File.WriteAllText(Path.Combine(migrationsDirectory, "100_third.sql"), "-- third");

            using var database = VelaSqlite.Open(":memory:");
            VelaSqlite.Migrate(database, migrationsDirectory);

            var applied = VelaSqlite.Query(database, "SELECT name FROM _vela_migrations ORDER BY name;");
            Assert.Equal(
                "[{\"name\":\"002_first.sql\"},{\"name\":\"010_second.sql\"},{\"name\":\"100_third.sql\"}]",
                applied);
        }
        finally
        {
            Directory.Delete(migrationsDirectory, recursive: true);
        }
    }
}

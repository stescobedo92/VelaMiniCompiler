using System.Globalization;
using System.Text;
using Npgsql;

namespace Vela.Postgres;

/// <summary>PostgreSQL database handle for Vela programs.</summary>
public sealed class VelaPostgresDatabase : IDisposable
{
    private NpgsqlConnection? _connection;
    private bool _disposed;

    private VelaPostgresDatabase(NpgsqlConnection connection) => _connection = connection;

    /// <summary>Opens a PostgreSQL database using <paramref name="connectionString"/>.</summary>
    public static VelaPostgresDatabase Open(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        return new VelaPostgresDatabase(connection);
    }

    /// <summary>Executes non-query SQL and returns the number of rows affected.</summary>
    public int Execute(string sql)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        using var command = _connection!.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteNonQuery();
    }

    /// <summary>Executes a query and returns rows as a JSON array of objects.</summary>
    public string Query(string sql)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        using var command = _connection!.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        return VelaDbJson.FormatRows(reader);
    }

    /// <summary>Applies <c>*.sql</c> files from <paramref name="migrationsDirectory"/> in sorted file-name order.</summary>
    public void Migrate(string migrationsDirectory)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationsDirectory);
        VelaDbMigrations.Apply(_connection!, migrationsDirectory);
    }

    /// <summary>Closes the database connection.</summary>
    public void Close()
    {
        if (_connection is null)
        {
            return;
        }

        _connection.Close();
        _connection.Dispose();
        _connection = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Close();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

/// <summary>Static helpers emitted by the Vela PostgreSQL core module.</summary>
public static class VelaPostgres
{
    /// <summary>Opens a PostgreSQL database.</summary>
    public static VelaPostgresDatabase Open(string connectionString) => VelaPostgresDatabase.Open(connectionString);

    /// <summary>Executes non-query SQL.</summary>
    public static int Execute(VelaPostgresDatabase database, string sql)
    {
        ArgumentNullException.ThrowIfNull(database);
        return database.Execute(sql);
    }

    /// <summary>Executes a query and returns JSON rows.</summary>
    public static string Query(VelaPostgresDatabase database, string sql)
    {
        ArgumentNullException.ThrowIfNull(database);
        return database.Query(sql);
    }

    /// <summary>Applies SQL migrations from a directory.</summary>
    public static void Migrate(VelaPostgresDatabase database, string migrationsDirectory)
    {
        ArgumentNullException.ThrowIfNull(database);
        database.Migrate(migrationsDirectory);
    }

    /// <summary>Closes the database.</summary>
    public static void Close(VelaPostgresDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        database.Close();
    }
}

internal static class VelaDbMigrations
{
    internal const string TableName = "_vela_migrations";

    internal static IReadOnlyList<string> ListMigrationFiles(string migrationsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationsDirectory);
        if (!Directory.Exists(migrationsDirectory))
        {
            throw new DirectoryNotFoundException($"Migration directory not found: {migrationsDirectory}");
        }

        return Directory.GetFiles(migrationsDirectory, "*.sql", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(static name => name is not null)
            .Cast<string>()
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
    }

    internal static void Apply(NpgsqlConnection connection, string migrationsDirectory)
    {
        EnsureTable(connection);
        var applied = LoadApplied(connection);
        foreach (var fileName in ListMigrationFiles(migrationsDirectory))
        {
            if (applied.Contains(fileName))
            {
                continue;
            }

            var sql = File.ReadAllText(Path.Combine(migrationsDirectory, fileName));
            using var transaction = connection.BeginTransaction();
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = sql;
                    command.ExecuteNonQuery();
                }

                using (var record = connection.CreateCommand())
                {
                    record.Transaction = transaction;
                    record.CommandText =
                        $"INSERT INTO {TableName} (name, applied_at) VALUES (@name, @applied_at);";
                    record.Parameters.AddWithValue("name", fileName);
                    record.Parameters.AddWithValue("applied_at", DateTimeOffset.UtcNow);
                    record.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }

    private static void EnsureTable(NpgsqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             CREATE TABLE IF NOT EXISTS {TableName} (
                 name TEXT PRIMARY KEY NOT NULL,
                 applied_at TIMESTAMPTZ NOT NULL
             );
             """;
        command.ExecuteNonQuery();
    }

    private static HashSet<string> LoadApplied(NpgsqlConnection connection)
    {
        var applied = new HashSet<string>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM {TableName};";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            applied.Add(reader.GetString(0));
        }

        return applied;
    }
}

internal static class VelaDbJson
{
    internal static string FormatRows(NpgsqlDataReader reader)
    {
        var columns = Enumerable.Range(0, reader.FieldCount)
            .Select(reader.GetName)
            .ToArray();

        var builder = new StringBuilder();
        builder.Append('[');
        var firstRow = true;
        while (reader.Read())
        {
            if (!firstRow)
            {
                builder.Append(',');
            }

            firstRow = false;
            builder.Append('{');
            for (var index = 0; index < columns.Length; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append('"');
                builder.Append(EscapeJsonString(columns[index]));
                builder.Append("\":");
                builder.Append(FormatValue(reader.IsDBNull(index) ? null : reader.GetValue(index)));
            }

            builder.Append('}');
        }

        builder.Append(']');
        return builder.ToString();
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "null",
        bool boolean => boolean ? "true" : "false",
        byte or sbyte or short or ushort or int or uint or long or ulong =>
            Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        float or double or decimal =>
            Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        byte[] bytes => $"\"{Convert.ToBase64String(bytes)}\"",
        string text => $"\"{EscapeJsonString(text)}\"",
        DateTime dateTime => $"\"{dateTime.ToUniversalTime():O}\"",
        DateTimeOffset dateTimeOffset => $"\"{dateTimeOffset.ToUniversalTime():O}\"",
        Guid guid => $"\"{guid}\"",
        _ => $"\"{EscapeJsonString(value.ToString() ?? string.Empty)}\""
    };

    private static string EscapeJsonString(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (char.IsControl(ch))
                    {
                        builder.Append("\\u");
                        builder.Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(ch);
                    }

                    break;
            }
        }

        return builder.ToString();
    }
}

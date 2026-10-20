using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace AllMyTasks.Storage.Sqlite;

/// <summary>
/// The local SQLite file. Opens connections and brings the schema up to date with ordered, forward-only
/// migrations embedded in this assembly.
/// </summary>
public sealed class SqliteDatabase
{
    private readonly string _connectionString;

    private SqliteDatabase(string path)
    {
        Path = path;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = true,
        }.ToString();
    }

    public string Path { get; }

    /// <summary>The newest schema version this build understands.</summary>
    public static int LatestVersion => Migrations.Value.Count == 0 ? 0 : Migrations.Value[^1].Version;

    private static Lazy<IReadOnlyList<(int Version, string Name, string Sql)>> Migrations { get; } = new(LoadMigrations);

    /// <summary>
    /// Opens (creating if needed) the database at <paramref name="path"/> and migrates it.
    /// Throws <see cref="StorageVersionException"/> if the file is from a newer version.
    /// </summary>
    public static async Task<SqliteDatabase> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var database = new SqliteDatabase(path);
        await database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        return database;
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL;";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The schema version recorded in the file.</summary>
    public async Task<int> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadVersionAsync(connection, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var current = await ReadVersionAsync(connection, null, cancellationToken).ConfigureAwait(false);
        if (current > LatestVersion)
        {
            throw new StorageVersionException(current, LatestVersion);
        }

        foreach (var (version, name, sql) in Migrations.Value.Where(m => m.Version > current))
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "CREATE TABLE IF NOT EXISTS schema_info (version INTEGER NOT NULL);";
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = sql;
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "DELETE FROM schema_info; INSERT INTO schema_info (version) VALUES ($v);";
                    command.Parameters.AddWithValue("$v", version);
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException($"Applying database migration '{name}' failed.", ex);
            }
        }
    }

    private static async Task<int> ReadVersionAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var exists = connection.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_info';";
        if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 0)
        {
            return 0;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version FROM schema_info LIMIT 1;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static List<(int Version, string Name, string Sql)> LoadMigrations()
    {
        var assembly = typeof(SqliteDatabase).Assembly;
        var result = new List<(int, string, string)>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.Ordinal)))
        {
            var file = resource[..^4];
            file = file[(file.LastIndexOf('.') + 1)..];
            var number = file.Split('_')[0];
            if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            result.Add((version, file, reader.ReadToEnd()));
        }

        return [.. result.OrderBy(r => r.Item1)];
    }
}

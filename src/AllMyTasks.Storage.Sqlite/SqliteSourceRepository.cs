using System.Globalization;
using System.Text.Json;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Sync;
using Microsoft.Data.Sqlite;

namespace AllMyTasks.Storage.Sqlite;

/// <summary>Sources, collections, the user's collection choices and per-collection sync state in SQLite.</summary>
public sealed class SqliteSourceRepository(SqliteDatabase database) : ISourceRepository
{
    public async Task<IReadOnlyList<SourceInfo>> GetSourcesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, kind, display_name FROM sources ORDER BY display_name, id;";
        var result = new List<SourceInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new SourceInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return result;
    }

    public async Task<IReadOnlyList<CollectionInfo>> GetCollectionsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_id, id, display_name, kind, COALESCE(color_override, color), is_visible, is_read_only FROM collections ORDER BY source_id, display_name, id;";
        var result = new List<CollectionInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new CollectionInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                (CollectionKind)reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5) != 0,
                reader.GetInt32(6) != 0));
        }

        return result;
    }

    public async Task SaveCollectionsAsync(SourceInfo source, IReadOnlyList<CollectionInfo> collections, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(collections);
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await UpsertSourceAsync(connection, transaction, source, null, cancellationToken).ConfigureAwait(false);

        var keep = collections.Select(c => c.Id).ToHashSet();
        var existing = new List<string>();
        await using (var list = connection.CreateCommand())
        {
            list.Transaction = transaction;
            list.CommandText = "SELECT id FROM collections WHERE source_id = $s;";
            list.Parameters.AddWithValue("$s", source.Id);
            await using var reader = await list.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                existing.Add(reader.GetString(0));
            }
        }

        foreach (var gone in existing.Where(id => !keep.Contains(id)))
        {
            await DeleteCollectionDataAsync(connection, transaction, source.Id, gone, cancellationToken).ConfigureAwait(false);
        }

        foreach (var collection in collections)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            // The user's visibility and colour choice are kept; only what the provider owns is updated.
            command.CommandText = """
                INSERT INTO collections (source_id, id, display_name, kind, color, is_read_only)
                VALUES ($s, $id, $name, $kind, $color, $ro)
                ON CONFLICT (source_id, id) DO UPDATE SET
                    display_name = excluded.display_name, kind = excluded.kind, color = excluded.color, is_read_only = excluded.is_read_only;
                """;
            command.Parameters.AddWithValue("$s", source.Id);
            command.Parameters.AddWithValue("$id", collection.Id);
            command.Parameters.AddWithValue("$name", collection.DisplayName);
            command.Parameters.AddWithValue("$kind", (int)collection.Kind);
            command.Parameters.AddWithValue("$color", (object?)collection.Color ?? DBNull.Value);
            command.Parameters.AddWithValue("$ro", collection.IsReadOnly ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetCollectionPreferencesAsync(string sourceId, string collectionId, bool isVisible, string? colorOverride, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE collections SET is_visible = $v, color_override = $c WHERE source_id = $s AND id = $id;";
        command.Parameters.AddWithValue("$v", isVisible ? 1 : 0);
        command.Parameters.AddWithValue("$c", (object?)colorOverride ?? DBNull.Value);
        command.Parameters.AddWithValue("$s", sourceId);
        command.Parameters.AddWithValue("$id", collectionId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveSourceAsync(SourceInfo source, SourceConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(config);
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await UpsertSourceAsync(connection, null, source, JsonSerializer.Serialize(config.Values), cancellationToken).ConfigureAwait(false);
    }

    public async Task<SourceConfig?> GetSourceConfigAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT config_json FROM sources WHERE id = $id;";
        command.Parameters.AddWithValue("$id", sourceId);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string json)
        {
            return null;
        }

        return new SourceConfig(JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []);
    }

    public async Task RemoveSourceAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var sql in new[]
        {
            "DELETE FROM events WHERE source_id = $s;",
            "DELETE FROM tasks WHERE source_id = $s;",
            "DELETE FROM collections WHERE source_id = $s;",
            "DELETE FROM sources WHERE id = $s;",
        })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$s", sourceId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SyncState?> GetSyncStateAsync(string sourceId, string collectionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sync_token FROM collections WHERE source_id = $s AND id = $c;";
        command.Parameters.AddWithValue("$s", sourceId);
        command.Parameters.AddWithValue("$c", collectionId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string token ? new SyncState(token) : null;
    }

    public async Task SaveSyncStateAsync(string sourceId, string collectionId, SyncState? state, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE collections SET sync_token = $t WHERE source_id = $s AND id = $c;";
        command.Parameters.AddWithValue("$t", (object?)state?.Token ?? DBNull.Value);
        command.Parameters.AddWithValue("$s", sourceId);
        command.Parameters.AddWithValue("$c", collectionId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SourceStatus>> GetSourceStatusesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, status, last_synced_at, last_error, warning, skipped_items, consecutive_failures, next_retry_at, last_error_detail FROM sources;";
        var result = new List<SourceStatus>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var kind = reader.IsDBNull(1) || !Enum.TryParse<SourceStatusKind>(reader.GetString(1), out var parsed) ? SourceStatusKind.New : parsed;
            result.Add(new SourceStatus(
                reader.GetString(0),
                kind,
                ReadDate(reader, 2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                ReadDate(reader, 7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return result;
    }

    public async Task SaveSourceStatusAsync(SourceStatus status, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sources SET status = $status, last_synced_at = $synced, last_error = $error, warning = $warning,
                               skipped_items = $skipped, consecutive_failures = $failures, next_retry_at = $retry,
                               last_error_detail = $detail
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", status.SourceId);
        command.Parameters.AddWithValue("$status", status.Kind.ToString());
        command.Parameters.AddWithValue("$synced", status.LastSyncedAt?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)status.LastError ?? DBNull.Value);
        command.Parameters.AddWithValue("$warning", (object?)status.Warning ?? DBNull.Value);
        command.Parameters.AddWithValue("$detail", (object?)status.LastErrorDetail ?? DBNull.Value);
        command.Parameters.AddWithValue("$skipped", status.SkippedItems);
        command.Parameters.AddWithValue("$failures", status.ConsecutiveFailures);
        command.Parameters.AddWithValue("$retry", status.NextRetryAt?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DateTimeOffset? ReadDate(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : DateTimeOffset.Parse(reader.GetString(index), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public async Task ResetCacheAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM events; DELETE FROM tasks; UPDATE collections SET sync_token = NULL; UPDATE sources SET last_synced_at = NULL, last_error = NULL, last_error_detail = NULL, status = NULL, warning = NULL, skipped_items = 0, consecutive_failures = 0, next_retry_at = NULL;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertSourceAsync(SqliteConnection connection, SqliteTransaction? transaction, SourceInfo source, string? configJson, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = configJson is null
            ? """
              INSERT INTO sources (id, kind, display_name) VALUES ($id, $kind, $name)
              ON CONFLICT (id) DO UPDATE SET kind = excluded.kind, display_name = excluded.display_name;
              """
            : """
              INSERT INTO sources (id, kind, display_name, config_json) VALUES ($id, $kind, $name, $config)
              ON CONFLICT (id) DO UPDATE SET kind = excluded.kind, display_name = excluded.display_name, config_json = excluded.config_json;
              """;
        command.Parameters.AddWithValue("$id", source.Id);
        command.Parameters.AddWithValue("$kind", source.Kind);
        command.Parameters.AddWithValue("$name", source.DisplayName);
        if (configJson is not null)
        {
            command.Parameters.AddWithValue("$config", configJson);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteCollectionDataAsync(SqliteConnection connection, SqliteTransaction transaction, string sourceId, string collectionId, CancellationToken cancellationToken)
    {
        foreach (var sql in new[]
        {
            "DELETE FROM events WHERE source_id = $s AND collection_id = $c;",
            "DELETE FROM tasks WHERE source_id = $s AND collection_id = $c;",
            "DELETE FROM collections WHERE source_id = $s AND id = $c;",
        })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$s", sourceId);
            command.Parameters.AddWithValue("$c", collectionId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

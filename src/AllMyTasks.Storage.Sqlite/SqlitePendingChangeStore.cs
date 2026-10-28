using System.Globalization;
using AllMyTasks.Core.Changes;
using AllMyTasks.Core.Model;

namespace AllMyTasks.Storage.Sqlite;

/// <summary>The queue of unsent changes in the <c>pending_changes</c> table.</summary>
public sealed class SqlitePendingChangeStore(SqliteDatabase database) : IPendingChangeStore
{
    public async Task<IReadOnlyList<PendingChange>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, source_id, collection_id, item_id, kind, created_at, attempts, state, error FROM pending_changes ORDER BY created_at, rowid;";
        var result = new List<PendingChange>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new PendingChange(
                Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                new ItemKey(reader.GetString(1), reader.GetString(2), reader.GetString(3)),
                (PendingKind)reader.GetInt32(4),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
                reader.GetInt32(6),
                (PendingState)reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return result;
    }

    public async Task SaveAsync(PendingChange change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO pending_changes (id, source_id, collection_id, item_id, kind, created_at, attempts, state, error)
            VALUES ($id, $s, $c, $i, $kind, $at, $attempts, $state, $error)
            ON CONFLICT (id) DO UPDATE SET kind = excluded.kind, attempts = excluded.attempts, state = excluded.state, error = excluded.error;
            """;
        command.Parameters.AddWithValue("$id", change.Id.ToString("D"));
        command.Parameters.AddWithValue("$s", change.Key.SourceId);
        command.Parameters.AddWithValue("$c", change.Key.CollectionId);
        command.Parameters.AddWithValue("$i", change.Key.ItemId);
        command.Parameters.AddWithValue("$kind", (int)change.Kind);
        command.Parameters.AddWithValue("$at", change.CreatedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$attempts", change.Attempts);
        command.Parameters.AddWithValue("$state", (int)change.State);
        command.Parameters.AddWithValue("$error", (object?)change.Error ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM pending_changes WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

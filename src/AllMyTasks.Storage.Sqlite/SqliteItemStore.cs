using System.Globalization;
using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Tasks;
using Microsoft.Data.Sqlite;

namespace AllMyTasks.Storage.Sqlite;

/// <summary>The SQLite-backed cache of events and tasks.</summary>
public sealed class SqliteItemStore(SqliteDatabase database) : IItemStore
{
    // Range queries are widened by a day on each side: all-day and floating items are stored as UTC midnights
    // or wall-clock values, and the real zone is only known to the caller, who filters exactly.
    internal const long MarginMilliseconds = 24L * 60 * 60 * 1000;

    public async Task ApplyAsync(string sourceId, string collectionId, SyncResult<CalendarEvent> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (result.IsFullSnapshot)
        {
            await DeleteCollectionAsync(connection, transaction, "events", sourceId, collectionId, cancellationToken).ConfigureAwait(false);
        }

        await DeleteIdsAsync(connection, transaction, "events", sourceId, collectionId, result.DeletedItemIds, cancellationToken).ConfigureAwait(false);
        foreach (var item in result.Upserts)
        {
            await UpsertEventAsync(connection, transaction, item, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyAsync(string sourceId, string collectionId, SyncResult<TaskItem> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (result.IsFullSnapshot)
        {
            await DeleteCollectionAsync(connection, transaction, "tasks", sourceId, collectionId, cancellationToken).ConfigureAwait(false);
        }

        await DeleteIdsAsync(connection, transaction, "tasks", sourceId, collectionId, result.DeletedItemIds, cancellationToken).ConfigureAwait(false);
        foreach (var item in result.Upserts)
        {
            await UpsertTaskAsync(connection, transaction, item, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CalendarEvent>> GetEventCandidatesAsync(
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        TimeZoneInfo displayZone,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.json FROM events e
            WHERE (e.is_recurring = 0 AND e.start_utc < $to + $margin AND e.end_utc >= $from - $margin)
               OR EXISTS (
                    SELECT 1 FROM events m
                    WHERE m.source_id = e.source_id AND m.collection_id = e.collection_id AND m.item_id = e.item_id
                      AND m.is_recurring = 1 AND m.recurrence_id = ''
                      AND m.start_utc < $to + $margin
                      AND (m.until_utc IS NULL OR m.until_utc >= $from - $margin))
            ORDER BY e.start_utc;
            """;
        command.Parameters.AddWithValue("$from", rangeStart.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$to", rangeEnd.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$margin", MarginMilliseconds);

        var result = new List<CalendarEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ModelJson.Deserialize<CalendarEvent>(reader.GetString(0)));
        }

        return result;
    }

    public async Task<int> CountItemsAsync(string sourceId, string collectionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT count(*) FROM events WHERE source_id = $s AND collection_id = $c)
                 + (SELECT count(*) FROM tasks WHERE source_id = $s AND collection_id = $c);
            """;
        command.Parameters.AddWithValue("$s", sourceId);
        command.Parameters.AddWithValue("$c", collectionId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<TaskItem>> GetTasksAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM tasks ORDER BY source_id, collection_id, item_id;";
        var result = new List<TaskItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ModelJson.Deserialize<TaskItem>(reader.GetString(0)));
        }

        return result;
    }

    private static async Task UpsertEventAsync(SqliteConnection connection, SqliteTransaction transaction, CalendarEvent item, CancellationToken cancellationToken)
    {
        long start;
        long end;
        string? startDate = null;
        string? endDate = null;
        string? tz = null;
        var floating = false;
        TimeSpan duration;
        switch (item.Time)
        {
            case TimedEvent timed:
                // Floating items are wall-clock values; their stored offset is meaningless, so the wall clock is used as UTC.
                start = timed.IsFloating ? new DateTimeOffset(timed.Start.DateTime, TimeSpan.Zero).ToUnixTimeMilliseconds() : timed.Start.ToUnixTimeMilliseconds();
                end = timed.IsFloating ? new DateTimeOffset(timed.End.DateTime, TimeSpan.Zero).ToUnixTimeMilliseconds() : timed.End.ToUnixTimeMilliseconds();
                tz = timed.TimeZoneId;
                floating = timed.IsFloating;
                duration = timed.End - timed.Start;
                break;
            case AllDayEvent allDay:
                start = new DateTimeOffset(allDay.Start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeMilliseconds();
                end = new DateTimeOffset(allDay.EndExclusive.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeMilliseconds();
                startDate = allDay.Start.ToString("O", CultureInfo.InvariantCulture);
                endDate = allDay.EndExclusive.ToString("O", CultureInfo.InvariantCulture);
                duration = TimeSpan.FromDays(allDay.Days);
                break;
            default:
                throw new InvalidOperationException("Unknown event time type.");
        }

        var isMaster = !item.Key.IsOverride;
        var recurring = isMaster && item.Recurrence is not null;
        var until = recurring ? RecurrenceBounds.UntilUtcMilliseconds(item, duration) : null;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO events (source_id, collection_id, item_id, recurrence_id, title, all_day, start_utc, end_utc, start_date, end_date,
                                is_floating, tz_id, status, is_recurring, until_utc, last_modified, json)
            VALUES ($source, $collection, $item, $rid, $title, $allDay, $start, $end, $startDate, $endDate,
                    $floating, $tz, $status, $recurring, $until, $modified, $json)
            ON CONFLICT (source_id, collection_id, item_id, recurrence_id) DO UPDATE SET
                title = excluded.title, all_day = excluded.all_day, start_utc = excluded.start_utc, end_utc = excluded.end_utc,
                start_date = excluded.start_date, end_date = excluded.end_date, is_floating = excluded.is_floating,
                tz_id = excluded.tz_id, status = excluded.status, is_recurring = excluded.is_recurring,
                until_utc = excluded.until_utc, last_modified = excluded.last_modified, json = excluded.json;
            """;
        command.Parameters.AddWithValue("$source", item.Key.SourceId);
        command.Parameters.AddWithValue("$collection", item.Key.CollectionId);
        command.Parameters.AddWithValue("$item", item.Key.ItemId);
        command.Parameters.AddWithValue("$rid", item.Key.RecurrenceId?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
        command.Parameters.AddWithValue("$title", item.Title);
        command.Parameters.AddWithValue("$allDay", item.Time is AllDayEvent ? 1 : 0);
        command.Parameters.AddWithValue("$start", start);
        command.Parameters.AddWithValue("$end", end);
        command.Parameters.AddWithValue("$startDate", (object?)startDate ?? DBNull.Value);
        command.Parameters.AddWithValue("$endDate", (object?)endDate ?? DBNull.Value);
        command.Parameters.AddWithValue("$floating", floating ? 1 : 0);
        command.Parameters.AddWithValue("$tz", (object?)tz ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", (int)item.Status);
        command.Parameters.AddWithValue("$recurring", recurring ? 1 : 0);
        command.Parameters.AddWithValue("$until", (object?)until ?? DBNull.Value);
        command.Parameters.AddWithValue("$modified", item.LastModified?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$json", ModelJson.Serialize(item));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertTaskAsync(SqliteConnection connection, SqliteTransaction transaction, TaskItem item, CancellationToken cancellationToken)
    {
        string? dueDate = null;
        long? dueUtc = null;
        switch (item.Due)
        {
            case DueDate d:
                dueDate = d.Date.ToString("O", CultureInfo.InvariantCulture);
                dueUtc = new DateTimeOffset(d.Date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeMilliseconds();
                break;
            case DueDateTime dt:
                dueUtc = dt.At.ToUnixTimeMilliseconds();
                break;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO tasks (source_id, collection_id, item_id, title, status, due_date, due_utc, parent_item_id, priority, last_modified, json)
            VALUES ($source, $collection, $item, $title, $status, $dueDate, $dueUtc, $parent, $priority, $modified, $json)
            ON CONFLICT (source_id, collection_id, item_id) DO UPDATE SET
                title = excluded.title, status = excluded.status, due_date = excluded.due_date, due_utc = excluded.due_utc,
                parent_item_id = excluded.parent_item_id, priority = excluded.priority, last_modified = excluded.last_modified,
                json = excluded.json;
            """;
        command.Parameters.AddWithValue("$source", item.Key.SourceId);
        command.Parameters.AddWithValue("$collection", item.Key.CollectionId);
        command.Parameters.AddWithValue("$item", item.Key.ItemId);
        command.Parameters.AddWithValue("$title", item.Title);
        command.Parameters.AddWithValue("$status", (int)item.Status);
        command.Parameters.AddWithValue("$dueDate", (object?)dueDate ?? DBNull.Value);
        command.Parameters.AddWithValue("$dueUtc", (object?)dueUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("$parent", (object?)item.ParentKey?.ItemId ?? DBNull.Value);
        command.Parameters.AddWithValue("$priority", (int)item.Priority);
        command.Parameters.AddWithValue("$modified", item.LastModified?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$json", ModelJson.Serialize(item));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // Table names come from the two fixed call sites above, never from input.
    private static async Task DeleteCollectionAsync(SqliteConnection connection, SqliteTransaction transaction, string table, string sourceId, string collectionId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = table == "events"
            ? "DELETE FROM events WHERE source_id = $s AND collection_id = $c;"
            : "DELETE FROM tasks WHERE source_id = $s AND collection_id = $c;";
        command.Parameters.AddWithValue("$s", sourceId);
        command.Parameters.AddWithValue("$c", collectionId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteIdsAsync(SqliteConnection connection, SqliteTransaction transaction, string table, string sourceId, string collectionId, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        foreach (var id in ids)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = table == "events"
                ? "DELETE FROM events WHERE source_id = $s AND collection_id = $c AND item_id = $i;"
                : "DELETE FROM tasks WHERE source_id = $s AND collection_id = $c AND item_id = $i;";
            command.Parameters.AddWithValue("$s", sourceId);
            command.Parameters.AddWithValue("$c", collectionId);
            command.Parameters.AddWithValue("$i", id);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

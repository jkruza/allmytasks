using System.Globalization;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Reminders;
using Microsoft.Data.Sqlite;

namespace AllMyTasks.Storage.Sqlite;

/// <summary>The reminder log in the <c>reminder_log</c> table, and the time of the last check in <c>settings</c>.</summary>
public sealed class SqliteReminderLog(SqliteDatabase database) : IReminderLog
{
    internal const string LastCheckKey = "reminders.lastCheck";

    public async Task<IReadOnlyList<ReminderLogEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_id, collection_id, item_id, recurrence_id, occurrence_utc, kind, title, detail,
                   fire_at_utc, ends_at_utc, state, snoozed_until_utc
            FROM reminder_log;
            """;
        var result = new List<ReminderLogEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var recurrence = reader.GetString(3);
            var key = new ItemKey(reader.GetString(0), reader.GetString(1), reader.GetString(2), recurrence.Length == 0 ? null : Instant(long.Parse(recurrence, CultureInfo.InvariantCulture)));
            var reminder = new Reminder(
                key,
                Instant(reader.GetInt64(8)),
                (ReminderKind)reader.GetInt32(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                Instant(reader.GetInt64(4)),
                Instant(reader.GetInt64(9)));
            result.Add(new ReminderLogEntry(reminder, (ReminderState)reader.GetInt32(10), reader.IsDBNull(11) ? null : Instant(reader.GetInt64(11))));
        }

        return result;
    }

    public async Task SaveAsync(ReminderLogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var reminder = entry.Reminder;
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO reminder_log (source_id, collection_id, item_id, recurrence_id, occurrence_utc, kind, title, detail,
                                      fire_at_utc, ends_at_utc, state, snoozed_until_utc)
            VALUES ($source, $collection, $item, $recurrence, $occurrence, $kind, $title, $detail, $fire, $ends, $state, $until)
            ON CONFLICT (source_id, collection_id, item_id, recurrence_id, occurrence_utc) DO UPDATE SET
                kind = excluded.kind, title = excluded.title, detail = excluded.detail, fire_at_utc = excluded.fire_at_utc,
                ends_at_utc = excluded.ends_at_utc, state = excluded.state, snoozed_until_utc = excluded.snoozed_until_utc;
            """;
        command.Parameters.AddWithValue("$source", reminder.Key.SourceId);
        command.Parameters.AddWithValue("$collection", reminder.Key.CollectionId);
        command.Parameters.AddWithValue("$item", reminder.Key.ItemId);
        command.Parameters.AddWithValue("$recurrence", reminder.Key.RecurrenceId is { } r ? r.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) : string.Empty);
        command.Parameters.AddWithValue("$occurrence", reminder.Occurrence.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$kind", (int)reminder.Kind);
        command.Parameters.AddWithValue("$title", reminder.Title);
        command.Parameters.AddWithValue("$detail", (object?)reminder.Detail ?? DBNull.Value);
        command.Parameters.AddWithValue("$fire", reminder.FireAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$ends", reminder.EndsAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$state", (int)entry.State);
        command.Parameters.AddWithValue("$until", entry.SnoozedUntil is { } until ? until.ToUnixTimeMilliseconds() : DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PruneAsync(DateTimeOffset endedBefore, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM reminder_log WHERE ends_at_utc < $before;";
        command.Parameters.AddWithValue("$before", endedBefore.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DateTimeOffset?> GetLastCheckAsync(CancellationToken cancellationToken = default)
    {
        var text = await new SqliteSettingsStore(database).GetAsync(LastCheckKey, cancellationToken).ConfigureAwait(false);
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds) ? Instant(milliseconds) : null;
    }

    public Task SetLastCheckAsync(DateTimeOffset at, CancellationToken cancellationToken = default) =>
        new SqliteSettingsStore(database).SetAsync(LastCheckKey, at.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), cancellationToken);

    private static DateTimeOffset Instant(long milliseconds) => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
}

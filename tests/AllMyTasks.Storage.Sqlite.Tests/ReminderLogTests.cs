using AllMyTasks.Core.Model;
using AllMyTasks.Core.Reminders;

namespace AllMyTasks.Storage.Sqlite.Tests;

public class ReminderLogTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static Reminder Sample(string id, DateTimeOffset? recurrence = null, string? detail = "12:00 · Room") =>
        new(new ItemKey("s", "c", id, recurrence), T0.AddMinutes(-10), ReminderKind.EventStart, "Title ünï ✓", detail, T0, T0.AddHours(1));

    [Fact]
    public async Task Entries_round_trip_including_snooze_and_override_keys()
    {
        var (temp, db) = await TempDatabase.CreateAsync();
        using var _ = temp;
        var log = new SqliteReminderLog(db);
        var shown = new ReminderLogEntry(Sample("a"), ReminderState.Shown);
        var snoozed = new ReminderLogEntry(Sample("b", recurrence: T0.AddDays(7), detail: null), ReminderState.Snoozed, T0.AddMinutes(10));

        await log.SaveAsync(shown, Ct);
        await log.SaveAsync(snoozed, Ct);

        var loaded = (await log.GetAllAsync(Ct)).ToDictionary(e => e.Reminder.Id);
        Assert.Equal(2, loaded.Count);
        Assert.Equal(shown, loaded[shown.Reminder.Id]);
        Assert.Equal(snoozed, loaded[snoozed.Reminder.Id]);
    }

    [Fact]
    public async Task Saving_again_replaces_the_row()
    {
        var (temp, db) = await TempDatabase.CreateAsync();
        using var _ = temp;
        var log = new SqliteReminderLog(db);
        var reminder = Sample("a");

        await log.SaveAsync(new ReminderLogEntry(reminder, ReminderState.Snoozed, T0), Ct);
        await log.SaveAsync(new ReminderLogEntry(reminder, ReminderState.Dismissed), Ct);

        var entry = Assert.Single(await log.GetAllAsync(Ct));
        Assert.Equal(ReminderState.Dismissed, entry.State);
        Assert.Null(entry.SnoozedUntil);
    }

    [Fact]
    public async Task Prune_removes_only_rows_that_ended_before_the_cutoff()
    {
        var (temp, db) = await TempDatabase.CreateAsync();
        using var _ = temp;
        var log = new SqliteReminderLog(db);
        var old = Sample("old") with { EndsAt = T0.AddDays(-2) };
        await log.SaveAsync(new ReminderLogEntry(old, ReminderState.Shown), Ct);
        await log.SaveAsync(new ReminderLogEntry(Sample("recent"), ReminderState.Shown), Ct);

        await log.PruneAsync(T0.AddDays(-1), Ct);

        Assert.Equal("recent", Assert.Single(await log.GetAllAsync(Ct)).Reminder.Key.ItemId);
    }

    [Fact]
    public async Task Last_check_is_remembered()
    {
        var (temp, db) = await TempDatabase.CreateAsync();
        using var _ = temp;
        var log = new SqliteReminderLog(db);
        Assert.Null(await log.GetLastCheckAsync(Ct));

        await log.SetLastCheckAsync(T0, Ct);

        Assert.Equal(T0, await log.GetLastCheckAsync(Ct));
    }
}

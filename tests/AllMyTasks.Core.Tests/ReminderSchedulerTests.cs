using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Reminders;
using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Core.Tests;

public class ReminderSchedulerTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Start = ViewTestKit.Utc_(2026, 10, 20, 12);

    private sealed class FakeNotifier : INotifier
    {
        public List<Reminder> Shown { get; } = [];

        public bool Throws { get; set; }

        public bool SupportsActions => true;

        public event EventHandler<NotificationActivatedEventArgs>? Activated;

        public Task ShowAsync(Reminder reminder, CancellationToken cancellationToken = default)
        {
            Shown.Add(reminder);
            return Throws ? throw new InvalidOperationException("boom") : Task.CompletedTask;
        }

        public async Task ActivateAsync(Reminder reminder, NotificationAction action)
        {
            Activated?.Invoke(this, new NotificationActivatedEventArgs(reminder, action));
            await Task.Delay(100, Ct);
        }
    }

    private sealed class Rig
    {
        public required InMemoryStoreHolder Holder { get; init; }

        public FixedClock Clock { get; init; } = new(Start);

        public InMemoryReminderLog Log { get; init; } = new();

        public FakeNotifier Notifier { get; init; } = new();

        public ReminderSettings Settings { get; set; } = ReminderSettings.Default;

        public ReminderScheduler Create() => new(Holder.Service, Log, Notifier, () => Settings, ViewTestKit.Utc, Clock);
    }

    private sealed record InMemoryStoreHolder(AllMyTasks.Core.Storage.InMemoryStore Store, AllMyTasks.Core.Views.AgendaService Service);

    private static async Task<Rig> RigAsync(IEnumerable<CalendarEvent> events, IEnumerable<TaskItem>? tasks = null)
    {
        var (store, service) = await ViewTestKit.ServiceAsync(events, tasks ?? [], Start);
        return new Rig { Holder = new InMemoryStoreHolder(store, service) };
    }

    private static CalendarEvent Timed(string id, DateTimeOffset start, int minutes = 30) =>
        new(TestData.Key(id), id, new TimedEvent(start, start.AddMinutes(minutes)));

    [Fact]
    public async Task Delivers_a_reminder_when_it_is_due_and_not_before()
    {
        var rig = await RigAsync([Timed("Standup", Start.AddMinutes(30))]);
        using var scheduler = rig.Create();

        await scheduler.TickAsync();
        Assert.Empty(rig.Notifier.Shown);

        rig.Clock.Now = Start.AddMinutes(20);
        await scheduler.TickAsync();

        Assert.Equal("Standup", Assert.Single(rig.Notifier.Shown).Title);
    }

    [Fact]
    public async Task Sleeps_until_the_next_reminder_but_never_longer_than_the_cap()
    {
        var rig = await RigAsync([Timed("Soon", Start.AddMinutes(10).AddSeconds(45)), Timed("Far", Start.AddHours(5))]);
        using var scheduler = rig.Create();

        await scheduler.TickAsync();

        Assert.Equal(Start.AddSeconds(45), scheduler.NextWake);

        rig.Clock.Now = Start.AddMinutes(1);
        await scheduler.TickAsync();

        Assert.Equal(Start.AddMinutes(1) + ReminderScheduler.MaxSleep, scheduler.NextWake);
    }

    [Fact]
    public async Task Never_delivers_the_same_reminder_twice_even_across_restarts()
    {
        var rig = await RigAsync([Timed("Standup", Start.AddMinutes(5))]);
        using (var first = rig.Create())
        {
            await first.TickAsync();
            await first.TickAsync();
        }

        using var second = rig.Create();
        await second.TickAsync();

        Assert.Single(rig.Notifier.Shown);
    }

    [Fact]
    public async Task A_moved_event_gets_a_new_reminder_at_its_new_time()
    {
        var rig = await RigAsync([Timed("Standup", Start.AddMinutes(5))]);
        using var scheduler = rig.Create();
        await scheduler.TickAsync();

        await rig.Holder.Store.ApplyAsync("s", "c", SyncResult.Snapshot<CalendarEvent>([Timed("Standup", Start.AddHours(3))], new SyncState("y")), Ct);
        rig.Clock.Now = Start.AddHours(3).AddMinutes(-9);
        await scheduler.TickAsync();

        Assert.Equal(2, rig.Notifier.Shown.Count);
        Assert.Equal(Start.AddHours(3), rig.Notifier.Shown[1].Occurrence);
    }

    [Fact]
    public async Task Catch_up_delivers_a_reminder_that_became_due_a_few_minutes_ago()
    {
        var rig = await RigAsync([Timed("Standup", Start.AddMinutes(10))]);
        using var scheduler = rig.Create();

        rig.Clock.Now = Start.AddMinutes(5);
        await scheduler.TickAsync();

        Assert.Single(rig.Notifier.Shown);
    }

    [Fact]
    public async Task An_event_that_already_ended_is_not_announced_and_not_counted_as_missed()
    {
        var events = Enumerable.Range(0, 5).Select(i => Timed($"Past{i}", Start.AddHours(-2), 30)).ToList();
        var rig = await RigAsync(events);
        await rig.Log.SetLastCheckAsync(Start.AddHours(-4), Ct);
        using var scheduler = rig.Create();

        await scheduler.TickAsync();

        Assert.Empty(rig.Notifier.Shown);
    }

    [Fact]
    public async Task More_than_three_missed_reminders_become_one_summary()
    {
        // Long events still running now, whose reminders came due an hour ago while the app was closed.
        var events = Enumerable.Range(0, 4).Select(i => Timed($"Long{i}", Start.AddMinutes(-50), 240)).ToList();
        var rig = await RigAsync(events);
        await rig.Log.SetLastCheckAsync(Start.AddHours(-3), Ct);
        using var scheduler = rig.Create();

        await scheduler.TickAsync();

        var summary = Assert.Single(rig.Notifier.Shown);
        Assert.Equal(ReminderKind.MissedSummary, summary.Kind);
        Assert.Equal("4 reminders missed", summary.Title);
        Assert.False(summary.CanSnooze);

        await scheduler.TickAsync();
        Assert.Single(rig.Notifier.Shown);
    }

    [Fact]
    public async Task Up_to_three_missed_reminders_are_skipped_silently()
    {
        var events = Enumerable.Range(0, 3).Select(i => Timed($"Long{i}", Start.AddMinutes(-50), 240)).ToList();
        var rig = await RigAsync(events);
        await rig.Log.SetLastCheckAsync(Start.AddHours(-3), Ct);
        using var scheduler = rig.Create();

        await scheduler.TickAsync();

        Assert.Empty(rig.Notifier.Shown);
    }

    [Fact]
    public async Task A_first_ever_start_reports_nothing_as_missed()
    {
        var events = Enumerable.Range(0, 5).Select(i => Timed($"Long{i}", Start.AddMinutes(-50), 240)).ToList();
        var rig = await RigAsync(events);
        using var scheduler = rig.Create();

        await scheduler.TickAsync();

        Assert.Empty(rig.Notifier.Shown);
    }

    [Fact]
    public async Task Snooze_delivers_the_reminder_again_after_the_snooze_length_and_only_once()
    {
        var rig = await RigAsync([Timed("Standup", Start.AddMinutes(5))]);
        using var scheduler = rig.Create();
        await scheduler.TickAsync();
        var reminder = Assert.Single(rig.Notifier.Shown);

        await rig.Notifier.ActivateAsync(reminder, NotificationAction.Snooze);
        rig.Clock.Now = Start.AddMinutes(9);
        await scheduler.TickAsync();
        Assert.Single(rig.Notifier.Shown);

        rig.Clock.Now = Start.AddMinutes(11);
        await scheduler.TickAsync();
        Assert.Equal(2, rig.Notifier.Shown.Count);

        rig.Clock.Now = Start.AddMinutes(30);
        await scheduler.TickAsync();
        Assert.Equal(2, rig.Notifier.Shown.Count);
    }

    [Fact]
    public async Task A_snoozed_reminder_is_dropped_when_its_task_was_completed_meanwhile()
    {
        var task = new TaskItem(TestData.Key("t"), "Call", Due: new DueDateTime(Start.AddMinutes(5)));
        var rig = await RigAsync([], [task]);
        using var scheduler = rig.Create();
        rig.Clock.Now = Start.AddMinutes(6);
        await scheduler.TickAsync();
        var reminder = Assert.Single(rig.Notifier.Shown);
        await rig.Notifier.ActivateAsync(reminder, NotificationAction.Snooze);

        await rig.Holder.Store.ApplyAsync("s", "c", SyncResult.Snapshot<TaskItem>([task with { Status = TaskItemStatus.Completed }], new SyncState("y")), Ct);
        rig.Clock.Now = Start.AddMinutes(20);
        await scheduler.TickAsync();

        Assert.Single(rig.Notifier.Shown);
    }

    [Fact]
    public async Task Dismiss_keeps_the_reminder_from_coming_back()
    {
        var rig = await RigAsync([Timed("Standup", Start.AddMinutes(5))]);
        using var scheduler = rig.Create();
        await scheduler.TickAsync();

        await rig.Notifier.ActivateAsync(rig.Notifier.Shown[0], NotificationAction.Dismiss);
        rig.Clock.Now = Start.AddHours(1);
        await scheduler.TickAsync();

        Assert.Single(rig.Notifier.Shown);
        Assert.Equal(ReminderState.Dismissed, Assert.Single(await rig.Log.GetAllAsync(Ct)).State);
    }

    [Fact]
    public async Task Open_asks_the_app_to_show_the_item()
    {
        var rig = await RigAsync([Timed("Standup", Start.AddMinutes(5))]);
        using var scheduler = rig.Create();
        Reminder? opened = null;
        scheduler.OpenRequested += (_, r) => opened = r;
        await scheduler.TickAsync();

        await rig.Notifier.ActivateAsync(rig.Notifier.Shown[0], NotificationAction.Open);

        Assert.Equal(rig.Notifier.Shown[0], opened);
    }

    [Fact]
    public async Task Disabled_reminders_show_nothing_and_do_not_count_the_gap_as_missed()
    {
        var events = Enumerable.Range(0, 5).Select(i => Timed($"Long{i}", Start.AddMinutes(-50), 240)).ToList();
        var rig = await RigAsync(events);
        rig.Settings = ReminderSettings.Default with { Enabled = false };
        await rig.Log.SetLastCheckAsync(Start.AddHours(-3), Ct);
        using var scheduler = rig.Create();

        await scheduler.TickAsync();
        rig.Settings = ReminderSettings.Default;
        await scheduler.TickAsync();

        Assert.Empty(rig.Notifier.Shown);
    }

    [Fact]
    public async Task A_notifier_that_throws_does_not_stop_reminders_or_repeat_them()
    {
        var rig = await RigAsync([Timed("A", Start.AddMinutes(5)), Timed("B", Start.AddMinutes(6))]);
        rig.Notifier.Throws = true;
        using var scheduler = rig.Create();

        await scheduler.TickAsync();
        await scheduler.TickAsync();

        Assert.Equal(2, rig.Notifier.Shown.Count);
    }

    [Fact]
    public async Task Old_log_rows_are_pruned_a_day_after_the_item_ended()
    {
        var rig = await RigAsync([Timed("Standup", Start.AddMinutes(5))]);
        using var scheduler = rig.Create();
        await scheduler.TickAsync();
        Assert.Single(await rig.Log.GetAllAsync(Ct));

        rig.Clock.Now = Start.AddDays(2);
        await scheduler.TickAsync();

        Assert.Empty(await rig.Log.GetAllAsync(Ct));
    }
}

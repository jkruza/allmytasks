using System.ComponentModel;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Reminders;

namespace AllMyTasks.Core.Tests;

public class LinuxNotifierTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(50);
    private static readonly DateTimeOffset At = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static Reminder Sample(string title = "Standup", string? detail = "12:00 · Room 3") =>
        new(new ItemKey("s", "c", "1"), At, ReminderKind.EventStart, title, detail, At, At.AddHours(1));

    private sealed class FakeLauncher(Func<IReadOnlyList<string>, Task<ProcessResult>> run) : IProcessLauncher
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            Assert.Equal("notify-send", fileName);
            Calls.Add(arguments);
            return run(arguments);
        }
    }

    [Fact]
    public async Task Shows_with_actions_and_returns_while_the_notification_is_still_open()
    {
        var open = new TaskCompletionSource<ProcessResult>();
        var launcher = new FakeLauncher(_ => open.Task);
        var notifier = new LinuxNotifier(launcher, Grace);

        await notifier.ShowAsync(Sample(), Ct);

        var args = Assert.Single(launcher.Calls);
        Assert.Contains("--action=default=Open", args);
        Assert.Contains("--action=snooze=Snooze", args);
        Assert.Contains("--action=dismiss=Dismiss", args);
        Assert.Contains("--wait", args);
        Assert.Equal(["--", "Standup", "12:00 · Room 3"], args.Skip(args.ToList().IndexOf("--")));
        Assert.True(notifier.SupportsActions);
    }

    [Theory]
    [InlineData("snooze\n", NotificationAction.Snooze)]
    [InlineData("dismiss\n", NotificationAction.Dismiss)]
    [InlineData("default\n", NotificationAction.Open)]
    public async Task Reports_the_action_the_user_chose(string output, NotificationAction expected)
    {
        var closed = new TaskCompletionSource<ProcessResult>();
        var notifier = new LinuxNotifier(new FakeLauncher(_ => closed.Task), Grace);
        var activated = new TaskCompletionSource<NotificationActivatedEventArgs>();
        notifier.Activated += (_, e) => activated.TrySetResult(e);
        var reminder = Sample();

        await notifier.ShowAsync(reminder, Ct);
        closed.SetResult(new ProcessResult(0, output));

        var e = await activated.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(expected, e.Action);
        Assert.Equal(reminder, e.Reminder);
    }

    [Fact]
    public async Task Closing_without_a_choice_reports_nothing()
    {
        var closed = new TaskCompletionSource<ProcessResult>();
        var notifier = new LinuxNotifier(new FakeLauncher(_ => closed.Task), Grace);
        var raised = false;
        notifier.Activated += (_, _) => raised = true;

        await notifier.ShowAsync(Sample(), Ct);
        closed.SetResult(new ProcessResult(0, string.Empty));
        await Task.Delay(100, Ct);

        Assert.False(raised);
    }

    [Fact]
    public async Task Snooze_is_not_offered_on_the_missed_summary()
    {
        var args = LinuxNotifier.Arguments(Reminder.Missed(4, At), withActions: true);

        Assert.DoesNotContain("--action=snooze=Snooze", args);
        Assert.Contains("--action=dismiss=Dismiss", args);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Falls_back_to_a_plain_notification_when_notify_send_does_not_know_actions()
    {
        var launcher = new FakeLauncher(args => args.Contains("--wait")
            ? Task.FromResult(new ProcessResult(64, string.Empty))
            : new TaskCompletionSource<ProcessResult>().Task);
        var notifier = new LinuxNotifier(launcher, Grace);

        await notifier.ShowAsync(Sample(), Ct);
        await notifier.ShowAsync(Sample("Second"), Ct);

        Assert.Equal(3, launcher.Calls.Count);
        Assert.DoesNotContain("--wait", launcher.Calls[1]);
        Assert.DoesNotContain("--wait", launcher.Calls[2]);
        Assert.False(notifier.SupportsActions);
    }

    [Fact]
    public async Task Reports_unavailable_when_notify_send_is_missing()
    {
        var notifier = new LinuxNotifier(new FakeLauncher(_ => throw new Win32Exception(2)), Grace);

        await Assert.ThrowsAsync<NotifierUnavailableException>(() => notifier.ShowAsync(Sample(), Ct));
    }

    [Fact]
    public async Task Reports_unavailable_when_there_is_no_notification_daemon()
    {
        var notifier = new LinuxNotifier(new FakeLauncher(_ => Task.FromResult(new ProcessResult(1, string.Empty))), Grace);

        await Assert.ThrowsAsync<NotifierUnavailableException>(() => notifier.ShowAsync(Sample(), Ct));
    }

    [Fact]
    public void Markup_in_the_body_is_escaped_and_a_dash_title_stays_text()
    {
        var args = LinuxNotifier.Arguments(Sample("--urgency=critical", "<b>bold</b> & more"), withActions: false);

        var rest = args.Skip(args.ToList().IndexOf("--") + 1).ToList();
        Assert.Equal(["--urgency=critical", "&lt;b&gt;bold&lt;/b&gt; &amp; more"], rest);
    }

    [Fact]
    public async Task Fallback_notifier_uses_the_second_notifier_once_the_first_is_unavailable()
    {
        var primary = new FakeNotifier { Unavailable = true };
        var fallback = new FakeNotifier();
        var notifier = new FallbackNotifier(primary, fallback);
        var raised = 0;
        notifier.Activated += (_, _) => raised++;

        await notifier.ShowAsync(Sample("A"), Ct);
        await notifier.ShowAsync(Sample("B"), Ct);
        fallback.Raise(Sample(), NotificationAction.Open);
        primary.Raise(Sample(), NotificationAction.Open);

        Assert.Equal(1, primary.Calls);
        Assert.Equal(2, fallback.Calls);
        Assert.Equal(2, raised);
    }

    private sealed class FakeNotifier : INotifier
    {
        public bool Unavailable { get; init; }

        public int Calls { get; private set; }

        public bool SupportsActions => true;

        public event EventHandler<NotificationActivatedEventArgs>? Activated;

        public Task ShowAsync(Reminder reminder, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Unavailable ? throw new NotifierUnavailableException("no") : Task.CompletedTask;
        }

        public void Raise(Reminder reminder, NotificationAction action) => Activated?.Invoke(this, new NotificationActivatedEventArgs(reminder, action));
    }
}

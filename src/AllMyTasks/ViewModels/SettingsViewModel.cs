using System.Globalization;
using AllMyTasks.Core.Autostart;
using AllMyTasks.Core.Settings;
using AllMyTasks.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AllMyTasks.ViewModels;

/// <summary>One choice in the week start drop-down.</summary>
public sealed record WeekStartOption(WeekStartSetting Value, string Label);

/// <summary>One choice in a drop-down of minutes.</summary>
public sealed record MinutesOption(int Minutes, string Label);

/// <summary>One choice in the time-of-day drop-down.</summary>
public sealed record TimeOption(TimeOnly Value, string Label);

/// <summary>The Settings dialog. Changes are applied and saved as they are made.</summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly AutostartManager _autostart;
    // True while the properties are being set from stored settings or reverted, so setting them does not touch the OS.
    private bool _applying;

    public SettingsViewModel(SettingsService settings, AutostartManager autostart)
    {
        _settings = settings;
        _autostart = autostart;
        var system = WeekStart.Resolve(WeekStartSetting.System, CultureInfo.CurrentCulture);
        WeekStartOptions =
        [
            new(WeekStartSetting.System, $"System default ({system})"),
            new(WeekStartSetting.Sunday, "Sunday"),
            new(WeekStartSetting.Monday, "Monday"),
        ];
        SelectedWeekStart = WeekStartOptions.First(o => o.Value == settings.Current.WeekStart);
        CloseToTray = settings.Current.CloseToTray;
        _applying = true;
        LaunchAtLogin = settings.Current.LaunchAtLogin && autostart.IsSupported;
        StartInTrayAtLogin = settings.Current.StartInTrayAtLogin;
        _applying = false;
        AutostartMessage = autostart.UnsupportedReason;

        LeadOptions = MinutesChoices([0, 5, 10, 15, 30, 60], settings.Current.EventReminderMinutes, minutes => minutes == 0 ? "At the start" : $"{Minutes(minutes)} before");
        SnoozeOptions = MinutesChoices([5, 10, 15, 30, 60], settings.Current.SnoozeMinutes, Minutes);
        TaskTimeOptions = TimeChoices(settings.Current.DateOnlyTaskReminderTime);
        RemindersEnabled = settings.Current.RemindersEnabled;
        RemindAboutTasks = settings.Current.RemindAboutTasks;
        SelectedLead = LeadOptions.First(o => o.Minutes == settings.Current.EventReminderMinutes);
        SelectedSnooze = SnoozeOptions.First(o => o.Minutes == settings.Current.SnoozeMinutes);
        SelectedTaskTime = TaskTimeOptions.First(o => o.Value == settings.Current.DateOnlyTaskReminderTime);
    }

    public IReadOnlyList<MinutesOption> LeadOptions { get; }

    public IReadOnlyList<MinutesOption> SnoozeOptions { get; }

    public IReadOnlyList<TimeOption> TaskTimeOptions { get; }

    [ObservableProperty]
    public partial bool RemindersEnabled { get; set; }

    partial void OnRemindersEnabledChanged(bool value) =>
        _settings.Update(s => s with { RemindersEnabled = value });

    [ObservableProperty]
    public partial MinutesOption SelectedLead { get; set; }

    partial void OnSelectedLeadChanged(MinutesOption value) =>
        _settings.Update(s => s with { EventReminderMinutes = value.Minutes });

    [ObservableProperty]
    public partial bool RemindAboutTasks { get; set; }

    partial void OnRemindAboutTasksChanged(bool value) =>
        _settings.Update(s => s with { RemindAboutTasks = value });

    [ObservableProperty]
    public partial TimeOption SelectedTaskTime { get; set; }

    partial void OnSelectedTaskTimeChanged(TimeOption value) =>
        _settings.Update(s => s with { DateOnlyTaskReminderTime = value.Value });

    [ObservableProperty]
    public partial MinutesOption SelectedSnooze { get; set; }

    partial void OnSelectedSnoozeChanged(MinutesOption value) =>
        _settings.Update(s => s with { SnoozeMinutes = value.Minutes });

    private static string Minutes(int minutes) =>
        minutes == 60 ? "1 hour" : minutes % 60 == 0 && minutes > 60 ? $"{minutes / 60} hours" : $"{minutes} minutes";

    /// <summary>The standard choices, plus the stored value if it is not one of them (for example from a hand-edited file).</summary>
    private static List<MinutesOption> MinutesChoices(int[] standard, int current, Func<int, string> label) =>
        [.. standard.Append(current).Distinct().Order().Select(minutes => new MinutesOption(minutes, label(minutes)))];

    private static List<TimeOption> TimeChoices(TimeOnly current)
    {
        var times = Enumerable.Range(0, 48).Select(i => new TimeOnly(i / 2, i % 2 * 30)).Append(current).Distinct().Order();
        return [.. times.Select(t => new TimeOption(t, t.ToString("t", CultureInfo.CurrentCulture)))];
    }

    public IReadOnlyList<WeekStartOption> WeekStartOptions { get; }

    [ObservableProperty]
    public partial WeekStartOption SelectedWeekStart { get; set; }

    partial void OnSelectedWeekStartChanged(WeekStartOption value) =>
        _settings.Update(s => s with { WeekStart = value.Value });

    [ObservableProperty]
    public partial bool CloseToTray { get; set; }

    partial void OnCloseToTrayChanged(bool value) =>
        _settings.Update(s => s with { CloseToTray = value });

    public bool CanChangeLaunchAtLogin => _autostart.IsSupported;

    [ObservableProperty]
    public partial bool LaunchAtLogin { get; set; }

    /// <summary>Only meaningful, and only editable, while launching at login is on.</summary>
    [ObservableProperty]
    public partial bool StartInTrayAtLogin { get; set; }

    /// <summary>Why launching at login is unavailable, or what went wrong the last time it was changed.</summary>
    [ObservableProperty]
    public partial string? AutostartMessage { get; set; }

    partial void OnLaunchAtLoginChanged(bool value)
    {
        if (_applying)
        {
            return;
        }

        var error = _autostart.Apply(value, StartInTrayAtLogin);
        if (error is not null)
        {
            // The OS refused: show the previous state again and say why.
            _applying = true;
            LaunchAtLogin = !value;
            _applying = false;
            AutostartMessage = error;
            return;
        }

        AutostartMessage = _autostart.UnsupportedReason;
        _settings.Update(s => s with { LaunchAtLogin = value });
    }

    partial void OnStartInTrayAtLoginChanged(bool value)
    {
        if (_applying)
        {
            return;
        }

        if (LaunchAtLogin)
        {
            var error = _autostart.Apply(true, value);
            if (error is not null)
            {
                AutostartMessage = error;
                return;
            }
        }

        _settings.Update(s => s with { StartInTrayAtLogin = value });
    }
}

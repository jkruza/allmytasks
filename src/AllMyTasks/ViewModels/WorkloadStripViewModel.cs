using System.Globalization;
using AllMyTasks.Core.Views;

namespace AllMyTasks.ViewModels;

/// <summary>One day in the workload strip.</summary>
public sealed class WorkloadDayItem
{
    /// <summary>Largest meeting time the bar can show; anything longer fills it.</summary>
    private const double _barHours = 10;

    public WorkloadDayItem(DayLoad load, bool isToday, Action<DateOnly> open)
    {
        Date = load.Date;
        IsHeavy = load.IsHeavy;
        DayName = isToday ? "Today" : load.Date.ToString("ddd", CultureInfo.CurrentCulture);
        DateText = load.Date.ToString("d MMM", CultureInfo.CurrentCulture);
        MeetingText = HoursText(load.MeetingTime);
        TasksText = load.TasksDue == 0 ? "no tasks" : load.TasksDue == 1 ? "1 task" : $"{load.TasksDue} tasks";
        BarHours = Math.Min(load.MeetingTime.TotalHours, _barHours);
        Spoken = $"{load.Date.ToString("dddd d MMMM", CultureInfo.CurrentCulture)}: {(load.MeetingTime == TimeSpan.Zero ? "no meetings" : MeetingText + " of meetings")}, {TasksText} due{(load.IsHeavy ? ", a heavy day" : string.Empty)}. Opens the day.";
        OpenCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() => open(Date));
    }

    public DateOnly Date { get; }

    public bool IsHeavy { get; }

    public string DayName { get; }

    public string DateText { get; }

    public string MeetingText { get; }

    public string TasksText { get; }

    public double BarHours { get; }

    public double BarMaximum { get; } = _barHours;

    public string Spoken { get; }

    public System.Windows.Input.ICommand OpenCommand { get; }

    /// <summary>"5h 30m", "45m", "2h", "0h".</summary>
    internal static string HoursText(TimeSpan time)
    {
        var minutes = (int)Math.Round(time.TotalMinutes);
        if (minutes <= 0)
        {
            return "0h";
        }

        var (h, m) = (minutes / 60, minutes % 60);
        return h == 0 ? $"{m}m" : m == 0 ? $"{h}h" : $"{h}h {m}m";
    }
}

/// <summary>The seven-day strip at the top of Upcoming: meeting time and tasks due per day, plus the overdue count.</summary>
public sealed class WorkloadStripViewModel
{
    public WorkloadStripViewModel(WorkloadSummary summary, DateOnly today, Action<DateOnly> openDay, Action openOverdue)
    {
        Days = [.. summary.Days.Select(d => new WorkloadDayItem(d, d.Date == today, openDay))];
        Overdue = summary.Overdue;
        OverdueText = summary.Overdue == 1 ? "1 overdue" : $"{summary.Overdue} overdue";
        OverdueSpoken = $"{OverdueText}. Opens All tasks filtered to overdue.";
        OpenOverdueCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(openOverdue);
    }

    public IReadOnlyList<WorkloadDayItem> Days { get; }

    public int Overdue { get; }

    public bool HasOverdue => Overdue > 0;

    public string OverdueText { get; }

    public string OverdueSpoken { get; }

    public System.Windows.Input.ICommand OpenOverdueCommand { get; }
}

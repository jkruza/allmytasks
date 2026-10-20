using AllMyTasks.Core.Model;
using AllMyTasks.Core.Tasks;
using AllMyTasks.Core.Time;

namespace AllMyTasks.Core.Views;

public enum TaskStatusFilter
{
    Open,
    Completed,
    Cancelled,
    All,
}

public enum DueOption
{
    Overdue,
    Today,
    Next7Days,
    ThisMonth,
    NoDueDate,
    CustomRange,
}

/// <summary>Identifies a collection across sources.</summary>
public sealed record CollectionRef(string SourceId, string CollectionId);

/// <summary>
/// Which due values to accept. An empty option set means "any". Several options are OR-ed.
/// </summary>
/// <param name="Options">Chosen options.</param>
/// <param name="From">Start of the custom range (inclusive), used with <see cref="DueOption.CustomRange"/>.</param>
/// <param name="To">End of the custom range (inclusive), used with <see cref="DueOption.CustomRange"/>.</param>
public sealed record DueFilter(IReadOnlySet<DueOption> Options, DateOnly? From = null, DateOnly? To = null)
{
    public static DueFilter Any { get; } = new(new HashSet<DueOption>());

    public bool Matches(TaskItem task, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (Options.Count == 0)
        {
            return true;
        }

        var day = DueDay(task, zone);
        var today = now.Today(zone);
        return Options.Any(option => option switch
        {
            DueOption.Overdue => task.IsOverdue(now, zone),
            DueOption.Today => day == today,
            DueOption.Next7Days => day >= today && day < today.AddDays(7),
            DueOption.ThisMonth => day is { } d && d.Year == today.Year && d.Month == today.Month,
            DueOption.NoDueDate => task.Due is null,
            DueOption.CustomRange => day is { } d && (From is null || d >= From) && (To is null || d <= To),
            _ => false,
        });
    }

    /// <summary>The calendar day a task is due, in <paramref name="zone"/>, or null without a due value.</summary>
    internal static DateOnly? DueDay(TaskItem task, TimeZoneInfo zone) => task.Due switch
    {
        DueDate date => date.Date,
        DueDateTime at => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at.At, zone).DateTime),
        _ => null,
    };
}

/// <summary>
/// The All tasks filter. Different filters combine with AND; within one filter, choices combine with OR.
/// Empty sets mean "no restriction".
/// </summary>
public sealed record TaskFilter(
    TaskStatusFilter Status,
    DueFilter Due,
    IReadOnlySet<CollectionRef> Collections,
    IReadOnlySet<Priority> Priorities,
    IReadOnlySet<string> Labels,
    bool AllLabels,
    string? Text)
{
    /// <summary>Open tasks, any due value, everything else unrestricted.</summary>
    public static TaskFilter Default { get; } = new(
        TaskStatusFilter.Open,
        DueFilter.Any,
        new HashSet<CollectionRef>(),
        new HashSet<Priority>(),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        AllLabels: false,
        Text: null);

    /// <summary>A pure function of the task, the clock and the zone, so it runs the same in tests, the store and memory.</summary>
    public bool Matches(TaskItem task, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(task);
        return MatchesStatus(task)
            && Due.Matches(task, now, zone)
            && (Collections.Count == 0 || Collections.Contains(new CollectionRef(task.Key.SourceId, task.Key.CollectionId)))
            && (Priorities.Count == 0 || Priorities.Contains(task.Priority))
            && MatchesLabels(task)
            && MatchesText(task);
    }

    private bool MatchesStatus(TaskItem task) => Status switch
    {
        TaskStatusFilter.Open => task.IsOpen,
        TaskStatusFilter.Completed => task.Status == TaskItemStatus.Completed,
        TaskStatusFilter.Cancelled => task.Status == TaskItemStatus.Cancelled,
        _ => true,
    };

    private bool MatchesLabels(TaskItem task)
    {
        if (Labels.Count == 0)
        {
            return true;
        }

        var has = task.Labels.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return AllLabels ? Labels.All(has.Contains) : Labels.Any(has.Contains);
    }

    private bool MatchesText(TaskItem task) =>
        string.IsNullOrWhiteSpace(Text)
        || task.Title.Contains(Text.Trim(), StringComparison.CurrentCultureIgnoreCase)
        || (task.Notes?.Contains(Text.Trim(), StringComparison.CurrentCultureIgnoreCase) ?? false);
}

using System.Globalization;
using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Core.Views;

/// <summary>Wording for the task details pane, kept out of the view model so it can be tested.</summary>
public static class TaskDetailsText
{
    public static string Title(TaskItem task) => string.IsNullOrWhiteSpace(task.Title) ? "(no title)" : task.Title;

    public static string Glyph(TaskItem task) => task.Status switch
    {
        TaskItemStatus.Completed => "☑",
        TaskItemStatus.Cancelled => "☒",
        _ => "☐",
    };

    public static string Status(TaskItem task, TimeZoneInfo zone, CultureInfo culture) => task.Status switch
    {
        TaskItemStatus.Open => "Open",
        TaskItemStatus.InProgress => "In progress",
        TaskItemStatus.Cancelled => "Cancelled",
        _ => task.CompletedAt is { } at ? $"Completed {DateTime(at, zone, culture)}" : "Completed",
    };

    /// <summary>The due value, or an empty string for an undated task.</summary>
    public static string Due(TaskDue? due, TimeZoneInfo zone, CultureInfo culture) => due switch
    {
        DueDate d => d.Date.ToString("ddd d MMM yyyy", culture),
        DueDateTime at => DateTime(at.At, zone, culture),
        _ => string.Empty,
    };

    public static string Start(DateTimeOffset? start, TimeZoneInfo zone, CultureInfo culture) =>
        start is { } value ? DateTime(value, zone, culture) : string.Empty;

    /// <summary>The priority name, or an empty string when there is none.</summary>
    public static string Priority(Priority priority) => priority == Tasks.Priority.None ? string.Empty : priority.ToString();

    public static string LastModified(DateTimeOffset? modified, TimeZoneInfo zone, CultureInfo culture) =>
        modified is { } value ? DateTime(value, zone, culture) : string.Empty;

    public static string DateTime(DateTimeOffset value, TimeZoneInfo zone, CultureInfo culture)
    {
        var local = TimeZoneInfo.ConvertTime(value, zone);
        return local.ToString("ddd d MMM yyyy", culture) + ", " + local.ToString("t", culture);
    }
}

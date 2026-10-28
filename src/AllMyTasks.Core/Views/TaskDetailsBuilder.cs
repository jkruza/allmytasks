using System.Globalization;
using AllMyTasks.Core.Changes;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Core.Views;

/// <summary>A task named in the details pane (the parent or a subtask) that the user can open.</summary>
public sealed record TaskRef(ItemKey Key, string Title, string Glyph);

/// <summary>Everything the details pane shows for one task, already formatted for display.</summary>
/// <param name="Key">The task's key, so the pane can be refreshed.</param>
/// <param name="Title">Never empty; a placeholder replaces an empty title.</param>
/// <param name="Glyph">Status glyph: open, completed or cancelled.</param>
/// <param name="AccentColor">List colour as <c>#RRGGBB</c>.</param>
/// <param name="IsOverdue">Open and past due.</param>
/// <param name="Rows">Rows that have a value, in display order.</param>
/// <param name="Link">The task's link when it is safe to open (http or https), otherwise null.</param>
/// <param name="Notes">Plain text, capped at <see cref="TaskDetailsBuilder.MaxNotesLength"/> characters.</param>
/// <param name="Parent">The parent task, if it is cached.</param>
/// <param name="Subtasks">Direct subtasks in display order.</param>
/// <param name="IsCompleted">Whether the task shows as completed (including a pending change).</param>
/// <param name="CanChange">Whether the pane offers to complete or reopen the task.</param>
/// <param name="Pending">The change that has not reached the source yet, if any.</param>
/// <param name="IsRecurring">Whether completing the task only moves it to its next occurrence.</param>
public sealed record TaskDetailsContent(
    ItemKey Key,
    string Title,
    string Glyph,
    string AccentColor,
    bool IsOverdue,
    IReadOnlyList<DetailRow> Rows,
    Uri? Link,
    string? Notes,
    TaskRef? Parent,
    IReadOnlyList<TaskRef> Subtasks,
    bool IsCompleted = false,
    bool CanChange = false,
    PendingChange? Pending = null,
    bool IsRecurring = false);

/// <summary>Builds the text of the task details pane. Pure, so it is tested without a UI.</summary>
public static class TaskDetailsBuilder
{
    /// <summary>Longest notes shown. Provider values can be very large and would stall the UI.</summary>
    public const int MaxNotesLength = 20_000;

    public static TaskDetailsContent Create(TaskDetails details, TimeZoneInfo zone, CultureInfo? culture = null)
    {
        Guard.NotNull(details);
        Guard.NotNull(zone);
        culture ??= CultureInfo.CurrentCulture;
        var task = details.Task;
        var rows = new List<DetailRow>();
        Add(rows, "Status", StatusText(task, details.IsOverdue, zone, culture));
        Add(rows, "Due", task.Due switch
        {
            DueDate d => d.Date.ToString("ddd d MMM yyyy", culture),
            DueDateTime at => DateTimeText(TimeZoneInfo.ConvertTime(at.At, zone), culture),
            _ => null,
        });
        Add(rows, "Starts", task.Start is { } start ? DateTimeText(TimeZoneInfo.ConvertTime(start, zone), culture) : null);
        Add(rows, "Priority", task.Priority == Priority.None ? null : task.Priority.ToString());
        Add(rows, "List", ListText(details.CollectionName, details.SourceName));
        Add(rows, "Labels", string.Join(", ", task.Labels));
        Add(rows, "Repeats", task.Recurrence is { } recurrence ? RecurrenceDescriber.Describe(recurrence, culture) : null);
        Add(rows, "Last changed", task.LastModified is { } modified ? DateTimeText(TimeZoneInfo.ConvertTime(modified, zone), culture) : null);

        return new TaskDetailsContent(
            task.Key,
            Title(task),
            Glyph(task),
            ColorUtil.OrDefault(details.CollectionColor),
            details.IsOverdue,
            rows,
            SafeLink(task.Url),
            Cap(task.Notes),
            details.Parent is { } parent ? Ref(parent) : null,
            [.. details.Subtasks.Select(Ref)],
            task.IsCompleted,
            details.CanChange,
            details.Pending,
            task.Recurrence is not null);
    }

    private static TaskRef Ref(TaskItem task) => new(task.Key, Title(task), Glyph(task));

    private static string Title(TaskItem task) => string.IsNullOrWhiteSpace(task.Title) ? "(no title)" : task.Title;

    private static string Glyph(TaskItem task) => task.Status switch
    {
        TaskItemStatus.Completed => "☑",
        TaskItemStatus.Cancelled => "☒",
        _ => "☐",
    };

    private static string StatusText(TaskItem task, bool overdue, TimeZoneInfo zone, CultureInfo culture) => task.Status switch
    {
        TaskItemStatus.InProgress => overdue ? "In progress, overdue" : "In progress",
        TaskItemStatus.Cancelled => "Cancelled",
        TaskItemStatus.Completed => task.CompletedAt is { } done
            ? $"Completed {DateTimeText(TimeZoneInfo.ConvertTime(done, zone), culture)}"
            : "Completed",
        _ => overdue ? "Open, overdue" : "Open",
    };

    private static string ListText(string collection, string source) =>
        string.IsNullOrWhiteSpace(source) || string.Equals(source, collection, StringComparison.Ordinal)
            ? collection
            : string.IsNullOrWhiteSpace(collection) ? source : $"{collection}, from {source}";

    // "t" only means short time as a standalone format, so date and time are formatted separately.
    private static string DateTimeText(DateTimeOffset value, CultureInfo culture) =>
        $"{value.ToString("ddd d MMM yyyy", culture)}, {value.ToString("t", culture)}";

    private static void Add(List<DetailRow> rows, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            rows.Add(new DetailRow(label, value));
        }
    }

    private static Uri? SafeLink(Uri? url) =>
        url is { IsAbsoluteUri: true } && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps) ? url : null;

    private static string? Cap(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return text.Length <= MaxNotesLength ? text : text[..MaxNotesLength] + "…";
    }
}

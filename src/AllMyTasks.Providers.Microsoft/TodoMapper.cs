using System.Globalization;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Providers.Microsoft;

/// <summary>Maps Microsoft To Do lists and tasks to the app's model. Pure: no I/O.</summary>
internal sealed class TodoMapper(string sourceId, TimeZoneInfo displayZone)
{
    internal const string TaskUrlPrefix = "https://to-do.live.com/tasks/id/";

    public CollectionInfo? MapList(TodoListDto list) =>
        list.Id is { Length: > 0 } id
            ? new CollectionInfo(sourceId, id, list.DisplayName ?? string.Empty, CollectionKind.TaskList, Color: null, IsVisible: true, IsReadOnly: true)
            : null;

    /// <summary>Maps one task and its checklist items (as subtasks). Returns an empty list when the task cannot be read.</summary>
    public List<TaskItem> MapTask(string listId, TodoTaskDto dto, List<SyncWarning> warnings)
    {
        if (string.IsNullOrEmpty(dto.Id))
        {
            return [];
        }

        var key = new ItemKey(sourceId, listId, dto.Id);
        if (!TryDue(dto.DueDateTime, out var due, out var dueProblem) || !TryStart(dto.StartDateTime, out var start, out var startProblem))
        {
            warnings.Add(new SyncWarning(dto.Id, "A task has a date that could not be read and was skipped."));
            return [];
        }

        if (dueProblem || startProblem)
        {
            warnings.Add(new SyncWarning(dto.Id, "A task has a time zone this version does not know; UTC was assumed."));
        }

        var status = MapStatus(dto.Status);
        if (status is null)
        {
            warnings.Add(new SyncWarning(dto.Id, "A task has a status this version does not know; it is shown as open."));
        }

        Recurrence? recurrence = null;
        if (dto.Recurrence is not null && (recurrence = RecurrenceMapper.Map(dto.Recurrence)) is null)
        {
            warnings.Add(new SyncWarning(dto.Id, "A task repeats in a way this version does not know; the repeat was left out."));
        }

        var task = new TaskItem(
            key,
            dto.Title ?? string.Empty,
            NotesOf(dto.Body),
            due,
            start,
            status ?? TaskItemStatus.Open,
            CompletedAt: CompletedAt(dto.CompletedDateTime),
            Priority: dto.Importance?.ToLowerInvariant() switch { "high" => Priority.High, "low" => Priority.Low, _ => Priority.None },
            Labels: dto.Categories,
            Recurrence: recurrence,
            Url: new Uri(TaskUrlPrefix + Uri.EscapeDataString(dto.Id) + "/details"),
            LastModified: Text.ParseInstant(dto.LastModifiedDateTime));

        var result = new List<TaskItem> { task };
        foreach (var item in dto.ChecklistItems ?? [])
        {
            if (!string.IsNullOrEmpty(item.Id))
            {
                result.Add(new TaskItem(
                    key with { ItemId = ChecklistId(dto.Id, item.Id) },
                    item.DisplayName ?? string.Empty,
                    Status: item.IsChecked ? TaskItemStatus.Completed : TaskItemStatus.Open,
                    CompletedAt: item.IsChecked ? Text.ParseInstant(item.CheckedDateTime) : null,
                    ParentKey: key));
            }
        }

        return result;
    }

    /// <summary>Checklist item ids are made unique by prefixing the task id.</summary>
    internal static string ChecklistId(string taskId, string itemId) => $"{taskId}/{itemId}";

    /// <summary><c>null</c> for a status this version does not know.</summary>
    internal static TaskItemStatus? MapStatus(string? status) => status?.ToLowerInvariant() switch
    {
        "notstarted" or "deferred" => TaskItemStatus.Open,
        "inprogress" or "waitingonothers" => TaskItemStatus.InProgress,
        "completed" => TaskItemStatus.Completed,
        _ => null,
    };

    private static string? NotesOf(BodyDto? body)
    {
        if (string.IsNullOrWhiteSpace(body?.Content))
        {
            return null;
        }

        return string.Equals(body.ContentType, "html", StringComparison.OrdinalIgnoreCase) ? Text.HtmlToPlain(body.Content) : body.Content;
    }

    private static DateTimeOffset? CompletedAt(DateTimeZoneDto? value) =>
        CalendarMapper.TryInstant(value, out var instant) ? instant : null;

    /// <summary>Midnight is how To Do stores a date with no time; any other time is a moment in its own zone.</summary>
    private static bool TryDue(DateTimeZoneDto? value, out TaskDue? due, out bool unknownZone)
    {
        due = null;
        unknownZone = false;
        if (value?.DateTime is null)
        {
            return true;
        }

        if (Text.ParseWallClock(value.DateTime) is not { } wall)
        {
            return false;
        }

        if (wall.TimeOfDay == TimeSpan.Zero)
        {
            due = new DueDate(DateOnly.FromDateTime(wall));
            return true;
        }

        var zone = Zones.Resolve(value.TimeZone);
        unknownZone = zone is null;
        var (info, iana) = zone ?? (TimeZoneInfo.Utc, "UTC");
        due = new DueDateTime(Zones.ToInstant(wall, info), iana);
        return true;
    }

    private bool TryStart(DateTimeZoneDto? value, out DateTimeOffset? start, out bool unknownZone)
    {
        start = null;
        unknownZone = false;
        if (value?.DateTime is null)
        {
            return true;
        }

        if (Text.ParseWallClock(value.DateTime) is not { } wall)
        {
            return false;
        }

        if (wall.TimeOfDay == TimeSpan.Zero)
        {
            start = Zones.ToInstant(wall, displayZone);
            return true;
        }

        var zone = Zones.Resolve(value.TimeZone);
        unknownZone = zone is null;
        start = Zones.ToInstant(wall, zone?.Zone ?? TimeZoneInfo.Utc);
        return true;
    }
}

/// <summary>Maps To Do's patterned recurrence to an RFC 5545 rule.</summary>
internal static class RecurrenceMapper
{
    private static readonly Dictionary<string, string> Days = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sunday"] = "SU",
        ["monday"] = "MO",
        ["tuesday"] = "TU",
        ["wednesday"] = "WE",
        ["thursday"] = "TH",
        ["friday"] = "FR",
        ["saturday"] = "SA",
    };

    private static readonly Dictionary<string, string> Positions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["first"] = "1",
        ["second"] = "2",
        ["third"] = "3",
        ["fourth"] = "4",
        ["last"] = "-1",
    };

    /// <summary>The rule, or null when the pattern is not one this version knows.</summary>
    public static Recurrence? Map(PatternedRecurrenceDto dto)
    {
        if (dto.Pattern is not { } pattern)
        {
            return null;
        }

        var interval = Math.Max(pattern.Interval, 1).ToString(CultureInfo.InvariantCulture);
        var days = string.Join(',', (pattern.DaysOfWeek ?? []).Select(d => Days.GetValueOrDefault(d ?? string.Empty)).Where(d => d is not null));
        var month = pattern.Month.ToString(CultureInfo.InvariantCulture);
        var dayOfMonth = pattern.DayOfMonth.ToString(CultureInfo.InvariantCulture);
        var position = Positions.GetValueOrDefault(pattern.Index ?? string.Empty);

        var parts = pattern.Type?.ToLowerInvariant() switch
        {
            "daily" => new List<string> { "FREQ=DAILY", $"INTERVAL={interval}" },
            "weekly" when days.Length > 0 => ["FREQ=WEEKLY", $"INTERVAL={interval}", $"BYDAY={days}", .. WeekStart(pattern)],
            "absolutemonthly" when pattern.DayOfMonth > 0 => ["FREQ=MONTHLY", $"INTERVAL={interval}", $"BYMONTHDAY={dayOfMonth}"],
            "relativemonthly" when days.Length > 0 && position is not null => ["FREQ=MONTHLY", $"INTERVAL={interval}", $"BYDAY={days}", $"BYSETPOS={position}"],
            "absoluteyearly" when pattern.Month > 0 && pattern.DayOfMonth > 0 => ["FREQ=YEARLY", $"INTERVAL={interval}", $"BYMONTH={month}", $"BYMONTHDAY={dayOfMonth}"],
            "relativeyearly" when pattern.Month > 0 && days.Length > 0 && position is not null => ["FREQ=YEARLY", $"INTERVAL={interval}", $"BYMONTH={month}", $"BYDAY={days}", $"BYSETPOS={position}"],
            _ => null,
        };
        if (parts is null)
        {
            return null;
        }

        switch (dto.Range?.Type?.ToLowerInvariant())
        {
            case "enddate" when DateOnly.TryParse(dto.Range.EndDate, CultureInfo.InvariantCulture, out var until):
                parts.Add($"UNTIL={until:yyyyMMdd}");
                break;
            case "numbered" when dto.Range.NumberOfOccurrences > 0:
                parts.Add($"COUNT={dto.Range.NumberOfOccurrences.ToString(CultureInfo.InvariantCulture)}");
                break;
        }

        return new Recurrence(string.Join(';', parts));
    }

    private static IEnumerable<string> WeekStart(RecurrencePatternDto pattern) =>
        Days.TryGetValue(pattern.FirstDayOfWeek ?? string.Empty, out var day) ? [$"WKST={day}"] : [];
}

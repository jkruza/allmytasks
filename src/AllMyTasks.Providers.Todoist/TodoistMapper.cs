using System.Globalization;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Providers.Todoist;

/// <summary>Maps Sync API objects to the app's model. Pure: no I/O.</summary>
internal sealed class TodoistMapper(string sourceId, TimeZoneInfo displayZone)
{
    internal const string TaskUrlPrefix = "https://app.todoist.com/app/task/";

    private static readonly Dictionary<string, string> Palette = new(StringComparer.OrdinalIgnoreCase)
    {
        ["berry_red"] = "#b8255f",
        ["red"] = "#db4035",
        ["orange"] = "#ff9933",
        ["yellow"] = "#fad000",
        ["olive_green"] = "#afb83b",
        ["lime_green"] = "#7ecc49",
        ["green"] = "#299438",
        ["mint_green"] = "#6accbc",
        ["teal"] = "#158fad",
        ["sky_blue"] = "#14aaf5",
        ["light_blue"] = "#96c3eb",
        ["blue"] = "#4073ff",
        ["grape"] = "#884dff",
        ["violet"] = "#af38eb",
        ["lavender"] = "#eb96eb",
        ["magenta"] = "#e05194",
        ["salmon"] = "#ff8d85",
        ["charcoal"] = "#808080",
        ["grey"] = "#b8b8b8",
        ["taupe"] = "#ccac93",
    };

    /// <summary>Todoist's palette colour name as <c>#RRGGBB</c>, or null for a name this version does not know.</summary>
    public static string? ColorFor(string? name) =>
        name is not null && Palette.TryGetValue(name, out var hex) ? hex : null;

    public CollectionInfo? MapProject(ProjectDto project) =>
        project is { Id: { Length: > 0 } id, IsArchived: false, IsDeleted: false }
            ? new CollectionInfo(sourceId, id, project.Name ?? string.Empty, CollectionKind.TaskList, ColorFor(project.Color), IsVisible: true, IsReadOnly: true)
            : null;

    /// <summary>Maps one item, or returns null when it cannot be mapped (no id, no project, unreadable due value).</summary>
    public TaskItem? MapItem(ItemDto item)
    {
        if (string.IsNullOrEmpty(item.Id) || string.IsNullOrEmpty(item.ProjectId))
        {
            return null;
        }

        if (!TryMapDue(item.Due, out var due))
        {
            return null;
        }

        var completedAt = ParseInstant(item.CompletedAt);
        var isCompleted = item.Checked || completedAt is not null;
        var parent = string.IsNullOrEmpty(item.ParentId) || item.ParentId == item.Id
            ? null
            : new ItemKey(sourceId, item.ProjectId, item.ParentId);
        return new TaskItem(
            new ItemKey(sourceId, item.ProjectId, item.Id),
            item.Content ?? string.Empty,
            string.IsNullOrEmpty(item.Description) ? null : item.Description,
            due,
            Status: isCompleted ? TaskItemStatus.Completed : TaskItemStatus.Open,
            CompletedAt: isCompleted ? completedAt : null,
            Priority: MapPriority(item.Priority),
            Labels: item.Labels,
            ParentKey: parent,
            Url: new Uri(TaskUrlPrefix + Uri.EscapeDataString(item.Id)),
            LastModified: ParseInstant(item.UpdatedAt));
    }

    /// <summary>The API value is inverted from the app: 4 is the red "P1".</summary>
    internal static Priority MapPriority(int apiPriority) => apiPriority switch
    {
        4 => Priority.High,
        3 => Priority.Medium,
        2 => Priority.Low,
        _ => Priority.None,
    };

    private bool TryMapDue(DueDto? due, out TaskDue? result)
    {
        result = null;
        if (due?.Date is not { Length: > 0 } text)
        {
            return true;
        }

        if (text.Length == 10 && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            result = new DueDate(date);
            return true;
        }

        if (text.EndsWith('Z') && ParseInstant(text) is { } instant)
        {
            result = new DueDateTime(instant, string.IsNullOrEmpty(due.Timezone) ? null : due.Timezone);
            return true;
        }

        // A floating time has no zone: keep the wall-clock time and read it in the display zone.
        if (!text.EndsWith('Z') && DateTime.TryParseExact(text, ["yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall))
        {
            result = new DueDateTime(new DateTimeOffset(wall, displayZone.GetUtcOffset(wall)));
            return true;
        }

        return false;
    }

    private static DateTimeOffset? ParseInstant(string? text) =>
        !string.IsNullOrEmpty(text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant)
            ? instant
            : null;
}

using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Providers.Google;

/// <summary>Maps Google Tasks lists and tasks to the app's model. Pure: no I/O.</summary>
internal sealed class TaskMapper(string sourceId)
{
    public CollectionInfo? MapList(TaskListDto list) =>
        list.Id is { Length: > 0 } id
            ? new CollectionInfo(sourceId, id, list.Title ?? string.Empty, CollectionKind.TaskList, Color: null, IsVisible: true, IsReadOnly: true)
            : null;

    /// <summary>Maps one task. Returns null (with a warning) when it cannot be read.</summary>
    public TaskItem? MapTask(string listId, TaskDto dto, List<SyncWarning> warnings)
    {
        if (string.IsNullOrEmpty(dto.Id))
        {
            return null;
        }

        var key = new ItemKey(sourceId, listId, dto.Id);
        TaskDue? due = null;
        if (!string.IsNullOrEmpty(dto.Due))
        {
            if (Text.ParseInstant(dto.Due) is not { } dueInstant)
            {
                warnings.Add(new SyncWarning(dto.Id, "A task has a due date that could not be read and was skipped."));
                return null;
            }

            // Google stores a date and sends it as midnight UTC. Converting it to a local zone would move "due Friday" to Thursday.
            due = new DueDate(DateOnly.FromDateTime(dueInstant.UtcDateTime));
        }

        var status = dto.Status switch
        {
            "needsAction" => TaskItemStatus.Open,
            "completed" => TaskItemStatus.Completed,
            _ => (TaskItemStatus?)null,
        };
        if (status is null)
        {
            warnings.Add(new SyncWarning(dto.Id, "A task has a status this version does not know; it is shown as open."));
        }

        var completedAt = Text.ParseInstant(dto.Completed);
        if (status == TaskItemStatus.Open && completedAt is not null)
        {
            warnings.Add(new SyncWarning(dto.Id, "A task is open but has a completion time; the time was ignored."));
            completedAt = null;
        }

        ItemKey? parent = null;
        if (!string.IsNullOrEmpty(dto.Parent))
        {
            if (dto.Parent == dto.Id)
            {
                warnings.Add(new SyncWarning(dto.Id, "A task is its own parent; the parent was ignored."));
            }
            else
            {
                parent = key with { ItemId = dto.Parent };
            }
        }

        return new TaskItem(
            key,
            dto.Title ?? string.Empty,
            string.IsNullOrEmpty(dto.Notes) ? null : dto.Notes,
            due,
            Status: status ?? TaskItemStatus.Open,
            CompletedAt: completedAt,
            ParentKey: parent,
            Url: Text.HttpsUri(dto.WebViewLink),
            LastModified: Text.ParseInstant(dto.Updated));
    }
}

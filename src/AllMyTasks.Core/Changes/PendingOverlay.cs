using AllMyTasks.Core.Model;
using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Core.Changes;

/// <summary>
/// Shows cached tasks as the user left them: pending changes are applied on top of what the last sync brought, so a
/// sync that returns before a change was sent does not flip a checkbox back.
/// </summary>
public static class PendingOverlay
{
    public static IReadOnlyList<TaskItem> Apply(IReadOnlyList<TaskItem> tasks, IReadOnlyList<PendingChange> changes)
    {
        Guard.NotNull(tasks);
        Guard.NotNull(changes);
        if (changes.Count == 0 || tasks.Count == 0)
        {
            return tasks;
        }

        // The newest change per task wins. The service keeps at most one, so this only guards against a stale queue.
        var latest = changes.GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.MaxBy(c => c.CreatedAt)!);
        var children = tasks.Where(t => t.ParentKey is not null).ToLookup(t => t.ParentKey!);
        var completed = new HashSet<ItemKey>();
        var reopened = new HashSet<ItemKey>();
        foreach (var change in latest.Values)
        {
            if (change.Kind == PendingKind.Reopen)
            {
                reopened.Add(change.Key);
                continue;
            }

            // Completing a parent completes its subtasks, as the source does.
            var pending = new Stack<ItemKey>([change.Key]);
            while (pending.Count > 0)
            {
                var key = pending.Pop();
                if (completed.Add(key))
                {
                    foreach (var child in children[key])
                    {
                        pending.Push(child.Key);
                    }
                }
            }
        }

        return
        [
            .. tasks.Select(t => t switch
            {
                _ when reopened.Contains(t.Key) && t.IsCompleted => t with { Status = TaskItemStatus.Open, CompletedAt = null },
                _ when completed.Contains(t.Key) && t.IsOpen => t with { Status = TaskItemStatus.Completed },
                _ => t,
            }),
        ];
    }
}

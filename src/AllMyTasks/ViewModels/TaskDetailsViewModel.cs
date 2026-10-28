using System.Windows.Input;
using AllMyTasks.Core.Changes;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Views;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;

namespace AllMyTasks.ViewModels;

/// <summary>What the details pane can do to a task: complete or reopen it, and retry or discard a change that failed.</summary>
public sealed record TaskActions(Func<ItemKey, bool, bool, string, Task> Toggle, Func<Guid, Task> Retry, Func<Guid, Task> Discard);

/// <summary>Details of one task, shown in the details pane, with a button to complete or reopen it when its source allows.</summary>
public sealed class TaskDetailsViewModel : ViewModelBase
{
    public TaskDetailsViewModel(TaskDetailsContent details, Func<ItemKey, Task> open, TaskActions? actions = null)
    {
        Key = details.Key;
        Title = details.Title;
        Glyph = details.Glyph;
        Accent = Brush.Parse(details.AccentColor);
        IsOverdue = details.IsOverdue;
        Rows = details.Rows;
        Link = details.Link;
        Notes = details.Notes;
        Parent = details.Parent is { } parent ? new TaskLink(parent, open) : null;
        Subtasks = [.. details.Subtasks.Select(t => new TaskLink(t, open))];

        CanChange = details.CanChange && actions is not null;
        ToggleLabel = details.IsCompleted ? "Mark as not done" : "Mark as done";
        ToggleCommand = new AsyncRelayCommand(() => actions?.Toggle(details.Key, !details.IsCompleted, details.IsRecurring, details.Title) ?? Task.CompletedTask);
        if (details.Pending is { } pending)
        {
            if (pending.State == PendingState.Failed)
            {
                FailedText = "Not sent to the source: " + (pending.Error ?? "the change was refused") + ".";
                RetryCommand = new AsyncRelayCommand(() => actions?.Retry(pending.Id) ?? Task.CompletedTask);
                DiscardCommand = new AsyncRelayCommand(() => actions?.Discard(pending.Id) ?? Task.CompletedTask);
            }
            else
            {
                WaitingText = "Waiting to be sent to the source.";
            }
        }
    }

    public ItemKey Key { get; }

    public bool CanChange { get; }

    public string ToggleLabel { get; }

    public ICommand? ToggleCommand { get; }

    public string FailedText { get; } = string.Empty;

    public bool HasFailed => FailedText.Length > 0;

    public ICommand? RetryCommand { get; }

    public ICommand? DiscardCommand { get; }

    public string WaitingText { get; } = string.Empty;

    public bool IsWaiting => WaitingText.Length > 0;

    public string Title { get; }

    public string Glyph { get; }

    public IBrush Accent { get; }

    public bool IsOverdue { get; }

    public IReadOnlyList<DetailRow> Rows { get; }

    public Uri? Link { get; }

    public string? Notes { get; }

    public TaskLink? Parent { get; }

    public IReadOnlyList<TaskLink> Subtasks { get; }

    public bool HasLink => Link is not null;

    public bool HasNotes => !string.IsNullOrEmpty(Notes);

    public bool HasParent => Parent is not null;

    public bool HasSubtasks => Subtasks.Count > 0;

    public string AutomationName => $"Details for task {Title}";
}

/// <summary>A task named in the pane (parent or subtask) that opens its own details when activated.</summary>
public sealed class TaskLink(TaskRef task, Func<ItemKey, Task> open)
{
    public string Title { get; } = task.Title;

    public string Glyph { get; } = task.Glyph;

    public ICommand OpenCommand { get; } = new AsyncRelayCommand(() => open(task.Key));
}

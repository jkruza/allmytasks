using System.Windows.Input;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Views;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;

namespace AllMyTasks.ViewModels;

/// <summary>Read-only details of one task, shown in the details pane.</summary>
public sealed class TaskDetailsViewModel : ViewModelBase
{
    public TaskDetailsViewModel(TaskDetailsContent details, Func<ItemKey, Task> open)
    {
        Title = details.Title;
        Glyph = details.Glyph;
        Accent = Brush.Parse(details.AccentColor);
        IsOverdue = details.IsOverdue;
        Rows = details.Rows;
        Link = details.Link;
        Notes = details.Notes;
        Parent = details.Parent is { } parent ? new TaskLink(parent, open) : null;
        Subtasks = [.. details.Subtasks.Select(t => new TaskLink(t, open))];
    }

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

using AllMyTasks.Core.Views;
using Avalonia.Media;

namespace AllMyTasks.ViewModels;

/// <summary>Read-only details of one calendar entry, shown in the details pane.</summary>
public sealed class CalendarEntryDetailsViewModel : ViewModelBase
{
    public CalendarEntryDetailsViewModel(EntryDetails details)
    {
        Title = details.Title;
        IsTask = details.IsTask;
        Accent = Brush.Parse(details.AccentColor);
        Rows = details.Rows;
        Link = details.Link;
        Description = details.Description;
    }

    public string Title { get; }

    public bool IsTask { get; }

    public IBrush Accent { get; }

    public IReadOnlyList<DetailRow> Rows { get; }

    public Uri? Link { get; }

    public string? Description { get; }

    public bool HasLink => Link is not null;

    public bool HasDescription => !string.IsNullOrEmpty(Description);

    public string AutomationName => $"Details for {Title}";
}

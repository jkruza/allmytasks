using AllMyTasks.Core.Notes;
using AllMyTasks.Core.Views;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AllMyTasks.ViewModels;

/// <summary>An event that overlaps the one shown: its title and time, and a command that opens it.</summary>
public sealed record OverlapLine(string Title, string TimeText, System.Windows.Input.ICommand OpenCommand);

/// <summary>Read-only details of one calendar entry, shown in the details pane.</summary>
public sealed partial class CalendarEntryDetailsViewModel : ViewModelBase
{
    public CalendarEntryDetailsViewModel(
        EntryDetails details,
        IReadOnlyList<EventEntry>? overlaps = null,
        Func<AgendaEntry, Task>? open = null,
        TimeZoneInfo? zone = null)
    {
        Overlaps = [.. (overlaps ?? []).Select(o => new OverlapLine(
            string.IsNullOrWhiteSpace(o.Title) ? "(no title)" : o.Title,
            o.TimeRangeText(zone ?? TimeZoneInfo.Local),
            new RelayCommand(() => _ = open?.Invoke(o))))];
        Title = details.Title;
        IsTask = details.IsTask;
        Accent = Brush.Parse(details.AccentColor);
        Rows = details.Rows;
        Link = details.Link;
        Description = details.Description;
        DescriptionDocument = NoteFormatter.Format(details.Description);
        _details = details;
        MeetingUrl = details.MeetingUrl;
        MeetingLabel = details.MeetingLabel;
        MeetingHost = details.MeetingHost;
    }

    private readonly EntryDetails _details;

    /// <summary>The other events that overlap this one; each opens its own details.</summary>
    public IReadOnlyList<OverlapLine> Overlaps { get; }

    public bool HasOverlaps => Overlaps.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisiblePeople), nameof(CanShowAll))]
    public partial bool ShowAllPeople { get; set; }

    /// <summary>The online meeting link; always <c>https</c> (Core drops anything else). Opened only when the user clicks Join.</summary>
    public Uri? MeetingUrl { get; }

    public string? MeetingLabel { get; }

    public string? MeetingHost { get; }

    public bool HasMeeting => MeetingUrl is not null;

    public IReadOnlyList<PersonLine> VisiblePeople => _details.VisiblePeople(ShowAllPeople);

    public bool HasPeople => _details.People.Count > 0;

    public bool CanShowAll => !ShowAllPeople && _details.People.Count > EntryDetails.CollapsedCount;

    public string ShowAllText => $"Show all ({_details.People.Count})";

    public bool HasMoreAttendees => _details.MoreAttendees > 0;

    public string MoreAttendeesText => $"and {_details.MoreAttendees} more";

    [RelayCommand]
    private void ShowAll() => ShowAllPeople = true;

    public string Title { get; }

    public bool IsTask { get; }

    public IBrush Accent { get; }

    public IReadOnlyList<DetailRow> Rows { get; }

    public Uri? Link { get; }

    public string? Description { get; }

    /// <summary>The description with Markdown or HTML turned into whitelisted blocks; see formatted-notes.md.</summary>
    public NoteDocument DescriptionDocument { get; }

    public bool HasLink => Link is not null;

    /// <summary>The address, shown only as the tooltip of Open link.</summary>
    public string? LinkTooltip => Link?.AbsoluteUri;

    /// <summary>The meeting address, shown only as the tooltip of Join.</summary>
    public string? MeetingTooltip => MeetingUrl?.AbsoluteUri;

    public bool HasDescription => DescriptionDocument.Blocks.Count > 0;

    public string AutomationName => $"Details for {Title}";
}

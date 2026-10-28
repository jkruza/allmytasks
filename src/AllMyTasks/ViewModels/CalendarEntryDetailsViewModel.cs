using AllMyTasks.Core.Views;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AllMyTasks.ViewModels;

/// <summary>Read-only details of one calendar entry, shown in the details pane.</summary>
public sealed partial class CalendarEntryDetailsViewModel : ViewModelBase
{
    public CalendarEntryDetailsViewModel(EntryDetails details)
    {
        Title = details.Title;
        IsTask = details.IsTask;
        Accent = Brush.Parse(details.AccentColor);
        Rows = details.Rows;
        Link = details.Link;
        Description = details.Description;
        _details = details;
        MeetingUrl = details.MeetingUrl;
        MeetingLabel = details.MeetingLabel;
        MeetingHost = details.MeetingHost;
    }

    private readonly EntryDetails _details;

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

    public bool HasLink => Link is not null;

    public bool HasDescription => !string.IsNullOrEmpty(Description);

    public string AutomationName => $"Details for {Title}";
}

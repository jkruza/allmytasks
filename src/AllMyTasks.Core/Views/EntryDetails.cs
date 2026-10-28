namespace AllMyTasks.Core.Views;

/// <summary>One labelled line in the details pane.</summary>
public sealed record DetailRow(string Label, string Value);

/// <summary>One person in the details pane's attendee list, already formatted for display.</summary>
/// <param name="Text">Name, "You" for the account owner, or the e-mail when there is no name.</param>
/// <param name="Tooltip">The e-mail (and the name when "You" is shown), or null when there is nothing more to say.</param>
/// <param name="Glyph">One character for the answer: ✓ accepted, ? tentative, ✗ declined, … no answer yet.</param>
/// <param name="Note">"Organiser", "Optional" or "Room", or null for a required attendee.</param>
/// <param name="ResponseText">The answer in words, for screen readers.</param>
public sealed record PersonLine(string Text, string? Tooltip, string Glyph, string? Note, string ResponseText)
{
    /// <summary>Text for a screen reader, e.g. "Anna, organiser, accepted".</summary>
    public string Spoken => string.Join(", ", new[] { Text, Note?.ToLowerInvariant(), ResponseText.ToLowerInvariant() }.OfType<string>());
}

/// <summary>Everything the details pane shows for one calendar entry, already formatted for display.</summary>
/// <param name="Title">Never empty; a placeholder replaces an empty title.</param>
/// <param name="IsTask">True for dated tasks, false for events.</param>
/// <param name="AccentColor">Collection colour as <c>#RRGGBB</c>.</param>
/// <param name="Rows">Rows that have a value, in display order.</param>
/// <param name="Link">The entry's link when it is safe to open (http or https), otherwise null.</param>
/// <param name="MeetingUrl">The online meeting link; always an absolute <c>https</c> URL, otherwise null.</param>
/// <param name="MeetingLabel">The text of the join button, e.g. "Join Google Meet"; null without a meeting link.</param>
/// <param name="MeetingHost">The host the link points to, shown next to the button so a lookalike link is visible; null without a link.</param>
/// <param name="People">Organiser first, then required, optional and resources. Empty for tasks and events without people.</param>
/// <param name="MoreAttendees">Attendees the provider reported that are not in <paramref name="People"/> (over the cap).</param>
/// <param name="Description">Plain text, capped at <see cref="EntryDetailsBuilder.MaxDescriptionLength"/> characters.</param>
public sealed record EntryDetails(
    string Title,
    bool IsTask,
    string AccentColor,
    IReadOnlyList<DetailRow> Rows,
    Uri? Link,
    string? Description,
    Uri? MeetingUrl = null,
    string? MeetingLabel = null,
    string? MeetingHost = null,
    IReadOnlyList<PersonLine>? People = null,
    int MoreAttendees = 0)
{
    /// <summary>How many people the pane shows before "Show all".</summary>
    public const int CollapsedCount = 5;

    public IReadOnlyList<PersonLine> People { get; init => field = value ?? []; } = People ?? [];

    /// <summary>The people to list: the first <see cref="CollapsedCount"/> unless <paramref name="expanded"/>.</summary>
    public IReadOnlyList<PersonLine> VisiblePeople(bool expanded) =>
        expanded || People.Count <= CollapsedCount ? People : [.. People.Take(CollapsedCount)];
}

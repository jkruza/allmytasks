namespace AllMyTasks.Core.Calendar;

/// <summary>How much a person is expected at an event.</summary>
public enum AttendeeRole
{
    Required,
    Optional,

    /// <summary>A room or piece of equipment, not a person.</summary>
    Resource,
}

/// <summary>A person's answer to an invitation.</summary>
public enum ResponseStatus
{
    NeedsAction,
    Accepted,
    Tentative,
    Declined,
}

/// <summary>
/// Someone invited to an event, or its organiser. Third-party data and personal data of other people: shown as plain text,
/// never logged.
/// </summary>
/// <param name="Name">Display name; null or blank when the provider gave none.</param>
/// <param name="Email">E-mail address; null or blank when the provider gave none.</param>
/// <param name="Role">Required, optional or a resource.</param>
/// <param name="Response">The answer to the invitation.</param>
/// <param name="IsSelf">True for the account owner's own entry, when the provider says so.</param>
public sealed record Attendee(
    string? Name,
    string? Email,
    AttendeeRole Role = AttendeeRole.Required,
    ResponseStatus Response = ResponseStatus.NeedsAction,
    bool IsSelf = false)
{
    /// <summary>The most attendees kept per event. The real number is kept in <see cref="CalendarEvent.AttendeeCount"/>.</summary>
    public const int MaxPerEvent = 200;

    /// <summary>Longest name or e-mail kept. Feeds can contain very large values.</summary>
    public const int MaxTextLength = 320;

    /// <summary>The name, or the e-mail when there is no name, or null when there is neither.</summary>
    public string? DisplayText =>
        !string.IsNullOrWhiteSpace(Name) ? Name : !string.IsNullOrWhiteSpace(Email) ? Email : null;

    /// <summary>
    /// Keeps the first <see cref="MaxPerEvent"/> attendees that have a name or an e-mail, with over-long text cut.
    /// <paramref name="total"/> is how many there were before the cap.
    /// </summary>
    public static IReadOnlyList<Attendee> Cap(IEnumerable<Attendee> attendees, out int total)
    {
        Guard.NotNull(attendees);
        var all = attendees.Select(Clean).OfType<Attendee>().ToList();
        total = all.Count;
        return all.Count <= MaxPerEvent ? all : all.GetRange(0, MaxPerEvent);
    }

    /// <summary>Trims and caps the text; null when the entry has neither a name nor an e-mail.</summary>
    public static Attendee? Clean(Attendee attendee)
    {
        Guard.NotNull(attendee);
        var name = Trim(attendee.Name);
        var email = Trim(attendee.Email);
        return name is null && email is null ? null : attendee with { Name = name, Email = email };
    }

    private static string? Trim(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        return text.Length <= MaxTextLength ? text : text[..MaxTextLength];
    }
}

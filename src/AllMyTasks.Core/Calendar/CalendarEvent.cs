using AllMyTasks.Core.Model;

namespace AllMyTasks.Core.Calendar;

/// <summary>
/// An event as shown in All My Tasks, regardless of which provider it came from.
/// </summary>
/// <param name="Key">Identity. Has a <see cref="ItemKey.RecurrenceId"/> when this overrides one occurrence of a series.</param>
/// <param name="Title">Never null. An empty title is replaced by a placeholder in the view, not here.</param>
/// <param name="Time">When the event happens.</param>
/// <param name="Description">Free text.</param>
/// <param name="Location">Free text.</param>
/// <param name="Url">Link associated with the event.</param>
/// <param name="Status">Confirmed, tentative or cancelled.</param>
/// <param name="Availability">Busy or free.</param>
/// <param name="Recurrence">Null for single events.</param>
/// <param name="Categories">Provider categories; may be empty.</param>
/// <param name="LastModified">When the provider last changed it.</param>
public sealed record CalendarEvent(
    ItemKey Key,
    string Title,
    EventTime Time,
    string? Description = null,
    string? Location = null,
    Uri? Url = null,
    EventStatus Status = EventStatus.Confirmed,
    Availability Availability = Availability.Busy,
    Recurrence? Recurrence = null,
    IReadOnlyList<string>? Categories = null,
    DateTimeOffset? LastModified = null)
{
    public ItemKey Key { get; init => field = Guard.NotNull(value); } = Guard.NotNull(Key);

    public string Title { get; init => field = Guard.NotNull(value); } = Guard.NotNull(Title);

    public EventTime Time { get; init => field = Guard.NotNull(value); } = Guard.NotNull(Time);

    public IReadOnlyList<string> Categories { get; init => field = value ?? []; } = Categories ?? [];
}

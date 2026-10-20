namespace AllMyTasks.Core.Model;

/// <summary>
/// Identifies one event or task: which source, which collection in it, and the provider's own id.
/// </summary>
/// <param name="SourceId">The <see cref="Providers.ISource.Id"/> the item came from.</param>
/// <param name="CollectionId">The calendar or task list within the source.</param>
/// <param name="ItemId">The provider's id within the collection, such as an iCalendar UID.</param>
/// <param name="RecurrenceId">
/// Set only on an override of one occurrence of a recurring event: the start of the original
/// occurrence it replaces. For all-day events this is midnight UTC of the original date.
/// </param>
public sealed record ItemKey(
    string SourceId,
    string CollectionId,
    string ItemId,
    DateTimeOffset? RecurrenceId = null)
{
    public string SourceId { get; init => field = Guard.NotBlank(value); } = Guard.NotBlank(SourceId);

    public string CollectionId { get; init => field = Guard.NotBlank(value); } = Guard.NotBlank(CollectionId);

    public string ItemId { get; init => field = Guard.NotBlank(value); } = Guard.NotBlank(ItemId);

    /// <summary>True when this key names a single modified occurrence rather than a whole item.</summary>
    public bool IsOverride => RecurrenceId is not null;

    /// <summary>The key of the recurring master this override belongs to (or this key if it is not an override).</summary>
    public ItemKey Master => RecurrenceId is null ? this : this with { RecurrenceId = null };
}

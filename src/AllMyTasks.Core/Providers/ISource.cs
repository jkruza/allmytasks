using AllMyTasks.Core.Model;

namespace AllMyTasks.Core.Providers;

/// <summary>
/// One configured account or feed of some provider. Implement <see cref="ICalendarSource"/>,
/// <see cref="ITaskSource"/> or both to say what it can serve.
/// </summary>
/// <remarks>Implementations throw only <see cref="SourceException"/> subtypes (and <see cref="OperationCanceledException"/>).</remarks>
public interface ISource
{
    /// <summary>Stable identifier, unique per configured source.</summary>
    string Id { get; }

    /// <summary>Provider type, such as <c>ical</c>, <c>caldav</c> or <c>todoist</c>.</summary>
    string Kind { get; }

    /// <summary>Name shown to the user.</summary>
    string DisplayName { get; }

    /// <summary>The calendars and task lists this source offers.</summary>
    Task<IReadOnlyList<CollectionInfo>> GetCollectionsAsync(CancellationToken cancellationToken = default);
}

namespace AllMyTasks.Core.Views;

/// <summary>A named All tasks setup: the filter, the sort and the grouping.</summary>
/// <param name="Id">Stable identity, kept when the filter is renamed.</param>
/// <param name="Name">Name shown in the Filters menu.</param>
/// <param name="Filter">The filter.</param>
/// <param name="Sort">The sort.</param>
/// <param name="Grouping">The grouping.</param>
/// <param name="HasMissingCollections">
/// Set on load when collections the filter named no longer exist. They are dropped and the other conditions kept, so
/// the view can show a notice. Not stored.
/// </param>
public sealed record SavedTaskFilter(Guid Id, string Name, TaskFilter Filter, TaskSort Sort, TaskGrouping Grouping, bool HasMissingCollections = false)
{
    public string Name { get; init => field = Guard.NotBlank(value); } = Guard.NotBlank(Name);

    /// <summary>True when the given settings are the same as this saved filter's, ignoring the order of choices.</summary>
    public bool Matches(TaskFilter filter, TaskSort sort, TaskGrouping grouping)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(sort);
        return Sort == sort
            && Grouping == grouping
            && Filter.Status == filter.Status
            && Filter.AllLabels == filter.AllLabels
            && (Filter.Text ?? string.Empty).Trim() == (filter.Text ?? string.Empty).Trim()
            && Filter.Collections.SetEquals(filter.Collections)
            && Filter.Priorities.SetEquals(filter.Priorities)
            && new HashSet<string>(Filter.Labels, StringComparer.OrdinalIgnoreCase).SetEquals(filter.Labels)
            && Filter.Due.Options.SetEquals(filter.Due.Options)
            && Filter.Due.From == filter.Due.From
            && Filter.Due.To == filter.Due.To;
    }

    /// <summary>
    /// Drops collections that no longer exist from the filter and keeps its other conditions. When any were dropped,
    /// <see cref="HasMissingCollections"/> is set so the view can warn instead of silently showing more than the user saved.
    /// </summary>
    public SavedTaskFilter WithoutMissing(IReadOnlySet<CollectionRef> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        var kept = Filter.Collections.Where(existing.Contains).ToHashSet();
        return kept.Count == Filter.Collections.Count
            ? this
            : this with { Filter = Filter with { Collections = kept }, HasMissingCollections = true };
    }
}

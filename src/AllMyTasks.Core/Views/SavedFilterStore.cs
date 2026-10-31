using System.Text.Json;
using System.Text.Json.Serialization;
using AllMyTasks.Core.Storage;

namespace AllMyTasks.Core.Views;

/// <summary>
/// Keeps the user's saved All tasks filters in the settings store under one key, as JSON with a schema version so
/// the format can change. See docs/design/search-and-saved-filters.md#saved-filters.
/// </summary>
public sealed class SavedFilterStore(ISettingsStore settings)
{
    /// <summary>The settings key.</summary>
    public const string Key = "filters.tasks";

    /// <summary>The newest format this build reads and writes.</summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>The saved filters in the user's order. A missing, damaged or newer-format value gives an empty list.</summary>
    public async Task<IReadOnlyList<SavedTaskFilter>> LoadAsync(CancellationToken cancellationToken = default) =>
        Deserialize(await settings.GetAsync(Key, cancellationToken).ConfigureAwait(false));

    public Task SaveAsync(IReadOnlyList<SavedTaskFilter> filters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filters);
        return settings.SetAsync(Key, Serialize(filters), cancellationToken);
    }

    public static string Serialize(IReadOnlyList<SavedTaskFilter> filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        return JsonSerializer.Serialize(new Document { Version = CurrentVersion, Filters = [.. filters.Select(Dto.From)] }, Options);
    }

    /// <summary>Reads the stored text. A filter that cannot be read is skipped, so one bad entry does not lose the rest.</summary>
    public static IReadOnlyList<SavedTaskFilter> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.Number
                || version.GetInt32() > CurrentVersion
                || !root.TryGetProperty("filters", out var filters)
                || filters.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<SavedTaskFilter>();
            foreach (var element in filters.EnumerateArray())
            {
                try
                {
                    if (element.Deserialize<Dto>(Options)?.ToFilter() is { } filter && result.All(f => f.Id != filter.Id))
                    {
                        result.Add(filter);
                    }
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException)
                {
                    System.Diagnostics.Trace.TraceWarning("Skipped an unreadable saved filter: {0}", ex.GetType().Name);
                }
            }

            return result;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            System.Diagnostics.Trace.TraceWarning("Could not read saved filters: {0}", ex.GetType().Name);
            return [];
        }
    }

    private sealed class Document
    {
        [JsonPropertyName("version")]
        public int Version { get; init; }

        [JsonPropertyName("filters")]
        public List<Dto> Filters { get; init; } = [];
    }

    // Plain data, so the stored shape does not depend on how the domain records are built.
    private sealed class Dto
    {
        public Guid Id { get; init; }

        public string? Name { get; init; }

        public TaskStatusFilter Status { get; init; } = TaskStatusFilter.Open;

        public List<DueOption> Due { get; init; } = [];

        public DateOnly? DueFrom { get; init; }

        public DateOnly? DueTo { get; init; }

        public List<CollectionDto> Collections { get; init; } = [];

        public List<AllMyTasks.Core.Tasks.Priority> Priorities { get; init; } = [];

        public List<string> Labels { get; init; } = [];

        public bool AllLabels { get; init; }

        public string? Text { get; init; }

        public TaskSortField SortField { get; init; } = TaskSortField.Due;

        public bool Descending { get; init; }

        public TaskGrouping Grouping { get; init; }

        public static Dto From(SavedTaskFilter saved) => new()
        {
            Id = saved.Id,
            Name = saved.Name,
            Status = saved.Filter.Status,
            Due = [.. saved.Filter.Due.Options],
            DueFrom = saved.Filter.Due.From,
            DueTo = saved.Filter.Due.To,
            Collections = [.. saved.Filter.Collections.Select(c => new CollectionDto { Source = c.SourceId, Collection = c.CollectionId })],
            Priorities = [.. saved.Filter.Priorities],
            Labels = [.. saved.Filter.Labels],
            AllLabels = saved.Filter.AllLabels,
            Text = saved.Filter.Text,
            SortField = saved.Sort.Field,
            Descending = saved.Sort.Descending,
            Grouping = saved.Grouping,
        };

        public SavedTaskFilter? ToFilter()
        {
            if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name))
            {
                return null;
            }

            var filter = new TaskFilter(
                Status,
                new DueFilter(Due.ToHashSet(), DueFrom, DueTo),
                Collections.Where(c => !string.IsNullOrWhiteSpace(c.Source) && !string.IsNullOrWhiteSpace(c.Collection))
                    .Select(c => new CollectionRef(c.Source!, c.Collection!))
                    .ToHashSet(),
                Priorities.ToHashSet(),
                new HashSet<string>(Labels, StringComparer.OrdinalIgnoreCase),
                AllLabels,
                Text);
            return new SavedTaskFilter(Id, Name, filter, new TaskSort(SortField, Descending), Grouping);
        }
    }

    private sealed class CollectionDto
    {
        public string? Source { get; init; }

        public string? Collection { get; init; }
    }
}

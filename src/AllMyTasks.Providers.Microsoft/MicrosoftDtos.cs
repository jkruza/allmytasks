using System.Text.Json.Serialization;

namespace AllMyTasks.Providers.Microsoft;

/// <summary>One page of a Graph collection or delta query. Unknown fields are ignored; every field may be missing.</summary>
internal sealed record GraphPage<T>
{
    public List<T>? Value { get; init; }

    [JsonPropertyName("@odata.nextLink")]
    public string? NextLink { get; init; }

    [JsonPropertyName("@odata.deltaLink")]
    public string? DeltaLink { get; init; }
}

internal sealed record RemovedDto
{
    public string? Reason { get; init; }
}

internal sealed record DateTimeZoneDto
{
    public string? DateTime { get; init; }

    public string? TimeZone { get; init; }
}

internal sealed record BodyDto
{
    public string? Content { get; init; }

    public string? ContentType { get; init; }
}

internal sealed record GraphErrorEnvelope
{
    public GraphErrorDto? Error { get; init; }
}

internal sealed record GraphErrorDto
{
    public string? Code { get; init; }
}

internal sealed record CalendarDto
{
    public string? Id { get; init; }

    public string? Name { get; init; }

    public string? HexColor { get; init; }
}

internal sealed record EventDto
{
    public string? Id { get; init; }

    [JsonPropertyName("@removed")]
    public RemovedDto? Removed { get; init; }

    public string? Subject { get; init; }

    public BodyDto? Body { get; init; }

    public DateTimeZoneDto? Start { get; init; }

    public DateTimeZoneDto? End { get; init; }

    public bool IsAllDay { get; init; }

    public bool IsCancelled { get; init; }

    public string? ShowAs { get; init; }

    public ResponseStatusDto? ResponseStatus { get; init; }

    public LocationDto? Location { get; init; }

    public List<LocationDto>? Locations { get; init; }

    public OnlineMeetingDto? OnlineMeeting { get; init; }

    public string? WebLink { get; init; }

    public List<string>? Categories { get; init; }

    public string? LastModifiedDateTime { get; init; }

    public string? OriginalStartTimeZone { get; init; }
}

internal sealed record ResponseStatusDto
{
    public string? Response { get; init; }
}

internal sealed record LocationDto
{
    public string? DisplayName { get; init; }
}

internal sealed record OnlineMeetingDto
{
    public string? JoinUrl { get; init; }
}

internal sealed record TodoListDto
{
    public string? Id { get; init; }

    public string? DisplayName { get; init; }

    public string? WellknownListName { get; init; }
}

internal sealed record TodoTaskDto
{
    public string? Id { get; init; }

    [JsonPropertyName("@removed")]
    public RemovedDto? Removed { get; init; }

    public string? Title { get; init; }

    public BodyDto? Body { get; init; }

    public DateTimeZoneDto? DueDateTime { get; init; }

    public DateTimeZoneDto? StartDateTime { get; init; }

    public string? Status { get; init; }

    public DateTimeZoneDto? CompletedDateTime { get; init; }

    public string? Importance { get; init; }

    public List<string>? Categories { get; init; }

    public PatternedRecurrenceDto? Recurrence { get; init; }

    public string? LastModifiedDateTime { get; init; }

    public List<ChecklistItemDto>? ChecklistItems { get; init; }
}

internal sealed record ChecklistItemDto
{
    public string? Id { get; init; }

    public string? DisplayName { get; init; }

    public bool IsChecked { get; init; }

    public string? CheckedDateTime { get; init; }
}

internal sealed record PatternedRecurrenceDto
{
    public RecurrencePatternDto? Pattern { get; init; }

    public RecurrenceRangeDto? Range { get; init; }
}

internal sealed record RecurrencePatternDto
{
    public string? Type { get; init; }

    public int Interval { get; init; }

    public int DayOfMonth { get; init; }

    public int Month { get; init; }

    public List<string>? DaysOfWeek { get; init; }

    public string? FirstDayOfWeek { get; init; }

    public string? Index { get; init; }
}

internal sealed record RecurrenceRangeDto
{
    public string? Type { get; init; }

    public string? EndDate { get; init; }

    public int NumberOfOccurrences { get; init; }
}

/// <summary>The delta position of a calendar, kept as the collection's sync state.</summary>
internal sealed record CalendarState
{
    public int V { get; init; } = 1;

    public string? DeltaLink { get; init; }

    public DateTimeOffset Start { get; init; }

    public DateTimeOffset End { get; init; }
}

/// <summary>The delta position of a task list, plus what is needed to notice vanished checklist items.</summary>
internal sealed record TodoState
{
    public int V { get; init; } = 1;

    public string? DeltaLink { get; init; }

    public DateTimeOffset SnapshotAt { get; init; }

    /// <summary>Checklist item ids per task, only for tasks that have any.</summary>
    public Dictionary<string, List<string>> Checklist { get; init; } = [];
}

internal sealed record TokenResponseDto
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }

    public string? Error { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(GraphPage<CalendarDto>))]
[JsonSerializable(typeof(GraphPage<EventDto>))]
[JsonSerializable(typeof(GraphPage<TodoListDto>))]
[JsonSerializable(typeof(GraphPage<TodoTaskDto>))]
[JsonSerializable(typeof(GraphErrorEnvelope))]
[JsonSerializable(typeof(CalendarState))]
[JsonSerializable(typeof(TodoState))]
[JsonSerializable(typeof(TokenResponseDto))]
internal sealed partial class MicrosoftJson : JsonSerializerContext;

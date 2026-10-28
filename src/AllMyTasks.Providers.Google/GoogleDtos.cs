using System.Text.Json.Serialization;

namespace AllMyTasks.Providers.Google;

/// <summary>One page of a Google list reply. Unknown fields are ignored; every field may be missing.</summary>
internal sealed record ListPage<T>
{
    public string? Kind { get; init; }

    public List<T>? Items { get; init; }

    public string? NextPageToken { get; init; }

    public string? NextSyncToken { get; init; }
}

internal sealed record GoogleErrorEnvelope
{
    public GoogleErrorDto? Error { get; init; }
}

internal sealed record GoogleErrorDto
{
    public List<GoogleErrorItemDto>? Errors { get; init; }
}

internal sealed record GoogleErrorItemDto
{
    public string? Reason { get; init; }
}

internal sealed record CalendarListEntryDto
{
    public string? Id { get; init; }

    public string? Summary { get; init; }

    public string? SummaryOverride { get; init; }

    public string? BackgroundColor { get; init; }

    public bool Selected { get; init; }

    public bool Hidden { get; init; }

    public bool Deleted { get; init; }
}

internal sealed record EventTimeDto
{
    public string? Date { get; init; }

    public string? DateTime { get; init; }

    public string? TimeZone { get; init; }
}

internal sealed record AttendeeDto
{
    public string? DisplayName { get; init; }

    public string? Email { get; init; }

    public bool Optional { get; init; }

    public bool Resource { get; init; }

    public bool Self { get; init; }

    public string? ResponseStatus { get; init; }
}

internal sealed record OrganizerDto
{
    public string? DisplayName { get; init; }

    public string? Email { get; init; }

    public bool Self { get; init; }
}

internal sealed record EntryPointDto
{
    public string? EntryPointType { get; init; }

    public string? Uri { get; init; }
}

internal sealed record ConferenceDataDto
{
    public List<EntryPointDto>? EntryPoints { get; init; }
}

internal sealed record EventDto
{
    public string? Id { get; init; }

    public string? Status { get; init; }

    public string? Summary { get; init; }

    public string? Description { get; init; }

    public string? Location { get; init; }

    public string? HtmlLink { get; init; }

    public EventTimeDto? Start { get; init; }

    public EventTimeDto? End { get; init; }

    public List<string>? Recurrence { get; init; }

    public string? RecurringEventId { get; init; }

    public EventTimeDto? OriginalStartTime { get; init; }

    public string? Transparency { get; init; }

    public string? Updated { get; init; }

    public string? EventType { get; init; }

    public List<AttendeeDto>? Attendees { get; init; }

    public OrganizerDto? Organizer { get; init; }

    public ConferenceDataDto? ConferenceData { get; init; }

    public string? HangoutLink { get; init; }
}

internal sealed record TaskListDto
{
    public string? Id { get; init; }

    public string? Title { get; init; }
}

internal sealed record TaskDto
{
    public string? Id { get; init; }

    public string? Title { get; init; }

    public string? Notes { get; init; }

    public string? Due { get; init; }

    public string? Status { get; init; }

    public string? Completed { get; init; }

    public string? Parent { get; init; }

    public string? WebViewLink { get; init; }

    public string? Updated { get; init; }

    public bool Deleted { get; init; }
}

/// <summary>The sync position of a calendar, kept as the collection's sync state.</summary>
internal sealed record CalendarState
{
    public int V { get; init; } = 1;

    public string? SyncToken { get; init; }
}

/// <summary>The sync position of a task list: Google Tasks has no sync token, only <c>updatedMin</c>.</summary>
internal sealed record TaskState
{
    public int V { get; init; } = 1;

    public DateTimeOffset UpdatedMin { get; init; }

    public DateTimeOffset FullAt { get; init; }
}

internal sealed record TokenResponseDto
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }

    public string? Scope { get; init; }

    [JsonPropertyName("id_token")]
    public string? IdToken { get; init; }

    public string? Error { get; init; }
}

internal sealed record IdTokenClaims
{
    public string? Sub { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(ListPage<CalendarListEntryDto>))]
[JsonSerializable(typeof(ListPage<EventDto>))]
[JsonSerializable(typeof(ListPage<TaskListDto>))]
[JsonSerializable(typeof(ListPage<TaskDto>))]
[JsonSerializable(typeof(GoogleErrorEnvelope))]
[JsonSerializable(typeof(CalendarState))]
[JsonSerializable(typeof(TaskState))]
[JsonSerializable(typeof(TokenResponseDto))]
[JsonSerializable(typeof(IdTokenClaims))]
internal sealed partial class GoogleJson : JsonSerializerContext;

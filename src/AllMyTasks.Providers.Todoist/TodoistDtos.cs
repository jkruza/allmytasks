using System.Text.Json;
using System.Text.Json.Serialization;

namespace AllMyTasks.Providers.Todoist;

/// <summary>The part of a Sync API v1 reply that the provider reads. Unknown fields are ignored; every field may be missing.</summary>
internal sealed record SyncResponseDto
{
    public string? SyncToken { get; init; }

    public bool FullSync { get; init; }

    public List<ProjectDto>? Projects { get; init; }

    public List<ItemDto>? Items { get; init; }
}

internal sealed record ProjectDto
{
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Id { get; init; }

    public string? Name { get; init; }

    public string? Color { get; init; }

    public bool IsArchived { get; init; }

    public bool IsDeleted { get; init; }
}

/// <summary>A Todoist task is called an "item" in the Sync API.</summary>
internal sealed record ItemDto
{
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Id { get; init; }

    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? ProjectId { get; init; }

    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? ParentId { get; init; }

    public string? Content { get; init; }

    public string? Description { get; init; }

    public DueDto? Due { get; init; }

    public int Priority { get; init; }

    public List<string>? Labels { get; init; }

    public bool Checked { get; init; }

    public bool IsDeleted { get; init; }

    public string? CompletedAt { get; init; }

    public string? UpdatedAt { get; init; }
}

internal sealed record DueDto
{
    public string? Date { get; init; }

    public string? Timezone { get; init; }
}

/// <summary>Reads an id written as a string or, defensively, as a number.</summary>
internal sealed class FlexibleStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => System.Text.Encoding.UTF8.GetString(reader.ValueSpan),
            _ => SkipToNull(ref reader),
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) => writer.WriteStringValue(value);

    private static string? SkipToNull(ref Utf8JsonReader reader)
    {
        reader.Skip();
        return null;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(SyncResponseDto))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class TodoistJson : JsonSerializerContext;

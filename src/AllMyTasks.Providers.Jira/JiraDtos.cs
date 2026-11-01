using System.Text.Json;
using System.Text.Json.Serialization;

namespace AllMyTasks.Providers.Jira;

/// <summary>One page of the token-paginated issue search. Unknown fields are ignored; every field may be missing.</summary>
internal sealed record SearchResponseDto
{
    public List<IssueDto>? Issues { get; init; }

    public string? NextPageToken { get; init; }

    public bool IsLast { get; init; }
}

internal sealed record IssueDto
{
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Id { get; init; }

    public string? Key { get; init; }

    public IssueFieldsDto? Fields { get; init; }
}

internal sealed record IssueFieldsDto
{
    public string? Summary { get; init; }

    public string? Duedate { get; init; }

    public NamedDto? Priority { get; init; }

    public List<string>? Labels { get; init; }

    public StatusDto? Status { get; init; }

    public ParentDto? Parent { get; init; }

    public ProjectDto? Project { get; init; }

    /// <summary>Atlassian Document Format (an object) on API v3; a plain string would also be accepted.</summary>
    public JsonElement Description { get; init; }

    public string? Updated { get; init; }
}

internal sealed record NamedDto
{
    public string? Name { get; init; }
}

internal sealed record StatusDto
{
    public StatusCategoryDto? StatusCategory { get; init; }
}

internal sealed record StatusCategoryDto
{
    public string? Key { get; init; }
}

internal sealed record ParentDto
{
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Id { get; init; }

    public string? Key { get; init; }
}

internal sealed record ProjectDto
{
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Id { get; init; }

    public string? Key { get; init; }

    public string? Name { get; init; }
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

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SearchResponseDto))]
internal sealed partial class JiraJson : JsonSerializerContext;

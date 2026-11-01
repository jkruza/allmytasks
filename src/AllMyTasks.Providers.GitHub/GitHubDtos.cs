using System.Text.Json;
using System.Text.Json.Serialization;

namespace AllMyTasks.Providers.GitHub;

/// <summary>
/// The part of a REST issue (or pull request, which the issues API returns too) that the provider reads. Unknown
/// fields are ignored and every field may be missing.
/// </summary>
internal sealed record IssueDto
{
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Id { get; init; }

    public int Number { get; init; }

    public string? Title { get; init; }

    public string? Body { get; init; }

    public string? State { get; init; }

    public string? HtmlUrl { get; init; }

    public string? RepositoryUrl { get; init; }

    public RepositoryDto? Repository { get; init; }

    public bool Draft { get; init; }

    public List<LabelDto>? Labels { get; init; }

    public MilestoneDto? Milestone { get; init; }

    /// <summary>Present on pull requests only.</summary>
    public PullRequestDto? PullRequest { get; init; }

    public string? UpdatedAt { get; init; }

    public string? ClosedAt { get; init; }
}

internal sealed record RepositoryDto
{
    public string? FullName { get; init; }
}

internal sealed record LabelDto
{
    public string? Name { get; init; }
}

internal sealed record MilestoneDto
{
    public string? DueOn { get; init; }
}

internal sealed record PullRequestDto
{
    public string? MergedAt { get; init; }
}

/// <summary>The reply of the search API.</summary>
internal sealed record SearchDto
{
    public bool IncompleteResults { get; init; }

    public List<IssueDto>? Items { get; init; }
}

/// <summary>Reads an id written as a number or, defensively, as a string.</summary>
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
[JsonSerializable(typeof(List<IssueDto>))]
[JsonSerializable(typeof(SearchDto))]
internal sealed partial class GitHubJson : JsonSerializerContext;

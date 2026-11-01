using System.Text.Json.Serialization;

namespace AllMyTasks.Providers.GitLab;

/// <summary>The part of <c>GET /user</c> that the provider reads.</summary>
internal sealed record UserDto
{
    public string? Username { get; init; }
}

/// <summary>An issue or a merge request from the global lists; the two share the fields the provider reads. Every field may be missing.</summary>
internal sealed record WorkItemDto
{
    /// <summary>The instance-wide id, which is unique per kind of item (the <c>iid</c> is only unique per project).</summary>
    public long Id { get; init; }

    public long Iid { get; init; }

    public long ProjectId { get; init; }

    public string? Title { get; init; }

    public string? Description { get; init; }

    public string? State { get; init; }

    public List<string>? Labels { get; init; }

    /// <summary>Issues only.</summary>
    public string? DueDate { get; init; }

    /// <summary>Merge requests only; the newer name for <c>work_in_progress</c>.</summary>
    public bool Draft { get; init; }

    public string? WebUrl { get; init; }

    public string? UpdatedAt { get; init; }

    public MilestoneDto? Milestone { get; init; }

    public ReferencesDto? References { get; init; }
}

internal sealed record MilestoneDto
{
    public string? DueDate { get; init; }
}

internal sealed record ReferencesDto
{
    /// <summary>For example <c>group/project#12</c> or <c>group/subgroup/project!5</c>.</summary>
    public string? Full { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(UserDto))]
[JsonSerializable(typeof(List<WorkItemDto>))]
internal sealed partial class GitLabJson : JsonSerializerContext;

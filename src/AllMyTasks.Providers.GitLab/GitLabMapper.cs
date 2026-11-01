using System.Globalization;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Providers.GitLab;

/// <summary>One issue or merge request, ready to be handed out per project. <see cref="Task"/> is null when it could not be mapped.</summary>
internal sealed record Mapped(string ProjectId, string ProjectName, string ItemId, TaskItem? Task);

/// <summary>Maps GitLab issues and merge requests to the app's model. Pure: no I/O.</summary>
internal sealed class GitLabMapper(string sourceId)
{
    internal const string DraftLabel = "draft";
    internal const string ReviewLabel = "review requested";
    internal const int MaxNotesLength = 4000;

    public static string IssueItemId(long id) => "issue-" + id.ToString(CultureInfo.InvariantCulture);

    public static string MergeRequestItemId(long id) => "mr-" + id.ToString(CultureInfo.InvariantCulture);

    public Mapped? MapIssue(WorkItemDto issue) => Map(issue, IssueItemId(issue.Id), '#', issue.DueDate, [], false);

    public Mapped? MapMergeRequest(WorkItemDto mergeRequest, bool reviewRequested) =>
        Map(mergeRequest, MergeRequestItemId(mergeRequest.Id), '!', mergeRequest.Milestone?.DueDate, mergeRequest.Draft ? [DraftLabel] : [], reviewRequested);

    private Mapped? Map(WorkItemDto item, string itemId, char sigil, string? dueText, string[] extraLabels, bool reviewRequested)
    {
        if (item.Id <= 0 || item.ProjectId <= 0)
        {
            return null;
        }

        var projectId = item.ProjectId.ToString(CultureInfo.InvariantCulture);
        var projectName = ProjectName(item.References?.Full, sigil) ?? "Project " + projectId;

        if (!TryParseDue(dueText, out var due))
        {
            // A due date that cannot be read makes a broken item; it is skipped with a warning rather than shown wrong.
            return new Mapped(projectId, projectName, itemId, null);
        }

        // Only web links are kept, because the link is opened from the app.
        var url = Uri.TryCreate(item.WebUrl, UriKind.Absolute, out var parsed) && parsed.Scheme is "https" or "http" ? parsed : null;
        return new Mapped(projectId, projectName, itemId, Build(item, itemId, projectId, sigil, due, url, extraLabels, reviewRequested));
    }

    private TaskItem Build(WorkItemDto item, string itemId, string projectId, char sigil, TaskDue? due, Uri? url, string[] extraLabels, bool reviewRequested)
    {
        var labels = new List<string>(item.Labels ?? []);
        labels.AddRange(extraLabels);
        if (reviewRequested)
        {
            labels.Add(ReviewLabel);
        }

        var notes = string.IsNullOrWhiteSpace(item.Description) ? null : Truncate(item.Description);
        return new TaskItem(
            new ItemKey(sourceId, projectId, itemId),
            $"{item.Title} ({sigil}{item.Iid.ToString(CultureInfo.InvariantCulture)})",
            notes,
            due,
            Status: MapState(item.State),
            Labels: [.. labels.Distinct(StringComparer.Ordinal)],
            Url: url,
            LastModified: ParseInstant(item.UpdatedAt));
    }

    internal static TaskItemStatus MapState(string? state) => state switch
    {
        "closed" or "merged" => TaskItemStatus.Completed,
        _ => TaskItemStatus.Open,
    };

    /// <summary>The project path with namespace from <c>group/sub/project#12</c>, or null when the reference is not in that form.</summary>
    internal static string? ProjectName(string? reference, char sigil)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var cut = reference.LastIndexOf(sigil);
        return cut > 0 ? reference[..cut] : null;
    }

    private static bool TryParseDue(string? text, out TaskDue? due)
    {
        due = null;
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            due = new DueDate(date);
            return true;
        }

        return false;
    }

    private static string Truncate(string text) => text.Length <= MaxNotesLength ? text : text[..MaxNotesLength];

    private static DateTimeOffset? ParseInstant(string? text) =>
        !string.IsNullOrEmpty(text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant)
            ? instant
            : null;
}

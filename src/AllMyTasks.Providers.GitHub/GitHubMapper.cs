using System.Globalization;
using System.Text.RegularExpressions;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Providers.GitHub;

/// <summary>An item mapped to the app's model, with the repository (<c>owner/repo</c>) it belongs to.</summary>
internal sealed record WorkItem(string Repository, TaskItem Task);

/// <summary>Maps GitHub issues and pull requests to tasks. Pure: no I/O. Everything it reads is untrusted text.</summary>
internal sealed partial class GitHubMapper(string sourceId)
{
    internal const string ReviewRequestedLabel = "review requested";
    internal const string DraftLabel = "draft";

    internal const int MaxNotesLength = 20_000;

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}$")]
    private static partial Regex RepositoryName();

    /// <summary>Maps one item, or returns null when it cannot be mapped (no id, no repository, no title).</summary>
    public WorkItem? Map(IssueDto issue, bool reviewRequested)
    {
        if (string.IsNullOrEmpty(issue.Id) || RepositoryOf(issue) is not { } repository)
        {
            return null;
        }

        var isMerged = !string.IsNullOrEmpty(issue.PullRequest?.MergedAt);
        var isCompleted = isMerged || string.Equals(issue.State, "closed", StringComparison.OrdinalIgnoreCase);
        var title = issue.Number > 0 ? $"{issue.Title} (#{issue.Number.ToString(CultureInfo.InvariantCulture)})" : issue.Title ?? string.Empty;

        var labels = (issue.Labels ?? []).Select(l => l.Name).OfType<string>().Where(n => n.Length > 0).ToList();
        if (reviewRequested)
        {
            labels.Add(ReviewRequestedLabel);
        }

        if (issue.Draft)
        {
            labels.Add(DraftLabel);
        }

        var task = new TaskItem(
            new ItemKey(sourceId, repository, issue.Id),
            title,
            Notes: string.IsNullOrWhiteSpace(issue.Body) ? null : Truncate(issue.Body),
            Due: MapDue(issue.Milestone?.DueOn),
            Status: isCompleted ? TaskItemStatus.Completed : TaskItemStatus.Open,
            CompletedAt: isCompleted ? ParseInstant(issue.ClosedAt) : null,
            Labels: labels,
            Url: SafeUrl(issue.HtmlUrl),
            LastModified: ParseInstant(issue.UpdatedAt));
        return new WorkItem(repository, task);
    }

    /// <summary>The <c>owner/repo</c> an item lives in, from the repository object or the last two segments of its API address.</summary>
    internal static string? RepositoryOf(IssueDto issue)
    {
        var name = issue.Repository?.FullName;
        if (string.IsNullOrEmpty(name) && Uri.TryCreate(issue.RepositoryUrl, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2)
            {
                name = $"{segments[^2]}/{segments[^1]}";
            }
        }

        return name is not null && RepositoryName().IsMatch(name) ? name : null;
    }

    /// <summary>A milestone's due date, which GitHub shows as a day. The day in the timestamp is used as written.</summary>
    internal static TaskDue? MapDue(string? dueOn) =>
        dueOn is { Length: >= 10 } && DateOnly.TryParseExact(dueOn.AsSpan(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? new DueDate(date)
            : null;

    /// <summary>Only an https address on github.com is kept, because the link is opened from the app.</summary>
    internal static Uri? SafeUrl(string? text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host == "github.com" || uri.Host.EndsWith(".github.com", StringComparison.Ordinal))
            ? uri
            : null;

    private static string Truncate(string text) => text.Length <= MaxNotesLength ? text : text[..MaxNotesLength];

    private static DateTimeOffset? ParseInstant(string? text) =>
        !string.IsNullOrEmpty(text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant)
            ? instant
            : null;
}

using System.Globalization;
using System.Text;
using System.Text.Json;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Providers.Jira;

/// <summary>Maps Jira issues to the app's model. Pure: no I/O.</summary>
internal sealed class JiraMapper(string sourceId, Uri site)
{
    internal const int MaxDescriptionDepth = 40;

    /// <summary>The project of an issue as a task list named <c>KEY Name</c>, or null when it has no usable project.</summary>
    public CollectionInfo? MapProject(ProjectDto? project) =>
        project is { Id: { Length: > 0 } id }
            ? new CollectionInfo(sourceId, id, ProjectName(project), CollectionKind.TaskList, IsVisible: true, IsReadOnly: true)
            : null;

    private static string ProjectName(ProjectDto project) =>
        string.Join(' ', new[] { project.Key, project.Name }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>
    /// Maps one issue, or returns null when it cannot be mapped (no id, key or project, or an unreadable due date).
    /// <paramref name="projectOfIssue"/> says which project each fetched issue id belongs to, so a parent is linked
    /// only when it is part of the same result.
    /// </summary>
    public TaskItem? MapIssue(IssueDto issue, IReadOnlyDictionary<string, string> projectOfIssue)
    {
        if (issue is not { Id: { Length: > 0 } id, Key: { Length: > 0 } key, Fields.Project.Id: { Length: > 0 } projectId } || issue.Fields is not { } fields)
        {
            return null;
        }

        TaskDue? due = null;
        if (!string.IsNullOrEmpty(fields.Duedate))
        {
            if (!DateOnly.TryParseExact(fields.Duedate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return null;
            }

            due = new DueDate(date);
        }

        ItemKey? parent = fields.Parent?.Id is { Length: > 0 } parentId && parentId != id && projectOfIssue.TryGetValue(parentId, out var parentProject)
            ? new ItemKey(sourceId, parentProject, parentId)
            : null;
        var summary = fields.Summary?.Trim();
        return new TaskItem(
            new ItemKey(sourceId, projectId, id),
            string.IsNullOrEmpty(summary) ? key : $"{key} {summary}",
            Description(fields.Description),
            due,
            Status: MapStatus(fields.Status?.StatusCategory?.Key),
            Priority: MapPriority(fields.Priority?.Name),
            Labels: fields.Labels,
            ParentKey: parent,
            Url: new Uri(site, "browse/" + Uri.EscapeDataString(key)),
            LastModified: ParseInstant(fields.Updated));
    }

    internal static TaskItemStatus MapStatus(string? categoryKey) => categoryKey switch
    {
        "indeterminate" => TaskItemStatus.InProgress,
        "done" => TaskItemStatus.Completed,
        _ => TaskItemStatus.Open,
    };

    internal static Priority MapPriority(string? name) => name?.Trim().ToUpperInvariant() switch
    {
        "HIGHEST" or "HIGH" => Priority.High,
        "MEDIUM" => Priority.Medium,
        "LOW" or "LOWEST" => Priority.Low,
        _ => Priority.None,
    };

    /// <summary>Jira writes offsets without a colon (<c>+0200</c>); the parser accepts both.</summary>
    internal static DateTimeOffset? ParseInstant(string? text) =>
        !string.IsNullOrEmpty(text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant)
            ? instant
            : null;

    /// <summary>Flattens a description (Atlassian Document Format, or a plain string) to text. Markup is never kept.</summary>
    internal static string? Description(JsonElement element)
    {
        string? text = null;
        if (element.ValueKind == JsonValueKind.String)
        {
            text = element.GetString();
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            var builder = new StringBuilder();
            Flatten(element, builder, 0);
            text = builder.ToString();
        }

        text = text?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static void Flatten(JsonElement node, StringBuilder builder, int depth)
    {
        if (depth > MaxDescriptionDepth || node.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var type = node.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        switch (type)
        {
            case "text":
                builder.Append(StringProperty(node, "text"));
                return;
            case "hardBreak":
                builder.Append('\n');
                return;
            case "mention" or "emoji" or "status":
                builder.Append(node.TryGetProperty("attrs", out var attrs) ? StringProperty(attrs, "text") : null);
                return;
            case "inlineCard":
                builder.Append(node.TryGetProperty("attrs", out var card) ? StringProperty(card, "url") : null);
                return;
        }

        if (node.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in content.EnumerateArray())
            {
                Flatten(child, builder, depth + 1);
            }
        }

        // Block nodes end with a line break so paragraphs and list items do not run together.
        if (type is "paragraph" or "heading" or "listItem" or "codeBlock" or "blockquote" or "tableRow" && builder.Length > 0 && builder[^1] != '\n')
        {
            builder.Append('\n');
        }
    }

    private static string? StringProperty(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

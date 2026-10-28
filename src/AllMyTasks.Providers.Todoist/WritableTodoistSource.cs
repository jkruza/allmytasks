using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using Microsoft.Extensions.Logging;

namespace AllMyTasks.Providers.Todoist;

/// <summary>
/// A Todoist account the user allowed the app to change: tasks can be completed and reopened. Created instead of
/// <see cref="TodoistSource"/> only when the source's <c>allowChanges</c> switch is on.
/// </summary>
internal sealed class WritableTodoistSource(
    string id,
    string displayName,
    TodoistClient client,
    TimeZoneInfo displayZone,
    ILogger? logger = null,
    TimeProvider? timeProvider = null)
    : TodoistSource(id, displayName, client, displayZone, logger, timeProvider, allowChanges: true), IWritableTaskSource
{
    internal const string CloseCommand = "item_close";
    internal const string UncompleteCommand = "item_uncomplete";

    public Task<WriteResult> CompleteAsync(ItemKey key, Guid changeId, CancellationToken cancellationToken = default) =>
        SendAsync(CloseCommand, key, changeId, cancellationToken);

    public Task<WriteResult> ReopenAsync(ItemKey key, Guid changeId, CancellationToken cancellationToken = default) =>
        SendAsync(UncompleteCommand, key, changeId, cancellationToken);

    private async Task<WriteResult> SendAsync(string type, ItemKey key, Guid changeId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.SourceId != Id)
        {
            throw new SourceException("The task belongs to another source.");
        }

        var reply = await _client.ExecuteAsync([new TodoistCommand(type, changeId, key.ItemId)], cancellationToken).ConfigureAwait(false);

        // Whatever happened, the account may have changed, so replies remembered for the next pull are out of date.
        ForgetReplies();

        if (reply.CommandStatus is null || !reply.CommandStatus.TryGetValue(changeId.ToString("D"), out var status))
        {
            throw new SourceDataException("Todoist did not say what happened to the change.") { Diagnostics = $"No result for command {type}" };
        }

        if (status.IsOk)
        {
            return new WriteResult(WriteOutcome.Applied);
        }

        if (IsNotFound(status))
        {
            return new WriteResult(WriteOutcome.NotFound);
        }

        throw new SourceException($"Todoist refused the change{Describe(status)}.")
        {
            Diagnostics = $"{type}: error code {status.ErrorCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}",
        };
    }

    // Unverified against a live account: the error text is what Todoist is assumed to say for a task that is gone.
    private static bool IsNotFound(CommandStatus status) =>
        status.Error?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true;

    // Todoist's text is shown to the user, so it is trimmed to one short printable line.
    private static string Describe(CommandStatus status)
    {
        var text = new string([.. (status.Error ?? string.Empty).Where(c => !char.IsControl(c))]).Trim();
        if (text.Length > 120)
        {
            text = text[..120] + "…";
        }

        return text.Length == 0 ? string.Empty : $": {text}";
    }
}

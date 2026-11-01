using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Sync;
using Microsoft.Extensions.Logging;

namespace AllMyTasks.Providers.GitHub;

/// <summary>Creates <c>github</c> sources from a personal access token, which is kept in the secret store only.</summary>
public sealed class GitHubFactory(HttpMessageHandler? handler = null, ILogger? logger = null) : ISourceFactory, IDisposable
{
    public const string TokenField = "token";

    private readonly HttpMessageHandler _handler = handler ?? GitHubClient.CreateDefaultHandler();
    private readonly bool _ownsHandler = handler is null;

    public string Kind => GitHubSource.KindName;

    public string DisplayName => "GitHub";

    public SourceConfigSchema ConfigSchema { get; } = new(
    [
        new SourceConfigField(
            TokenField,
            "Personal access token",
            FieldKind.Password,
            Required: true,
            Help: "In GitHub open Settings, Developer settings, Personal access tokens and create a fine-grained token with read-only access to Issues, Pull requests and Metadata on the repositories you want to see (a classic token with the repo scope also works). The app only reads with it. It is stored in your system keyring."),
        new SourceConfigField(SyncScheduler.IntervalKey, "Refresh every (minutes)", FieldKind.Number, Help: "Leave blank for 15."),
    ]);

    public void Dispose()
    {
        if (_ownsHandler)
        {
            _handler.Dispose();
        }
    }

    public ISource Create(string sourceId, string displayName, SourceConfig config, ISecretStore secrets)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(secrets);
        var client = new GitHubClient(_handler, async ct => (await secrets.GetAsync(sourceId, TokenField, ct).ConfigureAwait(false))?.Trim());
        return new GitHubSource(sourceId, displayName, client, logger);
    }
}

using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Sync;
using Microsoft.Extensions.Logging;

namespace AllMyTasks.Providers.GitLab;

/// <summary>Creates <c>gitlab</c> sources from an instance address and a personal access token, which is kept in the secret store only.</summary>
public sealed class GitLabFactory(HttpMessageHandler? handler = null, ILogger? logger = null) : ISourceFactory, ISourceConfigValidator, IDisposable
{
    public const string BaseUrlField = "baseUrl";
    public const string TokenField = "token";

    private readonly HttpMessageHandler _handler = handler ?? GitLabClient.CreateDefaultHandler();
    private readonly bool _ownsHandler = handler is null;

    public string Kind => GitLabSource.KindName;

    public string DisplayName => "GitLab";

    public SourceConfigSchema ConfigSchema { get; } = new(
    [
        new SourceConfigField(
            BaseUrlField,
            "GitLab address",
            FieldKind.Url,
            Help: "Leave blank for https://gitlab.com. For a self-managed instance enter its address, for example https://gitlab.example.com. It must be https."),
        new SourceConfigField(
            TokenField,
            "Personal access token",
            FieldKind.Password,
            Required: true,
            Help: "In GitLab open Preferences, Access tokens, add a token with only the read_api scope and paste it here. The app only reads. It is stored in your system keyring."),
        new SourceConfigField(SyncScheduler.IntervalKey, "Refresh every (minutes)", FieldKind.Number, Help: "Leave blank for 15."),
    ]);

    public IEnumerable<SourceConfigError> ValidateValues(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.TryGetValue(BaseUrlField, out var text) && !GitLabClient.TryParseBaseUrl(text, out _))
        {
            yield return new SourceConfigError(BaseUrlField, "Enter the address of the instance starting with https://, without a user name, query or anything after the path.");
        }
    }

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
        if (!GitLabClient.TryParseBaseUrl(config.Get(BaseUrlField), out var baseUrl))
        {
            throw new ArgumentException("The GitLab address is not valid.", nameof(config));
        }

        var client = new GitLabClient(baseUrl, _handler, async ct => (await secrets.GetAsync(sourceId, TokenField, ct).ConfigureAwait(false))?.Trim());
        return new GitLabSource(sourceId, displayName, client, logger);
    }
}

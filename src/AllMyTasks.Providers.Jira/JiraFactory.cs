using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Sync;
using Microsoft.Extensions.Logging;

namespace AllMyTasks.Providers.Jira;

/// <summary>
/// Creates <c>jira</c> sources for a Jira Cloud site. The site address and email are ordinary settings; the API token is
/// kept in the secret store only.
/// </summary>
public sealed class JiraFactory(HttpMessageHandler? handler = null, ILogger? logger = null) : ISourceFactory, ISourceConfigValidator, IDisposable
{
    public const string SiteField = "site";
    public const string EmailField = "email";
    public const string TokenField = "token";

    private readonly HttpMessageHandler _handler = handler ?? JiraClient.CreateDefaultHandler();
    private readonly bool _ownsHandler = handler is null;

    public string Kind => JiraSource.KindName;

    public string DisplayName => "Jira";

    public SourceConfigSchema ConfigSchema { get; } = new(
    [
        new SourceConfigField(
            SiteField,
            "Jira Cloud site",
            FieldKind.Url,
            Required: true,
            Help: "The address of your site, such as https://example.atlassian.net. Only Jira Cloud is supported."),
        new SourceConfigField(EmailField, "Email", FieldKind.Text, Required: true, Help: "The email address of your Atlassian account."),
        new SourceConfigField(
            TokenField,
            "API token",
            FieldKind.Password,
            Required: true,
            Help: "Create one in your Atlassian account settings under Security, API tokens. It gives the same access as your account, so the app only reads with it. It is stored in your system keyring."),
        new SourceConfigField(SyncScheduler.IntervalKey, "Refresh every (minutes)", FieldKind.Number, Help: "Leave blank for 15. The app shows open issues assigned to you."),
    ]);

    public IEnumerable<SourceConfigError> ValidateValues(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.TryGetValue(SiteField, out var site) && !string.IsNullOrWhiteSpace(site) && JiraClient.NormalizeSite(site) is null)
        {
            yield return new SourceConfigError(SiteField, "The site must be a secure web address such as https://example.atlassian.net.");
        }

        if (values.TryGetValue(EmailField, out var email) && !string.IsNullOrWhiteSpace(email) && !JiraClient.IsUsableCredential(email.Trim(), allowColon: false))
        {
            yield return new SourceConfigError(EmailField, "The email must be one address without spaces or a colon.");
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
        var site = JiraClient.NormalizeSite(config.Get(SiteField))
            ?? throw new ArgumentException("The Jira site must be a secure web address such as https://example.atlassian.net.", nameof(config));
        var client = new JiraClient(
            _handler,
            site,
            _ => Task.FromResult(config.Get(EmailField)),
            ct => secrets.GetAsync(sourceId, TokenField, ct));
        return new JiraSource(sourceId, displayName, client, site, logger);
    }
}

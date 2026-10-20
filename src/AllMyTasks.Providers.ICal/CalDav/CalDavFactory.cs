using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Sync;
using Microsoft.Extensions.Logging;

namespace AllMyTasks.Providers.ICal.CalDav;

/// <summary>Creates <c>caldav</c> sources: a server address, a user name and a password (an app password where offered).</summary>
public sealed class CalDavFactory(HttpMessageHandler? handler = null, ILogger? logger = null) : ISourceFactory, IDisposable
{
    public const string UrlField = "url";
    public const string UserField = "username";
    public const string PasswordField = "password";
    public const string InsecureField = "allowInsecure";

    private readonly HttpMessageHandler _handler = handler ?? IcalFeedClient.CreateDefaultHandler();
    private readonly bool _ownsHandler = handler is null;

    public string Kind => CalDavSource.KindName;

    public string DisplayName => "CalDAV account";

    public SourceConfigSchema ConfigSchema { get; } = new(
    [
        new SourceConfigField(UrlField, "Server address", FieldKind.Url, Required: true, Help: "For example https://nextcloud.example.com or the calendar's own address."),
        new SourceConfigField(UserField, "User name", FieldKind.Text, Required: true),
        new SourceConfigField(PasswordField, "Password", FieldKind.Password, Required: true, Help: "Use an app password if your provider offers them. It is stored in your system keyring."),
        new SourceConfigField(InsecureField, "Allow insecure connection (http)", FieldKind.Bool, Help: "Sends the password unencrypted. Only for servers on a network you fully trust."),
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
        var url = UrlRedactor.NormalizeFeedUrl(config.Get(UrlField))
            ?? throw new SourceAuthException("The server address is missing or not valid. Edit the source to enter it again.");
        var insecure = string.Equals(config.Get(InsecureField), "true", StringComparison.OrdinalIgnoreCase);
        var client = new CalDavClient(
            _handler,
            config.Get(UserField) ?? string.Empty,
            ct => secrets.GetAsync(sourceId, PasswordField, ct),
            insecure);
        return new CalDavSource(sourceId, displayName, url, client, logger);
    }
}

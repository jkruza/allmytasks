using AllMyTasks.Core.Storage;
using AllMyTasks.Providers.ICal.CalDav;

namespace AllMyTasks.Providers.ICal.Tests;

internal static class Integration
{
    public static string? Url => Environment.GetEnvironmentVariable("AMT_CALDAV_URL");

    public static bool Configured => !string.IsNullOrEmpty(Url);
}

/// <summary>
/// Optional test against a real CalDAV server (for example a throwaway Radicale). Off unless
/// <c>AMT_CALDAV_URL</c>, <c>AMT_CALDAV_USER</c> and <c>AMT_CALDAV_PASSWORD</c> are set; never runs in CI. Use a
/// throwaway account with made-up data only.
/// </summary>
public class IntegrationTests
{
    [Fact(Skip = "Set AMT_CALDAV_URL, AMT_CALDAV_USER and AMT_CALDAV_PASSWORD to run", SkipUnless = nameof(Integration.Configured), SkipType = typeof(Integration))]
    public async Task Discovery_and_a_full_then_incremental_sync_work_against_a_real_server()
    {
        var ct = TestContext.Current.CancellationToken;
        var secrets = new InMemorySecretStore();
        await secrets.SetAsync("it", "password", Environment.GetEnvironmentVariable("AMT_CALDAV_PASSWORD") ?? string.Empty, ct);
        var url = Integration.Url!;
        using var factory = new CalDavFactory();
        var source = (CalDavSource)factory.Create(
            "it",
            "Integration",
            new SourceConfig(new Dictionary<string, string>
            {
                ["url"] = url,
                ["username"] = Environment.GetEnvironmentVariable("AMT_CALDAV_USER") ?? string.Empty,
                ["allowInsecure"] = url.StartsWith("http://", StringComparison.Ordinal) ? "true" : "false",
            }),
            secrets);

        var collections = await source.GetCollectionsAsync(ct);
        Assert.NotEmpty(collections);
        var first = await source.SyncEventsAsync(collections[0], null, ct);
        var second = await source.SyncEventsAsync(collections[0], first.NewState, ct);

        Assert.True(first.IsFullSnapshot);
        Assert.False(second.IsFullSnapshot);
        Assert.Empty(second.Upserts);
    }
}

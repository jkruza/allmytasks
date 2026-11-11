using AllMyTasks.Core.About;

namespace AllMyTasks.Core.Tests;

public sealed class AboutInfoTests
{
    private sealed class FakeClient(string provider, ClientStatus status) : IBuiltInClientInfo
    {
        public string ClientProvider => provider;

        public ClientStatus BuiltInClientStatus => status;
    }

    [Theory]
    [InlineData("id", "secret", true, ClientStatus.Included)]
    [InlineData("id", null, true, ClientStatus.Incomplete)]
    [InlineData(null, "secret", true, ClientStatus.Incomplete)]
    [InlineData("", "  ", true, ClientStatus.NotIncluded)]
    [InlineData(null, null, true, ClientStatus.NotIncluded)]
    [InlineData("id", null, false, ClientStatus.Included)]
    [InlineData(" ", null, false, ClientStatus.NotIncluded)]
    public void Client_status_from_build_values(string? id, string? secret, bool requiresSecret, ClientStatus expected)
        => Assert.Equal(expected, ClientStatusRules.From(id, secret, requiresSecret));

    [Theory]
    [InlineData("0.9.0-alpha+abcdef1234567890", "0.9.0-alpha", "abcdef123")]
    [InlineData("1.0.0", "1.0.0", null)]
    [InlineData("1.0.0+", "1.0.0", null)]
    [InlineData(null, "unknown", null)]
    [InlineData("  ", "unknown", null)]
    public void Splits_informational_version(string? input, string version, string? commit)
        => Assert.Equal((version, commit), AboutInfo.SplitVersion(input));

    [Fact]
    public void Text_lists_version_and_client_states_in_provider_order()
    {
        var info = AboutInfo.Create("0.9.0", "abc", ".NET 10", "Linux", [new FakeClient("Microsoft", ClientStatus.NotIncluded), new FakeClient("Google", ClientStatus.Included)]);

        var text = info.ToText();

        Assert.Contains("All My Tasks 0.9.0", text, StringComparison.Ordinal);
        Assert.Contains("Commit: abc", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("Google", StringComparison.Ordinal) < text.IndexOf("Microsoft", StringComparison.Ordinal));
        Assert.Contains("Built-in Google client: Included", text, StringComparison.Ordinal);
        Assert.Contains("Built-in Microsoft client: Not included", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Blank_version_shows_unknown_and_commit_is_omitted()
    {
        var text = AboutInfo.Create(" ", null, "rt", "os", []).ToText();

        Assert.Contains("All My Tasks unknown", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Commit:", text, StringComparison.Ordinal);
    }
}

using AllMyTasks.Core.Storage;

namespace AllMyTasks.Core.Tests;

public class DataDirectoryTests
{
    [Fact]
    public void Override_wins_and_is_made_absolute()
    {
        Assert.Equal(Path.GetFullPath("data"), DataDirectory.Resolve("data"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../data")]
    [InlineData("a/../../b")]
    [InlineData("a\\..\\b")]
    [InlineData("a\0b")]
    public void Unsafe_override_is_rejected(string path)
    {
        Assert.False(DataDirectory.IsAcceptable(path));
        Assert.Throws<ArgumentException>(() => DataDirectory.Resolve(path));
    }

    [Theory]
    [InlineData("/tmp/data")]
    [InlineData("my..data")]
    [InlineData("..hidden/x")]
    public void Plain_paths_are_accepted(string path)
    {
        Assert.True(DataDirectory.IsAcceptable(path));
    }
}

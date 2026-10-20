using AllMyTasks.Core.Model;

namespace AllMyTasks.Core.Tests;

public class ItemKeyTests
{
    [Theory]
    [InlineData("", "c", "i")]
    [InlineData("s", " ", "i")]
    [InlineData("s", "c", "")]
    public void Constructor_rejects_blank_parts(string source, string collection, string item) =>
        Assert.ThrowsAny<ArgumentException>(() => new ItemKey(source, collection, item));

    [Fact]
    public void Constructor_rejects_null_parts() =>
        Assert.Throws<ArgumentNullException>(() => new ItemKey(null!, "c", "i"));

    [Fact]
    public void With_expression_still_validates() =>
        Assert.ThrowsAny<ArgumentException>(() => TestData.Key() with { ItemId = "" });

    [Fact]
    public void Keys_with_same_parts_are_equal() =>
        Assert.Equal(new ItemKey("s", "c", "i"), new ItemKey("s", "c", "i"));

    [Fact]
    public void Override_differs_from_its_master_but_shares_item_id()
    {
        var master = new ItemKey("s", "c", "uid");
        var occurrence = master with { RecurrenceId = new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero) };

        Assert.NotEqual(master, occurrence);
        Assert.Equal(master.ItemId, occurrence.ItemId);
        Assert.True(occurrence.IsOverride);
        Assert.False(master.IsOverride);
        Assert.Equal(master, occurrence.Master);
    }

    [Fact]
    public void Collection_info_validates_ids() =>
        Assert.ThrowsAny<ArgumentException>(() => new CollectionInfo("s", "", "x", CollectionKind.Calendar));

    [Fact]
    public void Source_info_validates_ids() =>
        Assert.ThrowsAny<ArgumentException>(() => new SourceInfo("", "ical", "x"));
}

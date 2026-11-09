using AllMyTasks.Core.Views;

namespace AllMyTasks.Core.Tests;

public class MonthCellFitTests
{
    [Theory]
    [InlineData(0, 100, 20, 16, 0)]
    [InlineData(3, 100, 20, 16, 3)]   // all fit, no "more" needed
    [InlineData(5, 100, 20, 16, 5)]   // exactly fills the height
    [InlineData(6, 100, 20, 16, 4)]   // 6 do not fit: keep room for the button
    [InlineData(10, 100, 20, 16, 4)]
    [InlineData(2, 10, 20, 16, 0)]    // nothing fits, only "+2 more"
    [InlineData(2, -5, 20, 16, 0)]
    public void Shows_what_fits_and_reserves_room_for_more(int total, double height, double item, double more, int expected) =>
        Assert.Equal(expected, MonthCellFit.VisibleCount(total, height, item, more));

    [Fact]
    public void A_taller_cell_shows_more_entries() =>
        Assert.True(MonthCellFit.VisibleCount(20, 200, 20, 16) > MonthCellFit.VisibleCount(20, 100, 20, 16));
}

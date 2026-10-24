using System.Globalization;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Views;

namespace AllMyTasks.Core.Tests;

public class RecurrenceDescriberTests
{
    private static string Describe(string rule) => RecurrenceDescriber.Describe(new Recurrence(rule), CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("FREQ=DAILY", "Every day")]
    [InlineData("FREQ=WEEKLY", "Every week")]
    [InlineData("FREQ=MONTHLY;INTERVAL=3", "Every 3 months")]
    [InlineData("FREQ=YEARLY", "Every year")]
    [InlineData("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE", "Every 2 weeks on Monday, Wednesday")]
    [InlineData("FREQ=DAILY;BYDAY=MO,TU,WE,TH,FR", "Every weekday")]
    [InlineData("FREQ=WEEKLY;BYDAY=FR,MO,WE,TU,TH", "Every weekday")]
    [InlineData("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,TU,WE,TH,FR", "Every 2 weeks on Monday, Tuesday, Wednesday, Thursday, Friday")]
    [InlineData("FREQ=MONTHLY;BYDAY=1MO", "Every month on the first Monday")]
    [InlineData("FREQ=MONTHLY;BYDAY=-1FR", "Every month on the last Friday")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=15", "Every month on day 15")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=-1", "Every month on day last")]
    [InlineData("FREQ=DAILY;COUNT=10", "Every day, 10 times")]
    [InlineData("FREQ=DAILY;COUNT=1", "Every day, once")]
    [InlineData("FREQ=WEEKLY;UNTIL=20261231", "Every week, until 31 Dec 2026")]
    [InlineData("FREQ=WEEKLY;UNTIL=20261231T235959Z", "Every week, until 31 Dec 2026")]
    [InlineData("freq=daily;interval=2", "Every 2 days")]
    public void Describes_common_rules(string rule, string expected) => Assert.Equal(expected, Describe(rule));

    [Theory]
    [InlineData("")]
    [InlineData("FREQ=HOURLY")]
    [InlineData("INTERVAL=2")]
    [InlineData("FREQ=DAILY;INTERVAL=0")]
    [InlineData("FREQ=DAILY;INTERVAL=x")]
    [InlineData("FREQ=YEARLY;BYMONTH=3")]
    [InlineData("FREQ=MONTHLY;BYSETPOS=1;BYDAY=MO")]
    [InlineData("FREQ=WEEKLY;BYDAY=XX")]
    [InlineData("FREQ=MONTHLY;BYDAY=9MO")]
    [InlineData("FREQ=DAILY;COUNT=0")]
    [InlineData("FREQ=DAILY;UNTIL=soon")]
    [InlineData("not a rule")]
    public void Unknown_or_invalid_rules_fall_back_without_throwing(string rule) =>
        Assert.Equal("Repeats (custom rule)", Describe(rule));

    [Fact]
    public void Rdate_only_series_is_described()
    {
        var recurrence = new Recurrence(string.Empty, [ViewTestKit.Utc_(2026, 10, 1)], []);
        Assert.Equal("Repeats on chosen dates", RecurrenceDescriber.Describe(recurrence, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Extra_and_excluded_dates_are_counted()
    {
        var recurrence = new Recurrence("FREQ=DAILY", [ViewTestKit.Utc_(2026, 10, 1)], [ViewTestKit.Utc_(2026, 10, 2), ViewTestKit.Utc_(2026, 10, 3)]);
        Assert.Equal("Every day (1 extra date, 2 skipped)", RecurrenceDescriber.Describe(recurrence, CultureInfo.InvariantCulture));
    }
}

using AllMyTasks.Providers.ICal;
using AllMyTasks.Providers.ICal.CalDav;

namespace AllMyTasks.Providers.ICal.Tests;

public class SetupHelpTests
{
    [Fact]
    public void Both_calendar_providers_say_what_the_user_needs_before_adding_them()
    {
        using var feed = new IcalFeedFactory();
        using var dav = new CalDavFactory();

        Assert.False(string.IsNullOrWhiteSpace(feed.ConfigSchema.WhatYouNeed));
        Assert.False(string.IsNullOrWhiteSpace(dav.ConfigSchema.WhatYouNeed));
    }
}

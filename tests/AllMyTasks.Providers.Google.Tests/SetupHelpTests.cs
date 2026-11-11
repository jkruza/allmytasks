using AllMyTasks.Providers.Google;

namespace AllMyTasks.Providers.Google.Tests;

public class SetupHelpTests
{
    [Fact]
    public void The_provider_says_what_the_user_needs_before_adding_it()
    {
        using var factory = new GoogleFactory();

        Assert.False(string.IsNullOrWhiteSpace(factory.ConfigSchema.WhatYouNeed));
    }
}

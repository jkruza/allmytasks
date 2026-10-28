using AllMyTasks.Core.Calendar;

namespace AllMyTasks.Core.Tests;

public class MeetingLinkTests
{
    [Theory]
    [InlineData("https://meet.google.com/abc-defg-hij")]
    [InlineData("  https://example.com/join?id=1  ")]
    public void Plain_https_links_are_accepted(string text) => Assert.NotNull(MeetingLinks.Sanitize(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://meet.google.com/abc")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("msteams://teams.microsoft.com/l/meetup-join/1")]
    [InlineData("zoommtg://zoom.us/join?confno=1")]
    [InlineData("https://user:secret@zoom.us/j/1")]
    [InlineData("https://user@zoom.us/j/1")]
    [InlineData("/relative/path")]
    [InlineData("not a url")]
    public void Anything_else_is_dropped(string? text) => Assert.Null(MeetingLinks.Sanitize(text));

    [Fact]
    public void Overlong_links_are_dropped() =>
        Assert.Null(MeetingLinks.Sanitize("https://example.com/" + new string('a', MeetingLinks.MaxLength)));

    [Theory]
    [InlineData("https://meet.google.com/abc", "Google Meet")]
    [InlineData("https://teams.microsoft.com/l/meetup-join/1", "Microsoft Teams")]
    [InlineData("https://us02web.zoom.us/j/123", "Zoom")]
    [InlineData("https://acme.webex.com/meet/x", "Webex")]
    [InlineData("https://MEET.GOOGLE.COM/abc", "Google Meet")]
    public void Known_hosts_give_a_service_name(string url, string name) => Assert.Equal(name, MeetingLinks.ServiceName(new Uri(url)));

    [Theory]
    [InlineData("https://example.com/zoom.us")]
    [InlineData("https://notzoom.us/j/1")]
    [InlineData("https://zoom.us.evil.example/j/1")]
    [InlineData("https://meet.google.com.evil.example/x")]
    public void Lookalike_hosts_are_not_known(string url) => Assert.Null(MeetingLinks.ServiceName(new Uri(url)));

    [Fact]
    public void Idn_hosts_are_shown_in_their_ascii_form()
    {
        var uri = new Uri("https://zoom.us.аррӏе.example/j/1");
        Assert.Null(MeetingLinks.ServiceName(uri));
        Assert.StartsWith("zoom.us.xn--", uri.IdnHost, StringComparison.Ordinal);
    }

    [Fact]
    public void Join_label_names_the_service_or_falls_back()
    {
        Assert.Equal("Join Google Meet", MeetingLinks.JoinLabel(new Uri("https://meet.google.com/abc")));
        Assert.Equal("Join meeting", MeetingLinks.JoinLabel(new Uri("https://example.com/call")));
    }

    [Fact]
    public void Text_scan_finds_the_first_known_host_and_ignores_other_links()
    {
        const string Text = "Agenda: https://example.com/doc\nJoin: https://teams.microsoft.com/l/meetup-join/1, thanks.\nAlso https://meet.google.com/zzz";

        Assert.Equal("https://teams.microsoft.com/l/meetup-join/1", MeetingLinks.FindInText(Text)?.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("see https://example.com/page only")]
    [InlineData("http://meet.google.com/abc is not https")]
    public void Text_scan_without_a_known_https_link_finds_nothing(string? text) => Assert.Null(MeetingLinks.FindInText(text));

    [Fact]
    public void The_event_drops_an_unsafe_link_even_when_set_directly()
    {
        var ev = new CalendarEvent(TestData.Key(), "x", new AllDayEvent(new DateOnly(2026, 10, 26), new DateOnly(2026, 10, 27)), MeetingUrl: new Uri("http://meet.google.com/a"));

        Assert.Null(ev.MeetingUrl);
        Assert.Empty(ev.Attendees);
        Assert.Equal(0, ev.AttendeeCount);
    }
}

using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Views;
using static AllMyTasks.Core.Tests.ViewTestKit;

namespace AllMyTasks.Core.Tests;

public class ConflictFinderTests
{
    private static readonly DateTimeOffset Nine = Utc_(2026, 11, 11, 9);

    private static EventEntry Timed(string title, int startMinute, int endMinute, string source = "s", EventStatus status = EventStatus.Confirmed, Availability availability = Availability.Busy)
    {
        var start = Nine.AddMinutes(startMinute);
        var end = Nine.AddMinutes(endMinute);
        var entry = Event(title, start, end);
        var ev = entry.Occurrence.Event with { Key = TestData.Key(title, source), Status = status, Availability = availability };
        return entry with { Occurrence = new EventOccurrence(ev, ev.Time) };
    }

    private static string[] Titles(ConflictGroup group) => [.. group.Entries.Select(e => e.Title)];

    [Fact]
    public void Two_overlapping_events_from_different_sources_conflict()
    {
        var work = Timed("Team sync", 0, 60, "outlook");
        var private_ = Timed("Dentist", 30, 90, "google");

        var map = ConflictFinder.Find([work, private_]);

        var group = Assert.Single(map.Groups);
        Assert.Equal(["Team sync", "Dentist"], Titles(group));
        Assert.Equal("Dentist", Assert.Single(map.OverlapsWith(work)).Title);
        Assert.Equal("Team sync", Assert.Single(map.OverlapsWith(private_)).Title);
        Assert.True(map.HasConflict(work));
    }

    [Fact]
    public void Overlapping_events_in_one_source_conflict_too()
    {
        var map = ConflictFinder.Find([Timed("A", 0, 60), Timed("B", 10, 20)]);

        Assert.Single(map.Groups);
    }

    [Fact]
    public void Events_that_only_touch_do_not_conflict()
    {
        var map = ConflictFinder.Find([Timed("A", 0, 60), Timed("B", 60, 120)]);

        Assert.Empty(map.Groups);
    }

    [Fact]
    public void An_overlap_of_less_than_a_minute_is_not_a_conflict()
    {
        var a = Timed("A", 0, 60);
        var b = Event("B", Nine.AddMinutes(59).AddSeconds(30), Nine.AddMinutes(120));

        Assert.Empty(ConflictFinder.Find([a, b]).Groups);
    }

    [Fact]
    public void An_overlap_of_exactly_one_minute_is_a_conflict() =>
        Assert.Single(ConflictFinder.Find([Timed("A", 0, 60), Timed("B", 59, 120)]).Groups);

    [Fact]
    public void A_chain_forms_one_group_but_only_direct_overlaps_are_listed()
    {
        var a = Timed("A", 0, 60);
        var b = Timed("B", 50, 110);
        var c = Timed("C", 100, 160);

        var map = ConflictFinder.Find([c, a, b]);

        var group = Assert.Single(map.Groups);
        Assert.Equal(["A", "B", "C"], Titles(group));
        Assert.Equal(["B"], map.OverlapsWith(a).Select(e => e.Title));
        Assert.Equal(["A", "C"], map.OverlapsWith(b).Select(e => e.Title).Order());
        Assert.Equal(["B"], map.OverlapsWith(c).Select(e => e.Title));
    }

    [Fact]
    public void A_full_cluster_lists_everyone_for_everyone()
    {
        var all = new[] { Timed("A", 0, 60), Timed("B", 0, 60), Timed("C", 0, 60) };

        var map = ConflictFinder.Find(all);

        Assert.Single(map.Groups);
        Assert.All(all, e => Assert.Equal(2, map.OverlapsWith(e).Count));
    }

    [Fact]
    public void A_nested_event_conflicts_with_the_one_around_it()
    {
        var map = ConflictFinder.Find([Timed("Outer", 0, 180), Timed("Inner", 60, 90)]);

        Assert.Single(map.Groups);
    }

    [Fact]
    public void Separate_clusters_are_separate_groups_in_start_order()
    {
        var map = ConflictFinder.Find([Timed("C", 300, 360), Timed("A", 0, 60), Timed("B", 30, 90), Timed("D", 330, 400)]);

        Assert.Equal(2, map.Count);
        Assert.Equal(["A", "B"], Titles(map.Groups[0]));
        Assert.Equal(["C", "D"], Titles(map.Groups[1]));
    }

    [Fact]
    public void All_day_events_and_tasks_are_ignored_by_default()
    {
        var holiday = AllDay("Holiday", new DateOnly(2026, 11, 11), new DateOnly(2026, 11, 12));
        var task = TimedTask("Call", Nine.AddMinutes(10));

        Assert.Empty(ConflictFinder.Find([holiday, task, Timed("Meeting", 0, 60)]).Groups);
    }

    [Fact]
    public void A_busy_all_day_event_counts_only_when_asked_and_a_free_one_never_does()
    {
        var busy = AllDay("Out of office", new DateOnly(2026, 11, 11), new DateOnly(2026, 11, 12));
        var busyEvent = busy.Occurrence.Event with { Availability = Availability.Busy };
        busy = busy with { Occurrence = new EventOccurrence(busyEvent, busyEvent.Time) };
        var free = AllDay("Birthday", new DateOnly(2026, 11, 11), new DateOnly(2026, 11, 12));
        var freeEvent = free.Occurrence.Event with { Availability = Availability.Free };
        free = free with { Occurrence = new EventOccurrence(freeEvent, freeEvent.Time) };
        var meeting = Timed("Meeting", 0, 60);

        Assert.Empty(ConflictFinder.Find([busy, free, meeting]).Groups);
        var counted = ConflictFinder.Find([busy, free, meeting], new ConflictOptions(CountAllDayBusy: true));
        Assert.Equal(["Out of office", "Meeting"], Titles(Assert.Single(counted.Groups)).Order().Reverse());
    }

    [Theory]
    [InlineData(EventStatus.Cancelled, Availability.Busy)]
    [InlineData(EventStatus.Tentative, Availability.Busy)]
    [InlineData(EventStatus.Confirmed, Availability.Free)]
    public void Cancelled_tentative_and_free_events_do_not_conflict(EventStatus status, Availability availability)
    {
        var other = Timed("Other", 0, 60, status: status, availability: availability);

        Assert.Empty(ConflictFinder.Find([Timed("Meeting", 0, 60), other]).Groups);
    }

    [Fact]
    public void Zero_length_events_do_not_conflict()
    {
        var point = Event("Point", Nine.AddMinutes(10), Nine.AddMinutes(10));

        Assert.Empty(ConflictFinder.Find([Timed("Meeting", 0, 60), point]).Groups);
    }

    [Fact]
    public void Occurrences_of_one_series_are_told_apart_by_their_start()
    {
        var first = Timed("Standup", 0, 30);
        var second = first with { Start = first.Start.AddDays(1), End = first.End.AddDays(1) };
        var clash = Timed("Other", 10, 40);

        var map = ConflictFinder.Find([first, second, clash]);

        Assert.True(map.HasConflict(first));
        Assert.False(map.HasConflict(second));
    }

    [Fact]
    public void A_day_with_a_clock_change_uses_real_elapsed_time()
    {
        // Europe/Warsaw springs forward on 2026-03-29: 02:00 local does not exist. 01:30 to 03:30 is one real hour.
        var zone = TestData.Warsaw;
        DateTimeOffset At(int h, int m) => TimeZoneInfo.ConvertTime(new DateTimeOffset(2026, 3, 29, h, m, 0, TimeSpan.Zero), zone);
        var a = Event("A", At(0, 30), At(1, 30), zone);
        var b = Event("B", At(1, 0), At(2, 0), zone);
        var after = Event("After", At(1, 30), At(2, 30), zone);

        var map = ConflictFinder.Find([a, b, after]);

        Assert.Equal(["A", "B", "After"], Titles(Assert.Single(map.Groups)));
        Assert.Equal(["B"], map.OverlapsWith(a).Select(e => e.Title));
        Assert.Equal(["B"], map.OverlapsWith(after).Select(e => e.Title));
    }

    [Fact]
    public void Many_back_to_back_events_have_no_conflicts_and_finish_quickly()
    {
        var events = Enumerable.Range(0, 20_000).Select(i => Event("E" + i, Nine.AddMinutes(i * 30), Nine.AddMinutes((i * 30) + 30))).ToList();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var map = ConflictFinder.Find(events);

        Assert.Empty(map.Groups);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void No_entries_give_the_empty_map()
    {
        Assert.Same(ConflictMap.Empty, ConflictFinder.Find([]));
        Assert.Empty(ConflictMap.Empty.OverlapsWith(Timed("A", 0, 60)));
    }
}

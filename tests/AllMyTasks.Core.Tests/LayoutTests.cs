using AllMyTasks.Core.Views;
using static AllMyTasks.Core.Tests.ViewTestKit;

namespace AllMyTasks.Core.Tests;

public class DayAgendaTests
{
    private static readonly DateOnly Mon = new(2026, 10, 19);
    private static readonly DateTimeOffset Now = Utc_(2026, 10, 19, 12);

    [Fact]
    public void Splits_all_day_timed_and_overdue_and_marks_now()
    {
        var entries = new AgendaEntry[]
        {
            Event("morning", Utc_(2026, 10, 19, 9), Utc_(2026, 10, 19, 10)),
            Event("afternoon", Utc_(2026, 10, 19, 14), Utc_(2026, 10, 19, 15)),
            AllDay("holiday", Mon, Mon.AddDays(1)),
            Task("due today", Mon),
            Task("old", Mon.AddDays(-3), overdue: true),
        };

        var agenda = DayAgenda.Build(Mon, entries, Now, Utc);

        Assert.Equal(["holiday", "due today"], agenda.AllDay.Select(s => s.Entry.Title));
        Assert.Equal(["morning", "afternoon"], agenda.Timed.Select(s => s.Entry.Title));
        Assert.Equal(["old"], agenda.Overdue.Select(s => s.Entry.Title));
        Assert.Equal(1, agenda.NowIndex);
        Assert.True(agenda.Timed[0].IsPast(Now));
        Assert.False(agenda.Timed[1].IsPast(Now));
    }

    [Fact]
    public void Overdue_tasks_are_not_listed_on_other_days_or_their_original_day()
    {
        var old = Task("old", Mon.AddDays(-3), overdue: true);

        Assert.Empty(DayAgenda.Build(Mon.AddDays(-3), [old], Now, Utc).AllDay);
        Assert.True(DayAgenda.Build(Mon.AddDays(1), [old], Now, Utc).IsEmpty);
        Assert.Null(DayAgenda.Build(Mon.AddDays(1), [old], Now, Utc).NowIndex);
    }

    [Fact]
    public void Event_crossing_midnight_has_a_segment_on_each_day_with_continuation_flags()
    {
        var late = Event("late", Utc_(2026, 10, 19, 22), Utc_(2026, 10, 20, 2));

        var first = DayAgenda.Build(Mon, [late], Now, Utc).Timed.Single();
        var second = DayAgenda.Build(Mon.AddDays(1), [late], Now, Utc).Timed.Single();

        Assert.True(first.ContinuesAfter);
        Assert.False(first.ContinuedFromBefore);
        Assert.Equal(Utc_(2026, 10, 20), first.End);
        Assert.True(second.ContinuedFromBefore);
        Assert.Equal(Utc_(2026, 10, 20), second.Start);
    }

    [Fact]
    public void Multi_day_all_day_event_appears_on_every_day_it_covers()
    {
        var conf = AllDay("conf", Mon, Mon.AddDays(3));

        Assert.Single(DayAgenda.Build(Mon.AddDays(2), [conf], Now, Utc).AllDay);
        Assert.Empty(DayAgenda.Build(Mon.AddDays(3), [conf], Now, Utc).AllDay);
    }

    [Fact]
    public void Empty_day_is_empty() =>
        Assert.True(DayAgenda.Build(Mon, [], Now, Utc).IsEmpty);
}

public class WeekLayoutTests
{
    private static readonly DateOnly Mon = new(2026, 10, 19);
    private static readonly DateTimeOffset Now = Utc_(2026, 10, 19, 12);

    private static DayColumn Column(IEnumerable<AgendaEntry> entries, DateOnly? day = null) =>
        WeekLayout.Build(Mon, entries, Now, Utc).Days.Single(d => d.Date == (day ?? Mon));

    private static EventEntry At(string title, int startHour, int startMinute, int endHour, int endMinute) =>
        Event(title, Utc_(2026, 10, 19, startHour, startMinute), Utc_(2026, 10, 19, endHour, endMinute));

    [Fact]
    public void Non_overlapping_events_each_take_the_full_width()
    {
        var column = Column([At("a", 9, 0, 10, 0), At("b", 11, 0, 12, 0)]);

        Assert.All(column.Timed, p => Assert.Equal((0, 1), (p.Column, p.ColumnCount)));
    }

    [Fact]
    public void Events_that_only_touch_do_not_overlap()
    {
        var column = Column([At("a", 9, 0, 10, 0), At("b", 10, 0, 11, 0)]);

        Assert.All(column.Timed, p => Assert.Equal(1, p.ColumnCount));
    }

    [Fact]
    public void Chain_of_overlaps_shares_one_cluster_and_reuses_columns()
    {
        // a overlaps b, b overlaps c, a and c do not overlap: two columns, c reuses a's.
        var column = Column([At("a", 9, 0, 10, 0), At("b", 9, 30, 10, 30), At("c", 10, 0, 11, 0)]);

        var by = column.Timed.ToDictionary(p => p.Segment.Entry.Title);
        Assert.All(column.Timed, p => Assert.Equal(2, p.ColumnCount));
        Assert.Equal(0, by["a"].Column);
        Assert.Equal(1, by["b"].Column);
        Assert.Equal(0, by["c"].Column);
    }

    [Fact]
    public void Full_cluster_gets_one_column_per_event()
    {
        var column = Column([At("a", 9, 0, 11, 0), At("b", 9, 15, 11, 0), At("c", 9, 30, 11, 0)]);

        Assert.All(column.Timed, p => Assert.Equal(3, p.ColumnCount));
        Assert.Equal([0, 1, 2], column.Timed.Select(p => p.Column).Order());
    }

    [Fact]
    public void A_gap_starts_a_new_cluster()
    {
        var column = Column([At("a", 9, 0, 10, 0), At("b", 9, 30, 10, 30), At("c", 13, 0, 14, 0)]);

        Assert.Equal(1, column.Timed.Single(p => p.Segment.Entry.Title == "c").ColumnCount);
    }

    [Fact]
    public void Very_short_events_get_a_minimum_height()
    {
        var column = Column([At("point", 9, 0, 9, 0)]);

        Assert.Equal(WeekLayout.MinDisplayMinutes, column.Timed[0].EndMinute - column.Timed[0].StartMinute);
    }

    [Fact]
    public void Minutes_are_real_elapsed_time_on_dst_days()
    {
        var warsaw = TestData.Warsaw;
        var spring = new DateOnly(2026, 3, 23);
        var autumn = new DateOnly(2026, 10, 19);
        var springWeek = WeekLayout.Build(spring, [], new DateTimeOffset(2026, 3, 23, 0, 0, 0, TimeSpan.FromHours(1)), warsaw);
        var autumnWeek = WeekLayout.Build(autumn, [], new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.FromHours(2)), warsaw);

        Assert.Equal(1380, springWeek.Days.Single(d => d.Date == new DateOnly(2026, 3, 29)).TotalMinutes);
        Assert.Equal(1500, autumnWeek.Days.Single(d => d.Date == new DateOnly(2026, 10, 25)).TotalMinutes);
        Assert.Equal(1440, autumnWeek.Days[0].TotalMinutes);
    }

    [Fact]
    public void Event_after_the_spring_forward_gap_is_positioned_by_elapsed_time()
    {
        var warsaw = TestData.Warsaw;
        // 03:30 local on 2026-03-29 is only 150 elapsed minutes after midnight (02:00-03:00 does not exist).
        var entry = Event("early", new DateTimeOffset(2026, 3, 29, 3, 30, 0, TimeSpan.FromHours(2)), new DateTimeOffset(2026, 3, 29, 4, 30, 0, TimeSpan.FromHours(2)), warsaw);

        var layout = WeekLayout.Build(new DateOnly(2026, 3, 23), [entry], new DateTimeOffset(2026, 3, 23, 0, 0, 0, TimeSpan.FromHours(1)), warsaw);

        Assert.Equal(150, layout.Days.Single(d => d.Date == new DateOnly(2026, 3, 29)).Timed[0].StartMinute);
    }

    [Fact]
    public void Visible_hours_default_to_working_hours_and_widen_to_fit()
    {
        var plain = WeekLayout.Build(Mon, [At("a", 9, 0, 10, 0)], Now, Utc);
        var wide = WeekLayout.Build(Mon, [At("early", 6, 30, 7, 0), At("late", 21, 0, 22, 30)], Now, Utc);

        Assert.Equal((8, 20), (plain.VisibleStartHour, plain.VisibleEndHour));
        Assert.Equal((6, 23), (wide.VisibleStartHour, wide.VisibleEndHour));
    }

    [Fact]
    public void All_day_items_become_span_bars_with_lanes()
    {
        var layout = WeekLayout.Build(
            Mon,
            [AllDay("conf", Mon.AddDays(1), Mon.AddDays(4)), AllDay("holiday", Mon.AddDays(2), Mon.AddDays(3)), AllDay("single", Mon.AddDays(5), Mon.AddDays(6))],
            Now,
            Utc);

        var conf = layout.AllDaySpans.Single(b => b.Entry.Title == "conf");
        var holiday = layout.AllDaySpans.Single(b => b.Entry.Title == "holiday");
        var single = layout.AllDaySpans.Single(b => b.Entry.Title == "single");
        Assert.Equal((1, 3, 0), (conf.StartColumn, conf.ColumnSpan, conf.Lane));
        Assert.Equal(1, holiday.Lane);
        Assert.Equal(0, single.Lane);
        Assert.Equal(2, layout.AllDayLaneCount);
    }

    [Fact]
    public void Span_bars_are_clipped_to_the_week_and_flagged()
    {
        var layout = WeekLayout.Build(Mon, [AllDay("long", Mon.AddDays(-2), Mon.AddDays(10))], Now, Utc);

        var bar = Assert.Single(layout.AllDaySpans);
        Assert.Equal((0, 7, true, true), (bar.StartColumn, bar.ColumnSpan, bar.ContinuedFromBefore, bar.ContinuesAfter));
    }

    [Fact]
    public void Hiding_weekends_leaves_five_columns()
    {
        var layout = WeekLayout.Build(Mon, [AllDay("long", Mon, Mon.AddDays(7))], Now, Utc, hideWeekends: true);

        Assert.Equal(5, layout.Days.Count);
        Assert.Equal(5, layout.AllDaySpans.Single().ColumnSpan);
    }

    [Theory]
    [InlineData(DayOfWeek.Monday, "2026-10-19")]
    [InlineData(DayOfWeek.Sunday, "2026-10-18")]
    [InlineData(DayOfWeek.Saturday, "2026-10-17")]
    public void Start_of_week_follows_the_chosen_first_day(DayOfWeek first, string expected) =>
        Assert.Equal(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), WeekLayout.StartOfWeek(new DateOnly(2026, 10, 21), first));
}

public class MonthGridTests
{
    private static readonly DateTimeOffset Now = Utc_(2026, 10, 19, 12);

    [Theory]
    [InlineData(2026, 2, DayOfWeek.Sunday)]
    [InlineData(2026, 2, DayOfWeek.Monday)]
    [InlineData(2026, 3, DayOfWeek.Monday)]
    [InlineData(2026, 4, DayOfWeek.Monday)]
    [InlineData(2026, 5, DayOfWeek.Monday)]
    [InlineData(2026, 6, DayOfWeek.Sunday)]
    [InlineData(2026, 8, DayOfWeek.Monday)]
    [InlineData(2026, 11, DayOfWeek.Saturday)]
    public void Always_six_rows_of_seven_days_starting_on_the_first_weekday(int year, int month, DayOfWeek first)
    {
        var grid = MonthGrid.Build(new DateOnly(year, month, 15), [], Now, Utc, first);

        Assert.Equal(6, grid.Rows.Count);
        Assert.All(grid.Rows, r => Assert.Equal(7, r.Days.Count));
        Assert.Equal(first, grid.Rows[0].Days[0].Date.DayOfWeek);
        Assert.Contains(grid.Rows[0].Days, d => d.Date == new DateOnly(year, month, 1));
        Assert.Equal(42, grid.Rows.SelectMany(r => r.Days).Select(d => d.Date).Distinct().Count());
        Assert.Equal(DateTime.DaysInMonth(year, month), grid.Rows.SelectMany(r => r.Days).Count(d => d.IsCurrentMonth));
    }

    [Fact]
    public void Adjacent_month_days_include_their_entries_and_today_is_flagged()
    {
        // October 2026 starts on a Thursday; with Monday first, 28 September is in the first row.
        var entry = Event("trailing", Utc_(2026, 9, 29, 10), Utc_(2026, 9, 29, 11));

        var grid = MonthGrid.Build(new DateOnly(2026, 10, 1), [entry], Now, Utc, DayOfWeek.Monday);

        var cell = grid.Rows[0].Days.Single(d => d.Date == new DateOnly(2026, 9, 29));
        Assert.False(cell.IsCurrentMonth);
        Assert.Single(cell.Items);
        Assert.Single(grid.Rows.SelectMany(r => r.Days), d => d.IsToday);
    }

    [Fact]
    public void All_day_events_are_bars_split_per_row_and_tasks_are_cell_items()
    {
        // Thu 29 Oct - Tue 3 Nov crosses a row boundary (Monday first).
        var conf = AllDay("conf", new DateOnly(2026, 10, 29), new DateOnly(2026, 11, 4));
        var task = Task("t", new DateOnly(2026, 10, 30));

        var grid = MonthGrid.Build(new DateOnly(2026, 10, 1), [conf, task], Now, Utc, DayOfWeek.Monday);

        var rowWithStart = grid.Rows.Single(r => r.SpanBars.Any(b => !b.ContinuedFromBefore));
        var first = rowWithStart.SpanBars.Single();
        Assert.Equal((3, 4, true), (first.StartColumn, first.ColumnSpan, first.ContinuesAfter));
        var next = grid.Rows[grid.Rows.ToList().IndexOf(rowWithStart) + 1].SpanBars.Single();
        Assert.Equal((0, 2, true), (next.StartColumn, next.ColumnSpan, next.ContinuedFromBefore));
        Assert.Contains(grid.Rows.SelectMany(r => r.Days), d => d.Date == new DateOnly(2026, 10, 30) && d.Items.Count == 1);
    }

    [Fact]
    public void Overlapping_bars_get_different_lanes()
    {
        var a = AllDay("a", new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 8));
        var b = AllDay("b", new DateOnly(2026, 10, 6), new DateOnly(2026, 10, 9));

        var row = MonthGrid.Build(new DateOnly(2026, 10, 1), [a, b], Now, Utc, DayOfWeek.Monday).Rows.Single(r => r.SpanBars.Count == 2);

        Assert.Equal([0, 1], row.SpanBars.Select(s => s.Lane).Order());
        Assert.Equal(2, row.LaneCount);
    }
}

public class UpcomingListTests
{
    private static readonly DateOnly Mon = new(2026, 10, 19);
    private static readonly DateTimeOffset Now = Utc_(2026, 10, 19, 12);

    [Fact]
    public void Groups_overdue_today_tomorrow_then_dates_and_skips_empty_days()
    {
        var entries = new AgendaEntry[]
        {
            Task("old", Mon.AddDays(-4), overdue: true),
            Event("today", Utc_(2026, 10, 19, 15), Utc_(2026, 10, 19, 16)),
            Event("tomorrow", Utc_(2026, 10, 20, 9), Utc_(2026, 10, 20, 10)),
            Event("friday", Utc_(2026, 10, 23, 9), Utc_(2026, 10, 23, 10)),
        };

        var list = UpcomingList.Build(entries, Now, Utc);

        Assert.Equal([DayGroupKind.Overdue, DayGroupKind.Today, DayGroupKind.Tomorrow, DayGroupKind.Date], list.Groups.Select(g => g.Kind));
        Assert.Equal(Mon.AddDays(4), list.Groups[3].Date);
    }

    [Fact]
    public void Events_that_already_ended_today_are_not_listed_but_running_ones_are()
    {
        var entries = new AgendaEntry[]
        {
            Event("over", Utc_(2026, 10, 19, 9), Utc_(2026, 10, 19, 10)),
            Event("running", Utc_(2026, 10, 19, 11), Utc_(2026, 10, 19, 13)),
        };

        var list = UpcomingList.Build(entries, Now, Utc);

        Assert.Equal(["running"], list.Groups.Single().Segments.Select(s => s.Entry.Title));
    }

    [Fact]
    public void Within_a_day_all_day_comes_first_then_time_order()
    {
        var entries = new AgendaEntry[]
        {
            Event("late", Utc_(2026, 10, 19, 18), Utc_(2026, 10, 19, 19)),
            Event("soon", Utc_(2026, 10, 19, 13), Utc_(2026, 10, 19, 14)),
            AllDay("holiday", Mon, Mon.AddDays(1)),
        };

        var segments = UpcomingList.Build(entries, Now, Utc).Groups.Single().Segments;

        Assert.Equal(["holiday", "soon", "late"], segments.Select(s => s.Entry.Title));
    }

    [Fact]
    public void Range_is_limited_to_the_requested_number_of_days_and_empty_list_is_empty()
    {
        var far = Event("far", Utc_(2026, 11, 30, 9), Utc_(2026, 11, 30, 10));

        Assert.True(UpcomingList.Build([far], Now, Utc, days: 14).IsEmpty);
        Assert.False(UpcomingList.Build([far], Now, Utc, days: 60).IsEmpty);
    }

    [Fact]
    public void Multi_day_event_is_listed_under_each_remaining_day()
    {
        var conf = AllDay("conf", Mon.AddDays(-1), Mon.AddDays(2));

        var groups = UpcomingList.Build([conf], Now, Utc).Groups;

        Assert.Equal([DayGroupKind.Today, DayGroupKind.Tomorrow], groups.Select(g => g.Kind));
    }
}

public class ColorUtilTests
{
    [Theory]
    [InlineData("#ffffff", "#000000")]
    [InlineData("#000000", "#FFFFFF")]
    [InlineData("#3b82f6", "#000000")]
    [InlineData("#1e3a8a", "#FFFFFF")]
    [InlineData("#f59e0b", "#000000")]
    [InlineData("nonsense", "#FFFFFF")]
    [InlineData(null, "#FFFFFF")]
    public void Chooses_the_more_readable_text_colour(string? background, string expected) =>
        Assert.Equal(expected, ColorUtil.ContrastingText(background));

    [Theory]
    [InlineData("#112233", "#112233")]
    [InlineData("112233", "#112233")]
    [InlineData("#12", ColorUtil.DefaultColor)]
    [InlineData(null, ColorUtil.DefaultColor)]
    public void Falls_back_to_a_neutral_colour(string? input, string expected) =>
        Assert.Equal(expected, ColorUtil.OrDefault(input));

    [Fact]
    public void Contrast_of_black_on_white_is_21() =>
        Assert.Equal(21, ColorUtil.Contrast((0, 0, 0), (255, 255, 255)), precision: 6);
}

using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Search;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Tasks;
using AllMyTasks.Core.Views;

namespace AllMyTasks.Core.Tests;

public class SearchServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static CalendarEvent Event(string id, string title, DateTimeOffset start, string? description = null, string? location = null, string collection = "cal", Recurrence? recurrence = null, IReadOnlyList<Attendee>? attendees = null) =>
        new(new ItemKey("s", collection, id), title, new TimedEvent(start, start.AddHours(1)), description, location, Recurrence: recurrence, Attendees: attendees);

    private static TaskItem Todo(string id, string title, string? notes = null, TaskDue? due = null, string[]? labels = null, string collection = "list") =>
        new(new ItemKey("s", collection, id), title, notes, due, Labels: labels);

    private static async Task<SearchService> ServiceAsync(IEnumerable<CalendarEvent> events, IEnumerable<TaskItem> tasks, bool listVisible = true)
    {
        var store = new InMemoryStore();
        await store.SaveCollectionsAsync(
            new SourceInfo("s", "test", "Test source"),
            [
                new CollectionInfo("s", "cal", "Calendar", CollectionKind.Calendar, "#112233"),
                new CollectionInfo("s", "hidden", "Secret calendar", CollectionKind.Calendar, IsVisible: false),
                new CollectionInfo("s", "list", "Work", CollectionKind.TaskList, "#445566", IsVisible: listVisible),
            ],
            TestContext.Current.CancellationToken);
        foreach (var group in events.GroupBy(e => e.Key.CollectionId))
        {
            await store.ApplyAsync("s", group.Key, SyncResult.Snapshot<CalendarEvent>([.. group], new SyncState("x")), TestContext.Current.CancellationToken);
        }

        foreach (var group in tasks.GroupBy(t => t.Key.CollectionId))
        {
            await store.ApplyAsync("s", group.Key, SyncResult.Snapshot<TaskItem>([.. group], new SyncState("x")), TestContext.Current.CancellationToken);
        }

        return new SearchService(store, store, new FakeRecurrenceExpander(), new FixedClock(Now));
    }

    private static Task<SearchResults> Search(SearchService service, string text, int limit = 100, IReadOnlySet<CollectionRef>? collections = null) =>
        service.SearchAsync(new SearchQuery(text, collections, limit), Utc, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Finds_events_and_tasks_by_their_text_fields()
    {
        var service = await ServiceAsync(
            [
                Event("1", "Budget review", Now.AddDays(1)),
                Event("2", "Lunch", Now.AddDays(2), description: "with the budget team"),
                Event("3", "Dentist", Now.AddDays(3), location: "Budgetowa 5"),
                Event("4", "Unrelated", Now.AddDays(4)),
                Event("5", "Offsite", Now.AddDays(5), attendees: [new Attendee("Bud Getty", "bud@example.com")]),
            ],
            [
                Todo("a", "Prepare budget"),
                Todo("b", "Pay rent", notes: "from the BUDGET account"),
                Todo("c", "Call bank", labels: ["budgeting"]),
                Todo("d", "Wash car"),
            ]);

        var results = await Search(service, "budget");

        Assert.Equal(["Budget review", "Lunch", "Dentist"], results.Events.Select(e => e.Event.Title));
        Assert.Equal(["Call bank", "Pay rent", "Prepare budget"], results.Tasks.Select(t => t.Task.Title).Order());
        Assert.Equal(["Offsite"], (await Search(service, "bud getty")).Events.Select(e => e.Event.Title));
        Assert.Equal(["Offsite"], (await Search(service, "example.com")).Events.Select(e => e.Event.Title));
    }

    [Fact]
    public async Task Collection_name_is_searched_and_a_blank_query_finds_nothing()
    {
        var service = await ServiceAsync([Event("1", "Standup", Now.AddDays(1))], [Todo("a", "Invoice")]);

        Assert.Equal(["Standup"], (await Search(service, "calendar")).Events.Select(e => e.Event.Title));
        Assert.Equal(["Invoice"], (await Search(service, "work")).Tasks.Select(t => t.Task.Title));
        Assert.Equal(0, (await Search(service, "  ")).Count);
    }

    [Fact]
    public async Task Events_list_upcoming_first_then_past_most_recent_first()
    {
        var service = await ServiceAsync(
            [
                Event("1", "sync old", Now.AddDays(-10)),
                Event("2", "sync later", Now.AddDays(10)),
                Event("3", "sync recent", Now.AddDays(-1)),
                Event("4", "sync soon", Now.AddDays(1)),
            ],
            []);

        var results = await Search(service, "sync");

        Assert.Equal(["sync soon", "sync later", "sync recent", "sync old"], results.Events.Select(e => e.Event.Title));
        Assert.Equal([false, false, true, true], results.Events.Select(e => e.IsPast));
    }

    [Fact]
    public async Task Tasks_sort_by_due_with_undated_last()
    {
        var service = await ServiceAsync(
            [],
            [
                Todo("1", "x none"),
                Todo("2", "x late", due: new DueDate(new DateOnly(2026, 11, 30))),
                Todo("3", "x early", due: new DueDate(new DateOnly(2026, 10, 20))),
            ]);

        Assert.Equal(["x early", "x late", "x none"], (await Search(service, "x")).Tasks.Select(t => t.Task.Title));
    }

    [Fact]
    public async Task A_recurring_series_is_one_hit_at_its_next_occurrence()
    {
        var start = Now.AddDays(-30);
        var series = Event("r", "Weekly sync", start, recurrence: new Recurrence("FREQ=WEEKLY", [Now.AddDays(3), Now.AddDays(10)], []));
        var service = await ServiceAsync([series], []);

        var hit = Assert.Single((await Search(service, "weekly")).Events);

        Assert.True(hit.IsRecurring);
        Assert.Equal(Now.AddDays(3), hit.Start);
        Assert.False(hit.IsPast);
    }

    [Fact]
    public async Task A_series_that_has_ended_shows_its_latest_occurrence_as_past()
    {
        var series = Event("r", "Old series", Now.AddDays(-30), recurrence: new Recurrence("FREQ=WEEKLY", [Now.AddDays(-20)], []));
        var service = await ServiceAsync([series], []);

        var hit = Assert.Single((await Search(service, "series")).Events);

        Assert.True(hit.IsPast);
        Assert.Equal(Now.AddDays(-20), hit.Start);
    }

    [Fact]
    public async Task Hidden_collections_are_not_searched_but_counted()
    {
        var service = await ServiceAsync(
            [Event("1", "Plan visible", Now.AddDays(1)), Event("2", "Plan secret", Now.AddDays(1), collection: "hidden")],
            [Todo("a", "Plan list")],
            listVisible: false);

        var results = await Search(service, "plan");

        Assert.Equal(["Plan visible"], results.Events.Select(e => e.Event.Title));
        Assert.Empty(results.Tasks);
        Assert.Equal(2, results.HiddenByFilter);
    }

    [Fact]
    public async Task Limit_applies_to_events_and_tasks_separately()
    {
        var service = await ServiceAsync(
            [Event("1", "go a", Now.AddDays(1)), Event("2", "go b", Now.AddDays(2)), Event("3", "go c", Now.AddDays(3))],
            [Todo("a", "go 1"), Todo("b", "go 2"), Todo("c", "go 3")]);

        var results = await Search(service, "go", limit: 2);

        Assert.Equal(["go a", "go b"], results.Events.Select(e => e.Event.Title));
        Assert.Equal(2, results.Tasks.Count);
    }

    [Fact]
    public async Task Search_can_be_limited_to_collections()
    {
        var service = await ServiceAsync([Event("1", "Shared", Now.AddDays(1))], [Todo("a", "Shared")]);

        var onlyList = await Search(service, "shared", collections: new HashSet<CollectionRef> { new("s", "list") });

        Assert.Empty(onlyList.Events);
        Assert.Single(onlyList.Tasks);
    }

    [Fact]
    public async Task Hits_carry_collection_and_source_names()
    {
        var service = await ServiceAsync([], [Todo("a", "Colourful")]);

        var hit = Assert.Single((await Search(service, "colourful")).Tasks);

        Assert.Equal("Work", hit.CollectionName);
        Assert.Equal("Test source", hit.SourceName);
        Assert.Equal("#445566", hit.Color);
    }
}

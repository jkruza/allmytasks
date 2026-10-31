using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Tasks;
using AllMyTasks.Core.Views;

namespace AllMyTasks.Core.Tests;

public class SavedFilterTests
{
    private static SavedTaskFilter Sample(string name = "Work, due this week", Guid? id = null) => new(
        id ?? Guid.NewGuid(),
        name,
        TaskFilter.Default with
        {
            Status = TaskStatusFilter.All,
            Due = new DueFilter(new HashSet<DueOption> { DueOption.Next7Days, DueOption.CustomRange }, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30)),
            Collections = new HashSet<CollectionRef> { new("todoist", "inbox"), new("google", "list/1") },
            Priorities = new HashSet<Priority> { Priority.High, Priority.Medium },
            Labels = new HashSet<string>(["Work", "urgent"], StringComparer.OrdinalIgnoreCase),
            AllLabels = true,
            Text = "report",
        },
        new TaskSort(TaskSortField.Priority, Descending: true),
        TaskGrouping.Collection);

    [Fact]
    public void Round_trips_through_json()
    {
        var original = new[] { Sample("One"), Sample("Two") };

        var loaded = SavedFilterStore.Deserialize(SavedFilterStore.Serialize(original));

        Assert.Equal(["One", "Two"], loaded.Select(f => f.Name));
        Assert.Equal(original.Select(f => f.Id), loaded.Select(f => f.Id));
        Assert.True(loaded[0].Matches(original[0].Filter, original[0].Sort, original[0].Grouping));
        Assert.Equal(new DateOnly(2026, 11, 1), loaded[0].Filter.Due.From);
        Assert.Equal("report", loaded[0].Filter.Text);
        Assert.True(loaded[0].Filter.AllLabels);
        Assert.True(loaded[0].Filter.Labels.Contains("URGENT"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"version\":1}")]
    [InlineData("{\"version\":99,\"filters\":[]}")]
    [InlineData("{\"filters\":[]}")]
    public void Missing_damaged_or_newer_data_gives_an_empty_list(string? json) =>
        Assert.Empty(SavedFilterStore.Deserialize(json));

    [Fact]
    public void Unknown_fields_are_tolerated_and_bad_entries_skipped()
    {
        var good = Guid.NewGuid();
        var json = $$"""
            {"version":1,"future":true,"filters":[
              {"id":"{{good}}","name":"Kept","status":"Completed","somethingNew":42,"sortField":"Title"},
              {"id":"{{Guid.NewGuid()}}","name":"Bad enum","status":"Sideways"},
              {"id":"{{Guid.NewGuid()}}","name":"  "},
              {"name":"No id"},
              "not an object"
            ]}
            """;

        var loaded = SavedFilterStore.Deserialize(json);

        var kept = Assert.Single(loaded);
        Assert.Equal(good, kept.Id);
        Assert.Equal(TaskStatusFilter.Completed, kept.Filter.Status);
        Assert.Equal(TaskSortField.Title, kept.Sort.Field);
    }

    [Fact]
    public async Task Store_saves_loads_and_keeps_the_users_order()
    {
        var settings = new InMemorySettingsStore();
        var store = new SavedFilterStore(settings);
        Assert.Empty(await store.LoadAsync(TestContext.Current.CancellationToken));

        var a = Sample("A");
        var b = Sample("B");
        await store.SaveAsync([b, a], TestContext.Current.CancellationToken);

        Assert.Equal(["B", "A"], (await store.LoadAsync(TestContext.Current.CancellationToken)).Select(f => f.Name));
        Assert.NotNull(await settings.GetAsync(SavedFilterStore.Key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Rename_keeps_identity_and_matches_ignores_order_and_case_of_choices()
    {
        var saved = Sample();
        var renamed = saved with { Name = "Renamed" };

        Assert.Equal(saved.Id, renamed.Id);
        Assert.True(renamed.Matches(
            saved.Filter with { Labels = new HashSet<string>(["URGENT", "work"], StringComparer.OrdinalIgnoreCase), Text = "  report " },
            saved.Sort,
            saved.Grouping));
        Assert.False(saved.Matches(saved.Filter with { Status = TaskStatusFilter.Open }, saved.Sort, saved.Grouping));
        Assert.False(saved.Matches(saved.Filter, saved.Sort with { Descending = false }, saved.Grouping));
        Assert.False(saved.Matches(saved.Filter, saved.Sort, TaskGrouping.None));
        Assert.Throws<ArgumentException>(() => saved with { Name = " " });
    }

    [Fact]
    public void Removed_collections_are_dropped_and_flagged_but_other_conditions_stay()
    {
        var saved = Sample();

        var someGone = saved.WithoutMissing(new HashSet<CollectionRef> { new("todoist", "inbox") });
        var allGone = saved.WithoutMissing(new HashSet<CollectionRef>());
        var none = saved.WithoutMissing(saved.Filter.Collections);

        Assert.Equal(new[] { new CollectionRef("todoist", "inbox") }, someGone.Filter.Collections.ToArray());
        Assert.True(someGone.HasMissingCollections);
        Assert.Empty(allGone.Filter.Collections);
        Assert.True(allGone.HasMissingCollections);
        Assert.Equal(Priority.High, allGone.Filter.Priorities.Max());
        Assert.Equal("report", allGone.Filter.Text);
        Assert.Same(saved, none);
        Assert.False(none.HasMissingCollections);
    }

    [Fact]
    public void Dropped_collections_are_not_stored_with_the_flag()
    {
        var flagged = Sample().WithoutMissing(new HashSet<CollectionRef>());

        var loaded = Assert.Single(SavedFilterStore.Deserialize(SavedFilterStore.Serialize([flagged])));

        Assert.False(loaded.HasMissingCollections);
    }
}

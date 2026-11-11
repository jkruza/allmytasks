using AllMyTasks.Core.Model;
using AllMyTasks.Core.Onboarding;
using AllMyTasks.Core.Samples;
using AllMyTasks.Core.Settings;
using AllMyTasks.Core.Sync;

namespace AllMyTasks.Core.Tests;

public class OnboardingStateTests
{
    private static readonly SourceInfo Todoist = new("t1", "todoist", "Todoist");
    private static readonly SourceInfo Sample = new("s1", SampleSourceFactory.KindName, "Sample data");
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);

    private static OnboardingState Compute(SourceInfo[] sources, SourceStatus[] statuses, AppSettings? settings = null) =>
        OnboardingState.Compute(sources, statuses, settings ?? new AppSettings());

    private static OnboardingStep Step(OnboardingState state, OnboardingStepId id) => state.Steps.Single(s => s.Id == id);

    [Fact]
    public void With_no_sources_the_welcome_state_shows_and_nothing_is_done()
    {
        var state = Compute([], []);

        Assert.True(state.IsWelcome);
        Assert.True(state.IsChecklistVisible);
        Assert.False(Step(state, OnboardingStepId.AddSource).Done);
        Assert.False(Step(state, OnboardingStepId.FirstSync).Done);
        Assert.False(state.IsFirstSyncPending);
    }

    [Fact]
    public void A_source_that_is_syncing_ticks_the_first_step_and_notes_the_second()
    {
        var state = Compute([Todoist], [new SourceStatus("t1", SourceStatusKind.Syncing)]);

        Assert.False(state.IsWelcome);
        Assert.True(Step(state, OnboardingStepId.AddSource).Done);
        var sync = Step(state, OnboardingStepId.FirstSync);
        Assert.False(sync.Done);
        Assert.Equal("syncing now", sync.Note);
        Assert.True(state.IsChecklistVisible);
        Assert.True(state.IsFirstSyncPending);
    }

    [Fact]
    public void A_source_that_has_not_started_yet_is_waiting()
    {
        var state = Compute([Todoist], []);

        Assert.Equal("waiting", Step(state, OnboardingStepId.FirstSync).Note);
    }

    [Theory]
    [InlineData(SourceStatusKind.Error, "it failed, see Sources")]
    [InlineData(SourceStatusKind.Offline, "it failed, see Sources")]
    [InlineData(SourceStatusKind.NeedsAttention, "the source needs attention, see Sources")]
    public void A_failed_first_sync_stays_open_and_points_to_Sources(SourceStatusKind kind, string note)
    {
        var state = Compute([Todoist], [new SourceStatus("t1", kind)]);

        var sync = Step(state, OnboardingStepId.FirstSync);
        Assert.False(sync.Done);
        Assert.Equal(note, sync.Note);
        Assert.True(state.IsChecklistVisible);
    }

    [Fact]
    public void A_synced_source_completes_the_mandatory_steps_and_hides_the_checklist()
    {
        var state = Compute([Todoist], [new SourceStatus("t1", SourceStatusKind.Ok, Now)]);

        Assert.True(state.MandatoryDone);
        Assert.False(state.IsChecklistVisible);
        Assert.False(state.IsFirstSyncPending);
        Assert.Equal(string.Empty, Step(state, OnboardingStepId.FirstSync).Note);
    }

    [Fact]
    public void Once_synced_a_later_failure_does_not_bring_the_checklist_back()
    {
        var state = Compute([Todoist], [new SourceStatus("t1", SourceStatusKind.Error, Now, "x")]);

        Assert.False(state.IsChecklistVisible);
    }

    [Fact]
    public void Dismissing_hides_the_checklist_but_not_the_welcome_state()
    {
        var welcome = Compute([], [], new AppSettings { OnboardingDismissed = true });
        var syncing = Compute([Todoist], [], new AppSettings { OnboardingDismissed = true });

        Assert.True(welcome.IsWelcome);
        Assert.False(welcome.IsChecklistVisible);
        Assert.False(syncing.IsChecklistVisible);
    }

    [Fact]
    public void The_sample_source_is_not_a_source_added()
    {
        var state = Compute([Sample], [new SourceStatus("s1", SourceStatusKind.Ok, Now)]);

        Assert.False(state.IsWelcome);
        Assert.False(Step(state, OnboardingStepId.AddSource).Done);
        Assert.False(Step(state, OnboardingStepId.FirstSync).Done);
        Assert.True(state.IsChecklistVisible);
        Assert.False(state.IsFirstSyncPending);
    }

    [Fact]
    public void A_real_source_next_to_the_sample_counts_by_itself()
    {
        var state = Compute([Sample, Todoist], [new SourceStatus("s1", SourceStatusKind.Ok, Now), new SourceStatus("t1", SourceStatusKind.Syncing)]);

        Assert.True(Step(state, OnboardingStepId.AddSource).Done);
        Assert.False(Step(state, OnboardingStepId.FirstSync).Done);
    }

    [Fact]
    public void Optional_steps_follow_the_settings_and_never_keep_the_checklist_open()
    {
        var off = Compute([Todoist], [new SourceStatus("t1", SourceStatusKind.Ok, Now)], new AppSettings { RemindersEnabled = false, LaunchAtLogin = false });
        var on = Compute([Todoist], [new SourceStatus("t1", SourceStatusKind.Ok, Now)], new AppSettings { RemindersEnabled = true, LaunchAtLogin = true });

        Assert.False(Step(off, OnboardingStepId.Reminders).Done);
        Assert.False(Step(off, OnboardingStepId.StartAtLogin).Done);
        Assert.True(Step(on, OnboardingStepId.Reminders).Done);
        Assert.True(Step(on, OnboardingStepId.StartAtLogin).Done);
        Assert.All(off.Steps.Where(s => s.Id is OnboardingStepId.Reminders or OnboardingStepId.StartAtLogin), s => Assert.True(s.Optional));
        Assert.False(off.IsChecklistVisible);
    }

    [Fact]
    public void Statuses_of_removed_sources_are_ignored()
    {
        var state = Compute([Todoist], [new SourceStatus("gone", SourceStatusKind.Ok, Now)]);

        Assert.False(Step(state, OnboardingStepId.FirstSync).Done);
    }

    [Fact]
    public void The_sample_source_factory_is_the_only_kind_that_needs_nothing()
    {
        Assert.Equal("sample", SampleSourceFactory.KindName);
        Assert.False(string.IsNullOrWhiteSpace(new SampleSourceFactory(TimeProvider.System, TimeZoneInfo.Utc).ConfigSchema.WhatYouNeed));
        Assert.False(string.IsNullOrWhiteSpace(SourceConfigSchema.Empty.WhatYouNeed));
    }
}

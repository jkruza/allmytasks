using AllMyTasks.Core.Model;
using AllMyTasks.Core.Samples;
using AllMyTasks.Core.Settings;
using AllMyTasks.Core.Sync;

namespace AllMyTasks.Core.Onboarding;

public enum OnboardingStepId
{
    AddSource,
    FirstSync,
    Reminders,
    StartAtLogin,
}

/// <summary>One line of the first-run checklist.</summary>
/// <param name="Text">What the step is, for example "Add a source".</param>
/// <param name="Done">Whether it is ticked.</param>
/// <param name="Optional">Optional steps never keep the checklist on screen.</param>
/// <param name="Note">A short remark while the step is not done, such as "waiting for the first sync"; empty otherwise.</param>
public sealed record OnboardingStep(OnboardingStepId Id, string Text, bool Done, bool Optional, string Note = "");

/// <summary>
/// The first-run state: whether the welcome screen shows, and the checklist with its ticks. Computed from the source list,
/// the saved sync status and the settings, so the steps tick themselves and nothing else has to be stored except whether
/// the user dismissed it. Pure; see docs/design/first-run-onboarding.md.
/// </summary>
public sealed record OnboardingState(bool IsWelcome, IReadOnlyList<OnboardingStep> Steps, bool Dismissed)
{
    /// <summary>The mandatory steps are done: a real source was added and it has synced once.</summary>
    public bool MandatoryDone => Steps.Where(s => !s.Optional).All(s => s.Done);

    /// <summary>The checklist shows until the mandatory steps are done, unless the user dismissed it.</summary>
    public bool IsChecklistVisible => !Dismissed && !MandatoryDone;

    /// <summary>The first source was added but has not synced yet (or the sync failed), so views may still be empty.</summary>
    public bool IsFirstSyncPending => Steps.Any(s => s is { Id: OnboardingStepId.FirstSync, Done: false }) && !IsWelcome && HasRealSource;

    public bool HasRealSource => Steps.Any(s => s is { Id: OnboardingStepId.AddSource, Done: true });

    /// <summary>
    /// <paramref name="sources"/> are all configured sources and <paramref name="statuses"/> their last known status. The
    /// sample source never counts as "a source added": it is the way to look around, not the goal.
    /// </summary>
    public static OnboardingState Compute(IReadOnlyList<SourceInfo> sources, IReadOnlyList<SourceStatus> statuses, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(statuses);
        ArgumentNullException.ThrowIfNull(settings);

        var real = sources.Where(s => s.Kind != SampleSourceFactory.KindName).ToList();
        var realStatuses = statuses.Where(s => real.Any(r => r.Id == s.SourceId)).ToList();
        var synced = realStatuses.Any(s => s.LastSyncedAt is not null);

        string firstSyncNote;
        if (real.Count == 0 || synced)
        {
            firstSyncNote = string.Empty;
        }
        else if (realStatuses.Any(s => s.Kind == SourceStatusKind.Syncing))
        {
            firstSyncNote = "syncing now";
        }
        else if (realStatuses.Any(s => s.Kind == SourceStatusKind.NeedsAttention))
        {
            firstSyncNote = "the source needs attention, see Sources";
        }
        else if (realStatuses.Any(s => s.Kind is SourceStatusKind.Error or SourceStatusKind.Offline))
        {
            firstSyncNote = "it failed, see Sources";
        }
        else
        {
            firstSyncNote = "waiting";
        }

        List<OnboardingStep> steps =
        [
            new(OnboardingStepId.AddSource, "Add a source", real.Count > 0, Optional: false),
            new(OnboardingStepId.FirstSync, "First sync", synced, Optional: false, firstSyncNote),
            new(OnboardingStepId.Reminders, "Reminders on", settings.RemindersEnabled, Optional: true),
            new(OnboardingStepId.StartAtLogin, "Start at login", settings.LaunchAtLogin, Optional: true),
        ];
        return new OnboardingState(sources.Count == 0, steps, settings.OnboardingDismissed);
    }
}

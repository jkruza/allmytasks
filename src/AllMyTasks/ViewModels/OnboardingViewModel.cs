using System.Collections.ObjectModel;
using AllMyTasks.Core.Onboarding;
using AllMyTasks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AllMyTasks.ViewModels;

/// <summary>One line of the checklist: a tick or a circle, the text and a note while it is not done.</summary>
public sealed record OnboardingStepItem(string Glyph, string Text, bool Done)
{
    public double Opacity => Done ? 0.6 : 1;
}

/// <summary>
/// The welcome state and the first-run checklist (docs/design/first-run-onboarding.md). Thin: the state is computed in Core
/// by <see cref="OnboardingState"/>; this only shows it and forwards the three actions.
/// </summary>
public sealed partial class OnboardingViewModel : ViewModelBase
{
    private readonly SettingsService? _settings;
    private readonly Func<Task>? _addSource;
    private readonly Func<Task>? _addSample;

    public OnboardingViewModel(SettingsService? settings = null, Func<Task>? addSource = null, Func<Task>? addSample = null)
    {
        _settings = settings;
        _addSource = addSource;
        _addSample = addSample;
    }

    public ObservableCollection<OnboardingStepItem> Steps { get; } = [];

    /// <summary>No source is configured: the welcome message shows in place of the calendar.</summary>
    [ObservableProperty]
    public partial bool IsWelcome { get; set; }

    /// <summary>The checklist strip shows.</summary>
    [ObservableProperty]
    public partial bool IsChecklistVisible { get; set; }

    /// <summary>The first source was added but has not synced, so views may still be empty.</summary>
    [ObservableProperty]
    public partial bool IsFirstSyncPending { get; set; }

    public bool CanTrySample => _addSample is not null;

    public void Apply(OnboardingState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        IsWelcome = state.IsWelcome;
        IsChecklistVisible = state.IsChecklistVisible;
        IsFirstSyncPending = state.IsFirstSyncPending;
        Steps.Clear();
        foreach (var step in state.Steps)
        {
            var text = step.Note.Length > 0 && !step.Done ? $"{step.Text} ({step.Note})" : step.Text;
            Steps.Add(new OnboardingStepItem(step.Done ? "✓" : "○", text + (step.Optional ? " (optional)" : string.Empty), step.Done));
        }
    }

    [RelayCommand]
    private Task AddSourceAsync() => _addSource?.Invoke() ?? Task.CompletedTask;

    [RelayCommand]
    private Task TrySampleAsync() => _addSample?.Invoke() ?? Task.CompletedTask;

    /// <summary>Hides the checklist for good.</summary>
    [RelayCommand]
    private void Dismiss() => _settings?.Update(s => s with { OnboardingDismissed = true });
}

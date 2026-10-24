using System.Globalization;
using AllMyTasks.Core.Settings;
using AllMyTasks.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AllMyTasks.ViewModels;

/// <summary>One choice in the week start drop-down.</summary>
public sealed record WeekStartOption(WeekStartSetting Value, string Label);

/// <summary>The Settings dialog. Changes are applied and saved as they are made.</summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settings;

    public SettingsViewModel(SettingsService settings)
    {
        _settings = settings;
        var system = WeekStart.Resolve(WeekStartSetting.System, CultureInfo.CurrentCulture);
        WeekStartOptions =
        [
            new(WeekStartSetting.System, $"System default ({system})"),
            new(WeekStartSetting.Sunday, "Sunday"),
            new(WeekStartSetting.Monday, "Monday"),
        ];
        SelectedWeekStart = WeekStartOptions.First(o => o.Value == settings.Current.WeekStart);
    }

    public IReadOnlyList<WeekStartOption> WeekStartOptions { get; }

    [ObservableProperty]
    public partial WeekStartOption SelectedWeekStart { get; set; }

    partial void OnSelectedWeekStartChanged(WeekStartOption value) =>
        _settings.Update(s => s with { WeekStart = value.Value });
}

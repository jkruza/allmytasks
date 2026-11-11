using AllMyTasks.Core.Updates;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AllMyTasks.ViewModels;

/// <summary>The quiet "a new version is available" banner. Docs: docs/design/update-check.md.</summary>
public sealed partial class UpdateBannerViewModel : ViewModelBase, IDisposable
{
    private readonly UpdateCheckService _service;

    public UpdateBannerViewModel(UpdateCheckService service)
    {
        _service = service;
        _service.Changed += OnChanged;
        Apply();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    public partial string Text { get; set; } = string.Empty;

    /// <summary>The release page to open, or null when there is no update.</summary>
    public string? Url { get; private set; }

    public bool IsVisible => Text.Length > 0;

    /// <summary>Hides this version, and older ones, for good.</summary>
    [RelayCommand]
    private async Task SkipAsync()
    {
        if (_service.Available is { } update)
        {
            await _service.SkipAsync(update.Version);
        }
    }

    public void Dispose() => _service.Changed -= OnChanged;

    private void OnChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Apply);

    private void Apply()
    {
        var update = _service.Available;
        Url = update?.Url;
        Text = update is null ? string.Empty : $"Version {update.Version} is available.";
    }
}

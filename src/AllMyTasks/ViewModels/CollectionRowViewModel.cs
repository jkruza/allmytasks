using AllMyTasks.Core.Model;
using AllMyTasks.Core.Views;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AllMyTasks.ViewModels;

/// <summary>A colour the user can pick for a collection.</summary>
public sealed record ColorChoice(string Name, string? Hex)
{
    public static IReadOnlyList<ColorChoice> All { get; } =
    [
        new("Default", null),
        new("Blue", "#3b82f6"),
        new("Green", "#10b981"),
        new("Amber", "#f59e0b"),
        new("Red", "#ef4444"),
        new("Pink", "#ec4899"),
        new("Purple", "#8b5cf6"),
        new("Teal", "#14b8a6"),
        new("Grey", "#6b7280"),
    ];

    public override string ToString() => Name;
}

/// <summary>One calendar or task list of a source, with the user's visibility and colour choice.</summary>
public sealed partial class CollectionRowViewModel : ObservableObject
{
    private readonly Func<CollectionRowViewModel, Task> _changed;
    private bool _loading = true;

    public CollectionRowViewModel(CollectionInfo info, string? colorOverride, Func<CollectionRowViewModel, Task> changed)
    {
        Info = info;
        _changed = changed;
        IsVisible = info.IsVisible;
        SelectedColor = ColorChoice.All.FirstOrDefault(c => string.Equals(c.Hex, colorOverride, StringComparison.OrdinalIgnoreCase)) ?? ColorChoice.All[0];
        Accent = Brush.Parse(ColorUtil.OrDefault(info.Color));
        _loading = false;
    }

    public CollectionInfo Info { get; }

    public string Name => Info.DisplayName;

    public string KindText => Info.Kind == CollectionKind.Calendar ? "Calendar" : "Task list";

    public static IReadOnlyList<ColorChoice> Colors => ColorChoice.All;

    [ObservableProperty]
    public partial bool IsVisible { get; set; }

    [ObservableProperty]
    public partial ColorChoice SelectedColor { get; set; }

    [ObservableProperty]
    public partial IBrush Accent { get; set; }

    /// <summary>The colour override to store, or null to use the provider's colour.</summary>
    public string? ColorOverride => SelectedColor.Hex;

    partial void OnIsVisibleChanged(bool value) => Notify();

    partial void OnSelectedColorChanged(ColorChoice value)
    {
        if (value.Hex is { } hex)
        {
            Accent = Brush.Parse(hex);
        }

        Notify();
    }

    private async void Notify()
    {
        if (!_loading)
        {
            await _changed(this);
        }
    }
}

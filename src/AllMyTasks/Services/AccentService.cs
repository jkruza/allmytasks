using AllMyTasks.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace AllMyTasks.Services;

/// <summary>
/// Applies the accent setting at runtime: <c>AmtAccentColor</c> for our own styles, and Fluent's palette so its
/// controls (focus, checked, progress) follow. Overdue and status colours are not touched.
/// </summary>
public sealed class AccentService
{
    private readonly Application _application;
    private readonly SettingsService _settings;

    public AccentService(Application application, SettingsService settings)
    {
        _application = application;
        _settings = settings;
        _settings.Changed += (_, _) => Apply();
        if (_application.PlatformSettings is { } platform)
        {
            platform.ColorValuesChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Apply);
        }
    }

    public void Apply()
    {
        var hex = AccentColors.Resolve(_settings.Current.Accent, SystemAccentHex());
        var color = Color.Parse(hex);
        _application.Resources["AmtAccentColor"] = color;
        _application.Resources["AmtOnAccentColor"] = Color.Parse(Core.Views.ColorUtil.ContrastingText(hex));
        foreach (var theme in _application.Styles.OfType<FluentTheme>())
        {
            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                if (theme.Palettes.TryGetValue(variant, out var palette))
                {
                    palette.Accent = color;
                }
            }
        }
    }

    private string? SystemAccentHex()
    {
        var accent = _application.PlatformSettings?.GetColorValues().AccentColor1 ?? default;
        return accent.A == 0 ? null : $"#{accent.R:x2}{accent.G:x2}{accent.B:x2}";
    }
}

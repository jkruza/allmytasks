using AllMyTasks.ViewModels;
using AllMyTasks.Views;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace AllMyTasks.Services;

/// <summary>Opens the app's modal windows over the window the user is working in.</summary>
public sealed class DialogService(IClassicDesktopStyleApplicationLifetime lifetime)
{
    private Window? Owner => lifetime.GetDialogOwner();

    /// <summary>Shows the source editor. Returns true if the source was saved.</summary>
    public async Task<bool> ShowSourceEditorAsync(SourceEditorViewModel editor)
    {
        var window = new SourceEditorWindow { DataContext = editor };
        return Owner is null ? false : await window.ShowDialog<bool>(Owner);
    }

    public void ShowSources(SourcesViewModel sources)
    {
        var window = new SourcesWindow { DataContext = sources };
        if (Owner is null)
        {
            window.Show();
        }
        else
        {
            window.Show(Owner);
        }
    }

    /// <summary>Shows the Settings dialog. Returns when it is closed.</summary>
    public async Task ShowSettingsAsync(SettingsViewModel settings)
    {
        var window = new SettingsWindow { DataContext = settings };
        if (Owner is null)
        {
            window.Show();
        }
        else
        {
            await window.ShowDialog(Owner);
        }
    }

    /// <summary>Asks a yes/no question. Returns true for the primary button.</summary>
    public async Task<bool> ConfirmAsync(string heading, string body, string yes, string no)
    {
        var window = new MessageWindow(heading, body, yes, no);
        return Owner is null ? false : await window.ShowDialog<bool>(Owner);
    }
}

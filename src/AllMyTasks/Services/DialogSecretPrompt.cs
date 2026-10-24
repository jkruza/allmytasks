using AllMyTasks.Secrets;
using AllMyTasks.Views;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace AllMyTasks.Services;

/// <summary>Asks the PIN questions with dialogs over the main window.</summary>
public sealed class DialogSecretPrompt(IClassicDesktopStyleApplicationLifetime lifetime) : ISecretUserPrompt
{
    public Task<bool> ConfirmPinFallbackAsync(CancellationToken cancellationToken) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialog = new MessageWindow(
                "No system keyring found",
                "All My Tasks keeps passwords in your system keyring (GNOME Keyring or KWallet), but none is available. "
                + "Instead it can store them in an encrypted file protected by a PIN. That is weaker than a keyring: "
                + "a 6-digit PIN can be guessed offline by anyone who gets a copy of the file.",
                "Use a PIN",
                "Cancel");
            return await Show<bool>(dialog);
        });

    public Task<string?> RequestPinAsync(PinRequest request, CancellationToken cancellationToken) =>
        Dispatcher.UIThread.InvokeAsync(() => Show<string?>(new PinWindow(request)));

    private async Task<T?> Show<T>(Window dialog)
    {
        var owner = lifetime.GetDialogOwner();
        return owner is { IsVisible: true } ? await dialog.ShowDialog<T?>(owner) : await ShowStandalone<T>(dialog);
    }

    private static async Task<T?> ShowStandalone<T>(Window dialog)
    {
        var tcs = new TaskCompletionSource<T?>();
        dialog.Closed += (_, _) => tcs.TrySetResult(default);
        dialog.Show();
        return await tcs.Task;
    }
}

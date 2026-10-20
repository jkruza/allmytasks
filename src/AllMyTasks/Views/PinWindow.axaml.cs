using AllMyTasks.Secrets;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AllMyTasks.Views;

/// <summary>Asks for the PIN that protects stored passwords when there is no system keyring. Closes with the PIN or null.</summary>
public sealed partial class PinWindow : Window
{
    private readonly bool _isNew;

    public PinWindow()
        : this(new PinRequest(true, 0))
    {
    }

    public PinWindow(PinRequest request)
    {
        InitializeComponent();
        _isNew = request.IsNewFile;
        if (request.IsNewFile)
        {
            Heading.Text = "Choose a PIN";
            Body.Text = "This PIN protects the passwords All My Tasks stores for you. You'll be asked for it each time the app starts. "
                + "There is no way to recover it; if you forget it, you'll re-enter your passwords. "
                + "A 6-digit PIN only protects against casual access: anyone who copies the file can try every PIN. "
                + "A longer passphrase is stronger, and a system keyring (GNOME Keyring, KWallet) is better still.";
            Confirm.IsVisible = true;
        }
        else
        {
            Heading.Text = "Enter your PIN";
            Body.Text = "Unlock the passwords stored by All My Tasks.";
            if (request.FailedAttempts > 0)
            {
                ShowError("That PIN did not work.");
            }
        }
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        var pin = Pin.Text ?? string.Empty;
        if (_isNew)
        {
            if (pin.Length < 6)
            {
                ShowError("Use at least 6 characters.");
                return;
            }

            if (pin != Confirm.Text)
            {
                ShowError("The two entries do not match.");
                return;
            }
        }
        else if (pin.Length == 0)
        {
            return;
        }

        Close(pin);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void ShowError(string message)
    {
        Error.Text = message;
        Error.IsVisible = true;
    }
}

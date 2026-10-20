namespace AllMyTasks.Secrets;

/// <summary>What the PIN prompt needs to show.</summary>
/// <param name="IsNewFile">True when no secret file exists yet and the user is choosing a PIN.</param>
/// <param name="FailedAttempts">How many wrong PINs were just entered in a row.</param>
public sealed record PinRequest(bool IsNewFile, int FailedAttempts);

/// <summary>
/// The questions the PIN-protected fallback has to ask the user. The app implements this with dialogs;
/// tests use scripted answers.
/// </summary>
public interface ISecretUserPrompt
{
    /// <summary>
    /// Explains that no OS keyring was found and asks whether to use a PIN-protected file instead. The text should
    /// state the limits: a 6-digit PIN can be guessed offline by anyone who copies the file.
    /// </summary>
    Task<bool> ConfirmPinFallbackAsync(CancellationToken cancellationToken);

    /// <summary>Asks for the PIN (or a longer passphrase). Returns null if the user cancels.</summary>
    Task<string?> RequestPinAsync(PinRequest request, CancellationToken cancellationToken);
}

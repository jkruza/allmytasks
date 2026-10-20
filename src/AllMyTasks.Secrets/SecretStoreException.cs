namespace AllMyTasks.Secrets;

/// <summary>
/// A secret could not be stored or read. Messages never contain secret values, account names or paths with
/// credentials in them.
/// </summary>
public class SecretStoreException : Exception
{
    public SecretStoreException()
    {
    }

    public SecretStoreException(string message)
        : base(message)
    {
    }

    public SecretStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The user declined to unlock or set up the secret store, or no secure backend is available.</summary>
public sealed class SecretStoreUnavailableException : SecretStoreException
{
    public SecretStoreUnavailableException()
    {
    }

    public SecretStoreUnavailableException(string message)
        : base(message)
    {
    }

    public SecretStoreUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

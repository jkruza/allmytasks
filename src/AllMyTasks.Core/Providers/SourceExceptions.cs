namespace AllMyTasks.Core.Providers;

/// <summary>
/// Base for everything a provider may throw across its public surface. Messages and inner
/// exceptions must not contain credentials, tokens or item content.
/// </summary>
public class SourceException : Exception
{
    public SourceException()
    {
    }

    /// <summary>
    /// Technical details for troubleshooting, such as the HTTP status, the content type and where parsing failed.
    /// The provider vets this text: it must not contain credentials or feed addresses (see <see cref="DiagnosticText"/>),
    /// but it may quote a short piece of the response. It is shown only in the app's own source details and local log.
    /// </summary>
    public string? Diagnostics { get; init; }

    public SourceException(string message)
        : base(message)
    {
    }

    public SourceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Credentials were rejected or have expired. The engine stops retrying until the user acts.</summary>
public class SourceAuthException : SourceException
{
    public SourceAuthException()
    {
    }

    public SourceAuthException(string message)
        : base(message)
    {
    }

    public SourceAuthException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Network, DNS, TLS, server error or timeout. The engine keeps the cache and retries with backoff.</summary>
public class SourceUnavailableException : SourceException
{
    public SourceUnavailableException()
    {
    }

    public SourceUnavailableException(string message)
        : base(message)
    {
    }

    public SourceUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The <see cref="SyncState"/> is no longer valid. The engine retries once with no previous state.</summary>
public class SyncStateExpiredException : SourceException
{
    public SyncStateExpiredException()
    {
    }

    public SyncStateExpiredException(string message)
        : base(message)
    {
    }

    public SyncStateExpiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A response could not be parsed or violates the model. Skip the item if possible and warn.</summary>
public class SourceDataException : SourceException
{
    public SourceDataException()
    {
    }

    public SourceDataException(string message)
        : base(message)
    {
    }

    public SourceDataException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

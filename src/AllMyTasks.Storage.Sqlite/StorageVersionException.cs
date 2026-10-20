namespace AllMyTasks.Storage.Sqlite;

/// <summary>The database was written by a newer version of the app than this one. Never silently downgraded.</summary>
public sealed class StorageVersionException : Exception
{
    public StorageVersionException()
    {
    }

    public StorageVersionException(string message)
        : base(message)
    {
    }

    public StorageVersionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public StorageVersionException(int databaseVersion, int supportedVersion)
        : base($"This data was created by a newer version of All My Tasks (database schema {databaseVersion}; this version supports up to {supportedVersion}). Update the app; the data was not changed.")
    {
        DatabaseVersion = databaseVersion;
        SupportedVersion = supportedVersion;
    }

    public int DatabaseVersion { get; }

    public int SupportedVersion { get; }
}

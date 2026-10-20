using AllMyTasks.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace AllMyTasks.Storage.Sqlite.Tests;

/// <summary>A database in a temporary directory, removed afterwards.</summary>
internal sealed class TempDatabase : IDisposable
{
    private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "amt-tests-" + Guid.NewGuid().ToString("N"));

    public string FilePath => System.IO.Path.Combine(_directory, "data.db");

    public static async Task<(TempDatabase Temp, SqliteDatabase Database)> CreateAsync()
    {
        var temp = new TempDatabase();
        return (temp, await SqliteDatabase.OpenAsync(temp.FilePath, TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        // Pooled connections keep the file open, which blocks deletion on Windows.
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the OS cleans the temp directory eventually.
        }
    }
}

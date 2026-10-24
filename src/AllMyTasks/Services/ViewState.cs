using System.Text.Json;
using AllMyTasks.Core.Storage;

namespace AllMyTasks.Services;

/// <summary>What the shell remembers between sessions.</summary>
public sealed record ShellState(string Mode, bool ShowEvents, bool ShowTasks, bool ShowCompleted, bool HideWeekends);

/// <summary>What the All tasks view remembers between sessions: filter, sort and grouping.</summary>
public sealed record TasksState(
    string Status,
    string? Due,
    string Sort,
    string Grouping,
    bool Descending,
    bool High,
    bool Medium,
    bool Low,
    bool NoPriority,
    string Labels,
    bool AllLabels);

/// <summary>Reads and writes small JSON blobs in the settings store. Never throws: preferences are not worth a crash.</summary>
public sealed class ViewStateStore(ISettingsStore settings)
{
    public async Task<T?> LoadAsync<T>(string key)
        where T : class
    {
        try
        {
            var json = await settings.GetAsync(key).ConfigureAwait(false);
            return json is null ? null : JsonSerializer.Deserialize<T>(json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            System.Diagnostics.Trace.TraceWarning("Could not load setting {0}: {1}", key, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>The stored text for a key, or null when missing or unreadable.</summary>
    public async Task<string?> LoadRawAsync(string key)
    {
        try
        {
            return await settings.GetAsync(key).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            System.Diagnostics.Trace.TraceWarning("Could not load setting {0}: {1}", key, ex.GetType().Name);
            return null;
        }
    }

    public async Task SaveRawAsync(string key, string value)
    {
        try
        {
            await settings.SetAsync(key, value).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            System.Diagnostics.Trace.TraceWarning("Could not save setting {0}: {1}", key, ex.GetType().Name);
        }
    }

    public async Task SaveAsync<T>(string key, T value)
    {
        try
        {
            await settings.SetAsync(key, JsonSerializer.Serialize(value)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            System.Diagnostics.Trace.TraceWarning("Could not save setting {0}: {1}", key, ex.GetType().Name);
        }
    }
}

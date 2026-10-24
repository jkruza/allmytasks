using System.Globalization;
using AllMyTasks.Core.Settings;

namespace AllMyTasks.Services;

/// <summary>Holds the current <see cref="AppSettings"/>, saves changes and tells listeners about them.</summary>
public sealed class SettingsService(ViewStateStore store)
{
    public AppSettings Current { get; private set; } = new();

    /// <summary>The first day of the week the views should use right now.</summary>
    public DayOfWeek FirstDayOfWeek => WeekStart.Resolve(Current.WeekStart, CultureInfo.CurrentCulture);

    /// <summary>Raised after <see cref="Current"/> changed, on the thread that called <see cref="Update"/>.</summary>
    public event EventHandler? Changed;

    public async Task LoadAsync() => Current = AppSettings.Parse(await store.LoadRawAsync(AppSettings.StoreKey).ConfigureAwait(false));

    public void Update(Func<AppSettings, AppSettings> change)
    {
        var next = change(Current);
        if (next == Current)
        {
            return;
        }

        Current = next;
        _ = store.SaveRawAsync(AppSettings.StoreKey, next.ToJson());
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

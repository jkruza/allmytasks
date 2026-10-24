using System.Globalization;

namespace AllMyTasks.Core.Settings;

/// <summary>Which day weeks start on. <see cref="System"/> follows the OS culture.</summary>
public enum WeekStartSetting
{
    System,
    Sunday,
    Monday,
}

public static class WeekStart
{
    /// <summary>
    /// The first day of the week for a setting. <see cref="WeekStartSetting.System"/> uses the culture's first day
    /// when that is Sunday or Monday, and Monday otherwise (only those two are supported).
    /// </summary>
    public static DayOfWeek Resolve(WeekStartSetting setting, CultureInfo culture) => setting switch
    {
        WeekStartSetting.Sunday => DayOfWeek.Sunday,
        WeekStartSetting.Monday => DayOfWeek.Monday,
        _ => culture.DateTimeFormat.FirstDayOfWeek == DayOfWeek.Sunday ? DayOfWeek.Sunday : DayOfWeek.Monday,
    };
}

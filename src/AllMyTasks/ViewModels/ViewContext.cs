using AllMyTasks.Core.Views;

namespace AllMyTasks.ViewModels;

/// <summary>What the shell hands each content view model when it has to (re)load.</summary>
public sealed record ViewContext(
    DateOnly Date,
    ViewFilter Filter,
    DateTimeOffset Now,
    TimeZoneInfo Zone,
    DayOfWeek FirstDayOfWeek);

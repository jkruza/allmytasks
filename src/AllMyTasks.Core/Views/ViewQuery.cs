namespace AllMyTasks.Core.Views;

/// <summary>What the shared calendar filter lets through. Collection visibility is stored with the collections.</summary>
/// <param name="ShowEvents">Include events.</param>
/// <param name="ShowTasks">Include dated tasks.</param>
/// <param name="ShowCompleted">Include completed and cancelled tasks.</param>
public sealed record ViewFilter(bool ShowEvents = true, bool ShowTasks = true, bool ShowCompleted = false);

/// <summary>A date range to show, in the display zone.</summary>
/// <param name="From">First day, inclusive.</param>
/// <param name="ToExclusive">Day after the last day.</param>
/// <param name="DisplayZone">Zone dates and times are shown in.</param>
/// <param name="Filter">What to include.</param>
/// <param name="IncludeOverdue">
/// Also return open tasks that were due before <paramref name="From"/>, so day and upcoming views can
/// show them on today. Week and month leave it off and show tasks on their real due date.
/// </param>
public sealed record ViewQuery(
    DateOnly From,
    DateOnly ToExclusive,
    TimeZoneInfo DisplayZone,
    ViewFilter Filter,
    bool IncludeOverdue = false);

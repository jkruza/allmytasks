namespace AllMyTasks.Core.Views;

/// <summary>One labelled line in the details pane.</summary>
public sealed record DetailRow(string Label, string Value);

/// <summary>Everything the details pane shows for one calendar entry, already formatted for display.</summary>
/// <param name="Title">Never empty; a placeholder replaces an empty title.</param>
/// <param name="IsTask">True for dated tasks, false for events.</param>
/// <param name="AccentColor">Collection colour as <c>#RRGGBB</c>.</param>
/// <param name="Rows">Rows that have a value, in display order.</param>
/// <param name="Link">The entry's link when it is safe to open (http or https), otherwise null.</param>
/// <param name="Description">Plain text, capped at <see cref="EntryDetailsBuilder.MaxDescriptionLength"/> characters.</param>
public sealed record EntryDetails(
    string Title,
    bool IsTask,
    string AccentColor,
    IReadOnlyList<DetailRow> Rows,
    Uri? Link,
    string? Description);

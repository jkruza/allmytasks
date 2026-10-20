namespace AllMyTasks.Core.Model;

/// <summary>A configured account or feed.</summary>
/// <param name="Id">Stable, unique per configured source.</param>
/// <param name="Kind">Provider type, such as <c>ical</c>, <c>caldav</c> or <c>todoist</c>.</param>
/// <param name="DisplayName">Name shown to the user.</param>
public sealed record SourceInfo(string Id, string Kind, string DisplayName)
{
    public string Id { get; init => field = Guard.NotBlank(value); } = Guard.NotBlank(Id);

    public string Kind { get; init => field = Guard.NotBlank(value); } = Guard.NotBlank(Kind);

    public string DisplayName { get; init => field = Guard.NotNull(value); } = Guard.NotNull(DisplayName);
}

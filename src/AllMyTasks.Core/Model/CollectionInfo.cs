namespace AllMyTasks.Core.Model;

/// <summary>A calendar or task list inside a source.</summary>
/// <param name="SourceId">The source this collection belongs to.</param>
/// <param name="Id">Identifier, unique within the source (for CalDAV, the collection path).</param>
/// <param name="DisplayName">Name shown to the user.</param>
/// <param name="Kind">Whether it holds events or tasks.</param>
/// <param name="Color">Colour as <c>#RRGGBB</c>, if the provider or the user chose one.</param>
/// <param name="IsVisible">Whether the user wants it shown. Stored locally, never sent to the provider.</param>
/// <param name="IsReadOnly">Whether the provider rejects writes to this collection.</param>
public sealed record CollectionInfo(
    string SourceId,
    string Id,
    string DisplayName,
    CollectionKind Kind,
    string? Color = null,
    bool IsVisible = true,
    bool IsReadOnly = false)
{
    public string SourceId { get; init => field = Guard.NotBlank(value); } = Guard.NotBlank(SourceId);

    public string Id { get; init => field = Guard.NotBlank(value); } = Guard.NotBlank(Id);

    public string DisplayName { get; init => field = Guard.NotNull(value); } = Guard.NotNull(DisplayName);
}

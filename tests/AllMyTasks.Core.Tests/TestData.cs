using AllMyTasks.Core.Model;

namespace AllMyTasks.Core.Tests;

internal static class TestData
{
    public static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    public static ItemKey Key(string itemId = "1", string source = "s", string collection = "c") =>
        new(source, collection, itemId);

    public static CollectionInfo TaskList(string source = "s", string id = "c") =>
        new(source, id, "List", CollectionKind.TaskList);

    public static CollectionInfo Calendar(string source = "s", string id = "c") =>
        new(source, id, "Calendar", CollectionKind.Calendar);
}

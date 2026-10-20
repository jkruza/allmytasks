using System.Globalization;
using AllMyTasks.Core.Tasks;

namespace AllMyTasks.ViewModels;

/// <summary>A task prepared for display: title, completion and a due string in the display zone.</summary>
public sealed record TaskRow(string Title, string DueText, bool IsCompleted)
{
    public static TaskRow From(TaskItem task, TimeZoneInfo displayZone) => new(
        task.Title,
        task.Due switch
        {
            DueDate date => date.Date.ToString("d", CultureInfo.CurrentCulture),
            DueDateTime at => TimeZoneInfo.ConvertTime(at.At, displayZone).ToString("g", CultureInfo.CurrentCulture),
            _ => string.Empty,
        },
        task.IsCompleted);
}

using System.Runtime.CompilerServices;

namespace AllMyTasks.Core;

/// <summary>Argument checks shared by the model records, so invariants hold on construction and on <c>with</c>.</summary>
internal static class Guard
{
    public static string NotBlank(string? value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value;
    }

    public static T NotNull<T>(T? value, [CallerArgumentExpression(nameof(value))] string? name = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value, name);
        return value;
    }
}

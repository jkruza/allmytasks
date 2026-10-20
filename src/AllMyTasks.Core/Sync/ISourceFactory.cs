using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Storage;

namespace AllMyTasks.Core.Sync;

public enum FieldKind
{
    Text,
    Url,
    Number,
    Password,
    Bool,
}

/// <summary>One input of the "Add source" form.</summary>
/// <param name="Key">Config key (and secret field name).</param>
/// <param name="Label">Label shown to the user.</param>
/// <param name="Kind">What the input holds.</param>
/// <param name="Required">Whether it must be filled in.</param>
/// <param name="IsSecret">Stored in the OS secret store, never in the database or config. Passwords are always secret.</param>
/// <param name="Help">Optional hint shown under the field.</param>
public sealed record SourceConfigField(string Key, string Label, FieldKind Kind, bool Required = false, bool IsSecret = false, string? Help = null)
{
    public bool IsSecretField => IsSecret || Kind == FieldKind.Password;
}

/// <summary>The fields a provider needs; the app builds its form from this, so Core needs no provider-specific UI.</summary>
public sealed record SourceConfigSchema(IReadOnlyList<SourceConfigField> Fields)
{
    public static SourceConfigSchema Empty { get; } = new([]);
}

/// <summary>Describes one kind of provider and creates sources of it.</summary>
public interface ISourceFactory
{
    string Kind { get; }

    string DisplayName { get; }

    SourceConfigSchema ConfigSchema { get; }

    /// <summary>
    /// Creates the source. Secret fields are read through <paramref name="secrets"/> at call time and must not be
    /// held longer than needed, logged, or put in exception messages.
    /// </summary>
    ISource Create(string sourceId, string displayName, SourceConfig config, ISecretStore secrets);
}

/// <summary>A problem with one field of a source form.</summary>
public sealed record SourceConfigError(string Key, string Message);

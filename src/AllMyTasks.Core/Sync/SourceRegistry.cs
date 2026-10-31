using System.Globalization;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Storage;

namespace AllMyTasks.Core.Sync;

/// <summary>Creates source instances by id, from what the repository stores and the registered factories.</summary>
public interface ISourceProvider
{
    Task<ISource> GetAsync(SourceInfo info, CancellationToken cancellationToken = default);

    /// <summary>Forgets a cached instance, for example after the source was edited.</summary>
    void Invalidate(string sourceId);
}

/// <summary>
/// The composition root's list of provider kinds, plus add, edit and remove of sources. Non-secret settings go to the
/// repository, secret fields to the secret store.
/// </summary>
public sealed class SourceRegistry(
    IEnumerable<ISourceFactory> factories,
    ISourceRepository repository,
    ISecretStore secrets) : ISourceProvider
{
    private readonly IReadOnlyList<ISourceFactory> _factories = [.. factories];
    private readonly Dictionary<string, ISource> _instances = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<ISourceFactory> Factories => _factories;

    public ISourceFactory? FactoryFor(string kind) =>
        _factories.FirstOrDefault(f => string.Equals(f.Kind, kind, StringComparison.Ordinal));

    public async Task<ISource> GetAsync(SourceInfo info, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        lock (_gate)
        {
            if (_instances.TryGetValue(info.Id, out var cached))
            {
                return cached;
            }
        }

        var factory = FactoryFor(info.Kind)
            ?? throw new SourceException($"This version of the app has no provider of kind '{info.Kind}'.");
        var config = await repository.GetSourceConfigAsync(info.Id, cancellationToken).ConfigureAwait(false) ?? SourceConfig.Empty;
        var source = factory.Create(info.Id, info.DisplayName, config, secrets);
        lock (_gate)
        {
            _instances[info.Id] = source;
        }

        return source;
    }

    public void Invalidate(string sourceId)
    {
        lock (_gate)
        {
            _instances.Remove(sourceId);
        }
    }

    /// <summary>Checks required fields and field formats. An empty result means the values can be saved.</summary>
    public static IReadOnlyList<SourceConfigError> Validate(
        ISourceFactory factory,
        string displayName,
        IReadOnlyDictionary<string, string> values,
        bool isEdit = false)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(values);
        var errors = new List<SourceConfigError>();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            errors.Add(new SourceConfigError("name", "Enter a name."));
        }

        foreach (var field in factory.ConfigSchema.Fields)
        {
            var value = values.TryGetValue(field.Key, out var v) ? v.Trim() : string.Empty;
            if (value.Length == 0)
            {
                // When editing, an empty secret means "keep the stored one".
                if (field.Required && !(isEdit && field.IsSecretField))
                {
                    errors.Add(new SourceConfigError(field.Key, $"{field.Label} is required."));
                }

                continue;
            }

            if (field.Kind == FieldKind.Url && !IsHttpUrl(value))
            {
                errors.Add(new SourceConfigError(field.Key, $"{field.Label} must be a web address (https://...)."));
            }

            if (field.Kind == FieldKind.Number && !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                errors.Add(new SourceConfigError(field.Key, $"{field.Label} must be a number."));
            }
        }

        if (factory is ISourceConfigValidator validator)
        {
            foreach (var error in validator.ValidateValues(values))
            {
                if (!errors.Any(e => e.Key == error.Key))
                {
                    errors.Add(error);
                }
            }
        }

        return errors;
    }

    /// <summary>Adds a source. Throws <see cref="ArgumentException"/> if <see cref="Validate"/> would report errors.</summary>
    public async Task<SourceInfo> AddAsync(
        string kind,
        string displayName,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default)
    {
        var factory = FactoryFor(kind) ?? throw new ArgumentException($"Unknown provider kind '{kind}'.", nameof(kind));
        if (Validate(factory, displayName, values) is { Count: > 0 } errors)
        {
            throw new ArgumentException(errors[0].Message, nameof(values));
        }

        var info = new SourceInfo(NewId(kind), kind, displayName.Trim());
        await SaveAsync(factory, info, values, isEdit: false, cancellationToken).ConfigureAwait(false);
        return info;
    }

    /// <summary>Changes a source's name and settings. Blank secret fields keep what is stored.</summary>
    public async Task UpdateAsync(
        string sourceId,
        string displayName,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default)
    {
        var existing = (await repository.GetSourcesAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(s => s.Id == sourceId)
            ?? throw new ArgumentException("No such source.", nameof(sourceId));
        var factory = FactoryFor(existing.Kind) ?? throw new ArgumentException($"Unknown provider kind '{existing.Kind}'.", nameof(sourceId));
        if (Validate(factory, displayName, values, isEdit: true) is { Count: > 0 } errors)
        {
            throw new ArgumentException(errors[0].Message, nameof(values));
        }

        await SaveAsync(factory, existing with { DisplayName = displayName.Trim() }, values, isEdit: true, cancellationToken).ConfigureAwait(false);
        Invalidate(sourceId);
    }

    /// <summary>Removes the source, its cached data and its secrets together.</summary>
    public async Task RemoveAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        await CleanUpAtProviderAsync(sourceId, cancellationToken).ConfigureAwait(false);
        await secrets.DeleteAsync(sourceId, cancellationToken).ConfigureAwait(false);
        await repository.RemoveSourceAsync(sourceId, cancellationToken).ConfigureAwait(false);
        Invalidate(sourceId);
    }

    private async Task CleanUpAtProviderAsync(string sourceId, CancellationToken cancellationToken)
    {
        try
        {
            var info = (await repository.GetSourcesAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(s => s.Id == sourceId);
            if (info is not null && await GetAsync(info, cancellationToken).ConfigureAwait(false) is ISourceCleanup cleanup)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await cleanup.OnRemovingAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Best effort: removing the source must not depend on the provider being reachable.
        }
    }

    private async Task SaveAsync(
        ISourceFactory factory,
        SourceInfo info,
        IReadOnlyDictionary<string, string> values,
        bool isEdit,
        CancellationToken cancellationToken)
    {
        var config = new Dictionary<string, string>();
        foreach (var field in factory.ConfigSchema.Fields)
        {
            var value = values.TryGetValue(field.Key, out var v) ? v.Trim() : string.Empty;
            if (field.IsSecretField)
            {
                if (value.Length > 0)
                {
                    await secrets.SetAsync(info.Id, field.Key, value, cancellationToken).ConfigureAwait(false);
                }
                else if (!isEdit)
                {
                    await secrets.DeleteAsync(info.Id, field.Key, cancellationToken).ConfigureAwait(false);
                }
            }
            else if (value.Length > 0)
            {
                config[field.Key] = value;
            }
        }

        await repository.SaveSourceAsync(info, new SourceConfig(config), cancellationToken).ConfigureAwait(false);
    }

    private static string NewId(string kind) => $"{kind}-{Guid.NewGuid().ToString("N")[..8]}";

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "webcal" or "webcals";
}

using System.Text.Json;
using AllMyTasks.Core.Storage;

namespace AllMyTasks.Core.Updates;

/// <summary>A newer release that the user has not skipped.</summary>
public sealed record AvailableUpdate(string Version, string Url);

/// <summary>
/// Decides when to ask for releases and whether a newer one exists. Opt-out: while <c>isEnabled</c> returns false
/// no request is made. At most one check per <see cref="Interval"/>, with a conditional request so most are free.
/// Failures are silent. A stable build ignores prereleases; a prerelease build sees both.
/// See docs/design/update-check.md.
/// </summary>
public sealed class UpdateCheckService : IDisposable
{
    public const string StateKey = "update.check";
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly ISettingsStore _store;
    private readonly IReleaseChecker _checker;
    private readonly Func<bool> _isEnabled;
    private readonly ReleaseVersion? _current;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UpdateCheckService(ISettingsStore store, IReleaseChecker checker, Func<bool> isEnabled, string currentVersion, TimeProvider? clock = null)
    {
        _store = store;
        _checker = checker;
        _isEnabled = isEnabled;
        _current = ReleaseVersion.TryParse(currentVersion);
        _clock = clock ?? TimeProvider.System;
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>The update to offer, or null. Changes after <see cref="CheckAsync"/> and <see cref="SkipAsync"/>.</summary>
    public AvailableUpdate? Available { get; private set; }

    /// <summary>Raised when <see cref="Available"/> changed. May be raised on any thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Checks if a check is due (or <paramref name="force"/>) and updates <see cref="Available"/>. Never throws for network or storage problems.</summary>
    public async Task CheckAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (_current is null || !_isEnabled())
        {
            Set(null);
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var now = _clock.GetUtcNow();
            if (!force && state.LastCheck is { } last && now - last < Interval && now >= last)
            {
                Set(Pick(state));
                return;
            }

            var result = await _checker.CheckAsync(state.ETag, cancellationToken).ConfigureAwait(false);
            switch (result.Status)
            {
                case ReleaseCheckStatus.Ok:
                    state = state with { LastCheck = now, ETag = result.ETag, Releases = [.. result.Releases] };
                    break;
                case ReleaseCheckStatus.NotModified:
                    state = state with { LastCheck = now };
                    break;
                default:
                    // Silent; the next start or the next 24 hours tries again. Shown from what is already known.
                    Set(Pick(state));
                    return;
            }

            await SaveAsync(state, cancellationToken).ConfigureAwait(false);
            Set(Pick(state));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException)
        {
            // Storage trouble is not worth telling the user about.
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Hides the given version, and every older one, for good.</summary>
    public async Task SkipAsync(string version, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            await SaveAsync(state with { Skipped = version }, cancellationToken).ConfigureAwait(false);
            Set(Pick(state with { Skipped = version }));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Set(null);
        }
        finally
        {
            _gate.Release();
        }
    }

    private AvailableUpdate? Pick(State state)
    {
        var skipped = ReleaseVersion.TryParse(state.Skipped);
        AvailableUpdate? best = null;
        ReleaseVersion? bestVersion = null;
        foreach (var release in state.Releases)
        {
            var version = ReleaseVersion.TryParse(release.Version);
            if (version is null || (release.Prerelease || version.IsPrerelease) && !_current!.IsPrerelease)
            {
                continue;
            }

            if (version <= _current! || (skipped is not null && version <= skipped))
            {
                continue;
            }

            if (bestVersion is null || version > bestVersion)
            {
                bestVersion = version;
                best = new AvailableUpdate(version.ToString(), release.Url);
            }
        }

        return best;
    }

    private void Set(AvailableUpdate? value)
    {
        if (Equals(Available, value))
        {
            return;
        }

        Available = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<State> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var json = await _store.GetAsync(StateKey, cancellationToken).ConfigureAwait(false);
            return json is null ? new State() : JsonSerializer.Deserialize<State>(json) ?? new State();
        }
        catch (JsonException)
        {
            return new State();
        }
    }

    private Task SaveAsync(State state, CancellationToken cancellationToken) =>
        _store.SetAsync(StateKey, JsonSerializer.Serialize(state), cancellationToken);

    private sealed record State
    {
        public DateTimeOffset? LastCheck { get; init; }

        public string? ETag { get; init; }

        public string? Skipped { get; init; }

        public List<ReleaseInfo> Releases { get; init; } = [];
    }
}

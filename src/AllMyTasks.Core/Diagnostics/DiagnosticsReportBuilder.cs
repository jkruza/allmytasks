using System.Globalization;
using System.Text;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Sync;

namespace AllMyTasks.Core.Diagnostics;

/// <summary>
/// Builds the plain-text report the user can copy for a bug report. It only ever reads <see cref="SourceDiagnostics"/>,
/// which has no field for a secret, a name or an item, so there is nothing in it that could leak. The output order is
/// stable: sources by kind then short id, collections by kind then size.
/// </summary>
public static class DiagnosticsReportBuilder
{
    public static string Build(ReportEnvironment environment, IEnumerable<SourceDiagnostics> sources, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(sources);
        var report = new StringBuilder();
        report.Append("All My Tasks diagnostics\n");
        report.Append("App: ").Append(environment.AppVersion).Append('\n');
        report.Append("OS: ").Append(environment.OperatingSystem).Append('\n');
        report.Append(".NET: ").Append(environment.Runtime).Append('\n');
        report.Append("Created: ").Append(Time(now)).Append('\n');

        var ordered = sources.OrderBy(s => s.Kind, StringComparer.Ordinal).ThenBy(s => s.ShortId, StringComparer.Ordinal).ToList();
        report.Append("Sources: ").Append(ordered.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var source in ordered)
        {
            report.Append('\n');
            Append(report, source, now);
        }

        return report.ToString();
    }

    /// <summary>The block of the report for one source, as shown in the source's Details on the Sources page.</summary>
    public static string BuildSource(SourceDiagnostics source, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(source);
        var text = new StringBuilder();
        Append(text, source, now);
        return text.ToString().TrimEnd('\n');
    }

    private static void Append(StringBuilder report, SourceDiagnostics source, DateTimeOffset now)
    {
        report.Append("Source ").Append(source.Kind).Append(' ').Append(source.ShortId).Append('\n');
        if (source.Host is { Length: > 0 } host)
        {
            report.Append("  Host: ").Append(host).Append('\n');
        }

        report.Append("  Status: ").Append(StatusName(source.Status)).Append('\n');
        report.Append("  Last successful sync: ").Append(source.LastSuccessAt is { } ok ? $"{Time(ok)} ({Ago(now - ok)} ago)" : "never").Append('\n');
        if (source.LastAttempt is { } attempt)
        {
            report.Append("  Last attempt: ").Append(Time(attempt.StartedAt)).Append(", took ").Append(Duration(attempt.Duration)).Append('\n');
        }
        else
        {
            report.Append("  Last attempt: none since the app started\n");
        }

        report.Append("  Sync strategy: ").Append(source.Strategy ?? "unknown").Append('\n');
        report.Append("  Error category: ").Append(CategoryName(source.Category)).Append('\n');
        report.Append("  Consecutive failures: ").Append(source.ConsecutiveFailures.ToString(CultureInfo.InvariantCulture)).Append('\n');
        report.Append("  Next retry: ").Append(source.NextRetryAt is { } retry ? Time(retry) : "none").Append('\n');
        report.Append("  Items skipped in last sync: ").Append(source.SkippedItems.ToString(CultureInfo.InvariantCulture)).Append('\n');

        var shown = source.Collections.Count(c => c.IsVisible);
        report.Append("  Collections: ").Append(source.Collections.Count.ToString(CultureInfo.InvariantCulture))
            .Append(" (").Append(shown.ToString(CultureInfo.InvariantCulture)).Append(" shown)\n");
        foreach (var c in source.Collections.OrderBy(c => c.Kind).ThenByDescending(c => c.Items).ThenBy(c => c.IsVisible))
        {
            var noun = c.Kind == CollectionKind.Calendar ? "events" : "tasks";
            report.Append("    ").Append(c.Kind == CollectionKind.Calendar ? "calendar" : "task list")
                .Append(c.IsVisible ? string.Empty : ", hidden").Append(": ")
                .Append(c.Items.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(noun).Append('\n');
        }
    }

    internal static string StatusName(SourceStatusKind kind) => kind switch
    {
        SourceStatusKind.New => "not synced yet",
        SourceStatusKind.Ok => "up to date",
        SourceStatusKind.Syncing => "syncing",
        SourceStatusKind.Offline => "offline",
        SourceStatusKind.Error => "error",
        _ => "needs attention",
    };

    internal static string CategoryName(ErrorCategory category) => category switch
    {
        ErrorCategory.None => "none",
        ErrorCategory.Offline => "offline or server unavailable",
        ErrorCategory.Authentication => "authentication",
        ErrorCategory.RateLimit => "rate limit",
        ErrorCategory.ServerError => "server error",
        _ => "unexpected",
    };

    private static string Time(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Duration(TimeSpan value) => value.TotalSeconds < 10
        ? $"{value.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s"
        : $"{Math.Round(value.TotalSeconds).ToString(CultureInfo.InvariantCulture)} s";

    private static string Ago(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.TotalMinutes < 1 ? "under a minute"
            : value.TotalHours < 1 ? $"{(int)value.TotalMinutes} min"
            : value.TotalDays < 1 ? $"{(int)value.TotalHours} h"
            : $"{(int)value.TotalDays} d";
    }
}

using System.Globalization;

namespace AllMyTasks.Core.Updates;

/// <summary>A semantic version with optional prerelease labels, such as <c>0.10.0-alpha.2</c>. Build metadata is ignored.</summary>
public sealed class ReleaseVersion : IComparable<ReleaseVersion>, IEquatable<ReleaseVersion>
{
    private readonly string[] _pre;

    private ReleaseVersion(int major, int minor, int patch, string[] pre)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _pre = pre;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    public bool IsPrerelease => _pre.Length > 0;

    /// <summary>Reads "1.2.3", "v1.2.3-alpha.1" or "1.2.3-alpha+abc123". Returns null for anything else.</summary>
    public static ReleaseVersion? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var span = text.Trim();
        if (span.StartsWith('v') || span.StartsWith('V'))
        {
            span = span[1..];
        }

        var plus = span.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            span = span[..plus];
        }

        string[] pre = [];
        var dash = span.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            pre = span[(dash + 1)..].Split('.');
            if (pre.Any(p => p.Length == 0 || !p.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
            {
                return null;
            }

            span = span[..dash];
        }

        var parts = span.Split('.');
        if (parts.Length != 3
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return null;
        }

        return new ReleaseVersion(major, minor, patch, pre);
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var core = Major.CompareTo(other.Major);
        if (core != 0)
        {
            return core;
        }

        core = Minor.CompareTo(other.Minor);
        if (core != 0)
        {
            return core;
        }

        core = Patch.CompareTo(other.Patch);
        if (core != 0)
        {
            return core;
        }

        // A version without a prerelease label is newer than the same version with one.
        if (_pre.Length == 0 || other._pre.Length == 0)
        {
            return other._pre.Length.CompareTo(_pre.Length);
        }

        for (var i = 0; i < Math.Min(_pre.Length, other._pre.Length); i++)
        {
            var result = ComparePart(_pre[i], other._pre[i]);
            if (result != 0)
            {
                return result;
            }
        }

        return _pre.Length.CompareTo(other._pre.Length);
    }

    public bool Equals(ReleaseVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => Equals(obj as ReleaseVersion);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, string.Join('.', _pre));

    public override string ToString() => $"{Major}.{Minor}.{Patch}{(_pre.Length > 0 ? "-" + string.Join('.', _pre) : string.Empty)}";

    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;

    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;

    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;

    public static bool operator ==(ReleaseVersion? left, ReleaseVersion? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(ReleaseVersion? left, ReleaseVersion? right) => !(left == right);

    private static int ComparePart(string a, string b)
    {
        var aNumeric = a.All(char.IsAsciiDigit);
        var bNumeric = b.All(char.IsAsciiDigit);
        if (aNumeric && bNumeric)
        {
            // Compare by length first so very long numbers cannot overflow.
            var a0 = a.TrimStart('0');
            var b0 = b.TrimStart('0');
            return a0.Length != b0.Length ? a0.Length.CompareTo(b0.Length) : string.CompareOrdinal(a0, b0);
        }

        // Numeric labels sort before alphanumeric ones.
        return aNumeric ? -1 : bNumeric ? 1 : string.CompareOrdinal(a, b);
    }
}

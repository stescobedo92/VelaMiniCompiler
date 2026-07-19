using System.Globalization;
using System.Text.RegularExpressions;

namespace Vela.Packages;

/// <summary>SemVer major.minor.patch with comparison and range matching.</summary>
public sealed partial class SemVer : IComparable<SemVer>, IEquatable<SemVer>
{
    private static readonly Regex Pattern = SemVerRegex();

    /// <summary>Major version component.</summary>
    public int Major { get; }

    /// <summary>Minor version component.</summary>
    public int Minor { get; }

    /// <summary>Patch version component.</summary>
    public int Patch { get; }

    /// <summary>Initializes a new SemVer instance.</summary>
    public SemVer(int major, int minor, int patch)
    {
        if (major < 0 || minor < 0 || patch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(major), "Version components must be non-negative.");
        }

        Major = major;
        Minor = minor;
        Patch = patch;
    }

    /// <summary>Parses a <c>major.minor.patch</c> version string.</summary>
    public static SemVer Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var core = text.Split('+', 2)[0].Split('-', 2)[0];
        var match = Pattern.Match(core);
        if (!match.Success)
        {
            throw new FormatException($"Version '{text}' is not a valid SemVer major.minor.patch value.");
        }

        return new SemVer(
            int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["minor"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["patch"].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>Tries to parse a version string.</summary>
    public static bool TryParse(string text, out SemVer? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            version = Parse(text);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Returns whether <paramref name="version"/> satisfies <paramref name="range"/>.</summary>
    public static bool Satisfies(SemVer version, string range)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(range);
        range = range.Trim();

        if (range.Contains("||", StringComparison.Ordinal))
        {
            var start = 0;
            while (start <= range.Length)
            {
                var separator = range.IndexOf("||", start, StringComparison.Ordinal);
                var segment = separator >= 0 ? range[start..separator] : range[start..];
                if (SatisfiesAndGroup(version, segment.Trim()))
                {
                    return true;
                }

                if (separator < 0)
                {
                    break;
                }

                start = separator + 2;
            }

            return false;
        }

        return SatisfiesAndGroup(version, range);
    }

    /// <inheritdoc />
    public int CompareTo(SemVer? other)
    {
        if (other is null)
        {
            return 1;
        }

        return Compare(this, other);
    }

    /// <inheritdoc />
    public bool Equals(SemVer? other) =>
        other is not null
        && Major == other.Major
        && Minor == other.Minor
        && Patch == other.Patch;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SemVer other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch);

    /// <inheritdoc />
    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    /// <summary>Compares two SemVer values.</summary>
    public static int Compare(SemVer left, SemVer right)
    {
        var major = left.Major.CompareTo(right.Major);
        if (major != 0)
        {
            return major;
        }

        var minor = left.Minor.CompareTo(right.Minor);
        return minor != 0 ? minor : left.Patch.CompareTo(right.Patch);
    }

    /// <summary>Determines whether two versions are equal.</summary>
    public static bool operator ==(SemVer? left, SemVer? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>Determines whether two versions are not equal.</summary>
    public static bool operator !=(SemVer? left, SemVer? right) => !(left == right);

    /// <summary>Determines whether <paramref name="left"/> is less than <paramref name="right"/>.</summary>
    public static bool operator <(SemVer left, SemVer right) => Compare(left, right) < 0;

    /// <summary>Determines whether <paramref name="left"/> is greater than <paramref name="right"/>.</summary>
    public static bool operator >(SemVer left, SemVer right) => Compare(left, right) > 0;

    /// <summary>Determines whether <paramref name="left"/> is less than or equal to <paramref name="right"/>.</summary>
    public static bool operator <=(SemVer left, SemVer right) => Compare(left, right) <= 0;

    /// <summary>Determines whether <paramref name="left"/> is greater than or equal to <paramref name="right"/>.</summary>
    public static bool operator >=(SemVer left, SemVer right) => Compare(left, right) >= 0;

    private static bool SatisfiesAndGroup(SemVer version, string range)
    {
        foreach (var clause in SplitAndClauses(range))
        {
            if (!SatisfiesClause(version, clause))
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<string> SplitAndClauses(string range)
    {
        if (range.Contains(" - ", StringComparison.Ordinal))
        {
            yield return range;
            yield break;
        }

        var start = 0;
        while (start < range.Length)
        {
            while (start < range.Length && range[start] == ' ')
            {
                start++;
            }

            if (start >= range.Length)
            {
                yield break;
            }

            var end = start;
            while (end < range.Length && range[end] != ' ')
            {
                end++;
            }

            yield return range[start..end];
            start = end + 1;
        }
    }

    private static bool SatisfiesClause(SemVer version, string clause)
    {
        clause = clause.Trim();
        if (clause.Length == 0)
        {
            return true;
        }

        if (clause is "*" or "latest")
        {
            return true;
        }

        var hyphenIndex = clause.IndexOf(" - ", StringComparison.Ordinal);
        if (hyphenIndex >= 0)
        {
            var lower = Parse(clause[..hyphenIndex].Trim());
            var upper = Parse(clause[(hyphenIndex + 3)..].Trim());
            return Compare(version, lower) >= 0 && Compare(version, upper) <= 0;
        }

        if (clause.StartsWith('^'))
        {
            var baseline = Parse(clause[1..]);
            return version.Major == baseline.Major
                && Compare(version, baseline) >= 0;
        }

        if (clause.StartsWith('~'))
        {
            var baseline = Parse(clause[1..]);
            return version.Major == baseline.Major
                && version.Minor == baseline.Minor
                && version.Patch >= baseline.Patch;
        }

        if (clause.StartsWith(">=", StringComparison.Ordinal))
        {
            return Compare(version, Parse(clause[2..])) >= 0;
        }

        if (clause.StartsWith("<=", StringComparison.Ordinal))
        {
            return Compare(version, Parse(clause[2..])) <= 0;
        }

        if (clause.StartsWith('>'))
        {
            return Compare(version, Parse(clause[1..])) > 0;
        }

        if (clause.StartsWith('<'))
        {
            return Compare(version, Parse(clause[1..])) < 0;
        }

        return version.Equals(Parse(clause));
    }

    [GeneratedRegex(@"^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex SemVerRegex();
}

using System.Text.RegularExpressions;

namespace Consultologist.Api.Workflow;

public sealed record WorkflowPackageManifest(
    string Name,
    string Version,
    int SpecVersion);

public sealed record WorkflowPackage(
    WorkflowPackageManifest Manifest,
    string StandardsMarkdown)
{
    public string Ref => $"{Manifest.Name}@{Manifest.Version}";
}

public sealed record WorkflowPackageResponse(
    string Name,
    string Version,
    int SpecVersion,
    string StandardsMarkdown);

/// <summary>
/// A package reference of the form "name@vYYYY.MM.N" or "name@latest".
/// </summary>
public sealed record WorkflowPackageRef(string Name, string Version)
{
    public const string LatestVersion = "latest";

    private static readonly Regex NamePattern = new("^[a-z0-9][a-z0-9-]*$", RegexOptions.Compiled);

    public bool IsLatest => string.Equals(Version, LatestVersion, StringComparison.Ordinal);

    public static bool TryParse(string? value, out WorkflowPackageRef? packageRef)
    {
        packageRef = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Trim().Split('@');
        if (parts.Length != 2 || !NamePattern.IsMatch(parts[0]))
        {
            return false;
        }

        var version = parts[1];
        if (!string.Equals(version, LatestVersion, StringComparison.Ordinal)
            && !CalVerVersion.TryParse(version, out _))
        {
            return false;
        }

        packageRef = new WorkflowPackageRef(parts[0], version);
        return true;
    }

    public override string ToString() => $"{Name}@{Version}";
}

/// <summary>
/// Package version in the form "vYYYY.MM.N": zero-padded month, and a within-month
/// release counter starting at 1. Comparison is numeric, never lexicographic —
/// "v2026.07.10" sorts after "v2026.07.2" even though it sorts before it as a string.
/// </summary>
public readonly record struct CalVerVersion(int Year, int Month, int Counter) : IComparable<CalVerVersion>
{
    private static readonly Regex Pattern = new(@"^v(?<year>\d{4})\.(?<month>\d{2})\.(?<counter>[1-9]\d*)$", RegexOptions.Compiled);

    public static bool TryParse(string? value, out CalVerVersion version)
    {
        version = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var match = Pattern.Match(value.Trim());
        if (!match.Success)
        {
            return false;
        }

        var month = int.Parse(match.Groups["month"].Value);
        if (month is < 1 or > 12)
        {
            return false;
        }

        version = new CalVerVersion(
            int.Parse(match.Groups["year"].Value),
            month,
            int.Parse(match.Groups["counter"].Value));
        return true;
    }

    public int CompareTo(CalVerVersion other)
    {
        var byYear = Year.CompareTo(other.Year);
        if (byYear != 0)
        {
            return byYear;
        }

        var byMonth = Month.CompareTo(other.Month);
        return byMonth != 0 ? byMonth : Counter.CompareTo(other.Counter);
    }

    public override string ToString() => $"v{Year:D4}.{Month:D2}.{Counter}";
}

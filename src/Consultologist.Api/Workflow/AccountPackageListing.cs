using System.Text.Json;

namespace Consultologist.Api.Workflow;

/// <summary>
/// Parses one account fork's private-registry listing into the summary the
/// package selector consumes (#134): {name}/{version}/manifest.json blobs plus
/// the {name}/latest.json pointer — the same layout the public chain view's
/// package branch reads on the public side.
/// </summary>
public static class AccountPackageListing
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static PublicPackageSummary Build(string name, IReadOnlyList<string> blobNames, string? latestPointerJson)
    {
        var versions = PublicRegistryReader.SortVersions(blobNames
            .Select(blobName => blobName.Split('/'))
            .Where(parts => parts.Length == 3
                && string.Equals(parts[0], name, StringComparison.Ordinal)
                && parts[2] == "manifest.json")
            .Select(parts => parts[1]));

        string? latest = null;

        if (!string.IsNullOrWhiteSpace(latestPointerJson))
        {
            try
            {
                latest = JsonSerializer.Deserialize<LatestPointer>(latestPointerJson, JsonOptions)?.Version;
            }
            catch (JsonException)
            {
                // A malformed pointer degrades to "no latest"; the versions list stands.
            }
        }

        return new PublicPackageSummary(name, latest, versions);
    }

    private sealed record LatestPointer(string? Version);
}

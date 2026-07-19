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

    public static PublicPackageSummary Build(
        string name,
        IReadOnlyList<string> blobNames,
        string? latestPointerJson,
        IReadOnlyDictionary<string, int>? specVersions = null)
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

        return new PublicPackageSummary(name, latest, versions, specVersions);
    }

    /// <summary>
    /// Reads just the specVersion out of a manifest document; null when the
    /// document is unreadable (callers treat unknown as selectable — the
    /// resolver stays the runtime authority).
    /// </summary>
    public static int? ReadSpecVersion(string manifestJson)
    {
        try
        {
            using var document = JsonDocument.Parse(manifestJson);

            return document.RootElement.TryGetProperty("specVersion", out var spec)
                && spec.ValueKind == JsonValueKind.Number
                && spec.TryGetInt32(out var value)
                    ? value
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record LatestPointer(string? Version);
}

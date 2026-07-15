using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Consultologist.Api.Models;

namespace Consultologist.Api.Workflow;

internal static class ConsultGenerationProvenance
{
    // Canonical serialization: fixed property order and naming, no whitespace. The hash
    // must be stable across runtimes so provenance records stay comparable over time.
    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>The version-1 definition (specVersion ≤4 jobs): draft plus sections.</summary>
    public static string ComputeEffectiveInputHash(ConsultGenerationRequest request)
    {
        var canonical = JsonSerializer.Serialize(
            new
            {
                consultDraft = request.ConsultDraft,
                sections = request.Sections
                    .Select(section => new { id = section.Id, name = section.Name, standard = section.Standard })
                    .ToArray()
            },
            CanonicalJsonOptions);

        return Sha256Hex(canonical);
    }

    /// <summary>
    /// The version-2 definition (specVersion-5 jobs): the draft only — sections are
    /// package data, covered by the workflowPackage ref. Jobs record
    /// EffectiveInputHashVersion = 2 so the two definitions are never compared as
    /// equals (package-format-v5.md).
    /// </summary>
    public static string ComputeDraftOnlyHash(ConsultGenerationRequest request)
    {
        return Sha256Hex(JsonSerializer.Serialize(
            new { consultDraft = request.ConsultDraft },
            CanonicalJsonOptions));
    }

    /// <summary>
    /// Lowercase-hex SHA-256 of the UTF-8 text — the per-node provenance hash: a node's
    /// InputHash covers the exact rendered prompt the agent receives (template +
    /// prelude + variables), its OutputHash the raw assistant text, so two runs can be
    /// compared node by node (dag-improvements #6).
    /// </summary>
    public static string Sha256Hex(string text)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}

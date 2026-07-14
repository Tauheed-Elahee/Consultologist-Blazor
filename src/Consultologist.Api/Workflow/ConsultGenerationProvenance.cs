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

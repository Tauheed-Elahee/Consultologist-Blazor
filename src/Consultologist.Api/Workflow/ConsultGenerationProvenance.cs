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

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash);
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using Consultologist.Api.Models;

namespace Consultologist.Api.Workflow;

/// <summary>
/// The engine-pinned output contract for concept-producing analysis stages: the JSON
/// Schema the structured-output agent (agents/concept-extraction.yaml) is published
/// with, and the deserializer that materializes ClinicalConcept records from
/// schema-conformant output. Engine spec until packages declare schemas
/// (docs/customizable-workflow/dag-as-data-design.md).
/// </summary>
public static class ConceptOutputContract
{
    /// <summary>
    /// Constrained to the OpenAI strict-mode subset: every property required,
    /// additionalProperties false, nullability via type arrays.
    /// </summary>
    public const string SchemaJson = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["concepts"],
          "properties": {
            "concepts": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["term", "type", "id", "isSnomedConcept", "isActive", "support"],
                "properties": {
                  "term":            { "type": "string" },
                  "type":            { "type": "string" },
                  "id":              { "type": ["string", "null"] },
                  "isSnomedConcept": { "type": "boolean" },
                  "isActive":        { "type": "boolean" },
                  "support":         { "type": ["string", "null"] }
                }
              }
            }
          }
        }
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Deserializes schema-conformant agent output into ClinicalConcept records,
    /// stamping the engine-side source. A null id (unmapped finding) maps to an empty
    /// string, preserving the ClinicalConcept invariant the text renderers rely on.
    /// Malformed payloads throw <see cref="ConceptOutputContractException"/> — retryable
    /// under the agent activity retry policy, unlike configuration errors.
    /// </summary>
    public static IReadOnlyList<ClinicalConcept> Deserialize(string json, string source)
    {
        ConceptListOutput? output;
        try
        {
            output = JsonSerializer.Deserialize<ConceptListOutput>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ConceptOutputContractException(
                $"Agent output is not valid concept-list JSON at {ex.Path ?? "(unknown path)"}: {ex.Message}", ex);
        }

        if (output?.Concepts == null)
        {
            throw new ConceptOutputContractException("Agent output is missing the required 'concepts' array.");
        }

        return output.Concepts
            .Select(item => new ClinicalConcept(
                item.Term ?? throw new ConceptOutputContractException("A concept is missing the required 'term'."),
                item.Type ?? string.Empty,
                item.Id ?? string.Empty,
                item.IsSnomedConcept,
                item.IsActive,
                source,
                string.IsNullOrWhiteSpace(item.Support) ? null : item.Support))
            .ToList();
    }

    private sealed record ConceptListOutput(
        [property: JsonPropertyName("concepts")] List<ConceptOutputItem>? Concepts);

    private sealed record ConceptOutputItem(
        [property: JsonPropertyName("term")] string? Term,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("isSnomedConcept")] bool IsSnomedConcept,
        [property: JsonPropertyName("isActive")] bool IsActive,
        [property: JsonPropertyName("support")] string? Support);
}

/// <summary>
/// Schema-conformance failure of structured agent output. Deliberately not
/// InvalidOperationException: the Durable retry policy excludes that type (config
/// errors fail fast), while a malformed structured payload is anomalous but worth the
/// retries before failing loud.
/// </summary>
public sealed class ConceptOutputContractException : Exception
{
    public ConceptOutputContractException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

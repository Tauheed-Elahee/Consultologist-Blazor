using System.Text.Json.Nodes;
using Consultologist.Api.Agents;
using Consultologist.Api.Workflow;

namespace Consultologist.Api.Tests;

public class ConceptOutputContractTests
{
    [Fact]
    public void Deserialize_MapsSchemaConformantOutput()
    {
        const string json = """
            {
              "concepts": [
                { "term": "Adenocarcinoma of prostate", "type": "disorder", "id": "399490008",
                  "isSnomedConcept": true, "isActive": true, "support": "newly diagnosed prostate adenocarcinoma" },
                { "term": "Retired concept", "type": "disorder", "id": "12345",
                  "isSnomedConcept": true, "isActive": false, "support": null },
                { "term": "Strong family support", "type": "finding", "id": null,
                  "isSnomedConcept": false, "isActive": false, "support": null }
              ]
            }
            """;

        var concepts = ConceptOutputContract.Deserialize(json, "patient");

        Assert.Equal(3, concepts.Count);
        Assert.Equal("Adenocarcinoma of prostate", concepts[0].Term);
        Assert.Equal("399490008", concepts[0].Id);
        Assert.True(concepts[0].IsSnomedConcept);
        Assert.True(concepts[0].IsActive);
        Assert.Equal("newly diagnosed prostate adenocarcinoma", concepts[0].Support);
        Assert.All(concepts, c => Assert.Equal("patient", c.Source));

        Assert.False(concepts[1].IsActive);
        Assert.Null(concepts[1].Support);

        // Null id (unmapped finding) preserves the ClinicalConcept empty-string
        // invariant the text renderers rely on.
        Assert.Equal(string.Empty, concepts[2].Id);
        Assert.False(concepts[2].IsSnomedConcept);
    }

    [Fact]
    public void Deserialize_EmptyConceptListIsValid()
    {
        var concepts = ConceptOutputContract.Deserialize("""{ "concepts": [] }""", "problem");

        Assert.Empty(concepts);
    }

    [Theory]
    [InlineData("- Adenocarcinoma of prostate (disorder) - 399490008")] // legacy bullet output
    [InlineData("{ not json")]
    [InlineData("""{ "something": [] }""")]
    public void Deserialize_ThrowsContractExceptionOnMalformedOutput(string payload)
    {
        var ex = Assert.ThrowsAny<Exception>(() => ConceptOutputContract.Deserialize(payload, "patient"));

        // Must be the retryable contract exception — never InvalidOperationException,
        // which the Durable retry policy treats as a non-retryable config error.
        Assert.IsType<ConceptOutputContractException>(ex);
    }

    [Fact]
    public void AgentManifestSchema_MatchesEngineContract()
    {
        // The schema published in agents/concept-extraction.yaml and the engine's
        // pinned contract must be the same document (canonically) — the agent enforces
        // at generation time exactly what the engine deserializes.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Consultologist.sln")))
        {
            dir = dir.Parent;
        }

        var manifest = AttestedAgentManifest.Load(
            File.ReadAllText(Path.Combine(dir!.FullName, "agents", "concept-extraction.yaml")));

        Assert.Equal("json_schema", manifest.Definition.Text?.Format?.Type);
        Assert.Equal("concept_list", manifest.Definition.Text?.Format?.Name);
        Assert.Equal("true", manifest.Definition.Text?.Format?.Strict);

        var manifestSchema = JsonNode.Parse(manifest.Definition.Text!.Format!.Schema!)!;
        var engineSchema = JsonNode.Parse(ConceptOutputContract.SchemaJson)!;

        Assert.Equal(engineSchema.ToJsonString(), manifestSchema.ToJsonString());
    }
}

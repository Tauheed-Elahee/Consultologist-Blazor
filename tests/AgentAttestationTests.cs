using System.Text.Json.Nodes;
using Consultologist.Api.Agents;

namespace Consultologist.Api.Tests;

public class AgentAttestationTests
{
    private const string ManifestYaml = """
        # comment lines are ignored
        name: test-json
        version: "47"
        status: active
        definition:
          kind: prompt
          model: gpt-5.6-sol
          reasoning:
            effort: none
          instructions: |
            You are an AI assistant that helps physicians write consult notes.

            STRICT: rule one.
          tools:
            - type: mcp
              server_label: ConsultologistSnomed
              server_url: https://mcp.snomed.consultologist.ai/runtime/webhooks/mcp
              require_approval: never
          tool_choice: auto
          text:
            format:
              type: text
        """;

    private static JsonNode MatchingDeployed() => JsonNode.Parse("""
        {
          "id": "test-json:47",
          "definition": {
            "kind": "prompt",
            "model": "gpt-5.6-sol",
            "reasoning": { "effort": "none" },
            "instructions": "You are an AI assistant that helps physicians write consult notes.\n\nSTRICT: rule one.",
            "tools": [
              {
                "type": "mcp",
                "server_label": "ConsultologistSnomed",
                "server_url": "https://mcp.snomed.consultologist.ai/runtime/webhooks/mcp",
                "require_approval": "never"
              }
            ],
            "tool_choice": "auto",
            "text": { "format": { "type": "text" } }
          }
        }
        """)!;

    private const string StructuredManifestYaml = """
        name: concept-extraction
        version: "1"
        status: active
        definition:
          kind: prompt
          model: gpt-5.6-sol
          reasoning:
            effort: none
          instructions: |
            Emit concepts as JSON.
          tools:
            - type: mcp
              server_label: ConsultologistSnomed
              server_url: https://mcp.snomed.consultologist.ai/runtime/webhooks/mcp
          tool_choice: auto
          text:
            format:
              type: json_schema
              name: concept_list
              strict: true
              schema: |
                { "type": "object", "required": ["concepts"], "properties": { "concepts": { "type": "array" } } }
        """;

    private static JsonNode StructuredDeployed() => JsonNode.Parse("""
        {
          "definition": {
            "kind": "prompt",
            "model": "gpt-5.6-sol",
            "reasoning": { "effort": "none" },
            "instructions": "Emit concepts as JSON.",
            "tools": [
              {
                "type": "mcp",
                "server_label": "ConsultologistSnomed",
                "server_url": "https://mcp.snomed.consultologist.ai/runtime/webhooks/mcp"
              }
            ],
            "tool_choice": "auto",
            "text": {
              "format": {
                "type": "json_schema",
                "name": "concept_list",
                "strict": true,
                "schema": { "properties": { "concepts": { "type": "array" } }, "required": ["concepts"], "type": "object" }
              }
            }
          }
        }
        """)!;

    [Fact]
    public void Load_ParsesManifestFields()
    {
        var manifest = AttestedAgentManifest.Load(ManifestYaml);

        Assert.Equal("test-json", manifest.Name);
        Assert.Equal("47", manifest.Version);
        Assert.Equal("gpt-5.6-sol", manifest.Definition.Model);
        Assert.Equal("none", manifest.Definition.Reasoning?.Effort);
        Assert.Equal("auto", manifest.Definition.ToolChoice);
        Assert.Single(manifest.Definition.Tools);
        Assert.Equal("ConsultologistSnomed", manifest.Definition.Tools[0].ServerLabel);
    }

    [Fact]
    public void Compare_PassesWhenDeployedMatches()
    {
        var manifest = AttestedAgentManifest.Load(ManifestYaml);

        var mismatches = AttestedAgentManifest.Compare(manifest, MatchingDeployed());

        Assert.Empty(mismatches);
    }

    [Fact]
    public void Compare_ToleratesTrailingNewlineAndCrLfInInstructions()
    {
        var manifest = AttestedAgentManifest.Load(ManifestYaml);
        var deployed = MatchingDeployed();
        deployed["definition"]!["instructions"] =
            "You are an AI assistant that helps physicians write consult notes.\r\n\r\nSTRICT: rule one.\r\n";

        Assert.Empty(AttestedAgentManifest.Compare(manifest, deployed));
    }

    [Theory]
    [InlineData("model", "gpt-4o", "model")]
    [InlineData("tool_choice", "required", "tool_choice")]
    [InlineData("instructions", "Different instructions entirely.", "instructions differ")]
    public void Compare_DetectsFieldDrift(string field, string value, string expectedFragment)
    {
        var manifest = AttestedAgentManifest.Load(ManifestYaml);
        var deployed = MatchingDeployed();
        deployed["definition"]![field] = value;

        var mismatches = AttestedAgentManifest.Compare(manifest, deployed);

        Assert.Contains(mismatches, m => m.Contains(expectedFragment));
    }

    [Fact]
    public void Compare_DetectsToolDrift()
    {
        var manifest = AttestedAgentManifest.Load(ManifestYaml);
        var deployed = MatchingDeployed();
        deployed["definition"]!["tools"]![0]!["server_url"] = "https://evil.example.com/mcp";

        var mismatches = AttestedAgentManifest.Compare(manifest, deployed);

        Assert.Contains(mismatches, m => m.Contains("tools[0].server_url"));
    }

    [Fact]
    public void Compare_DetectsRemovedTools()
    {
        var manifest = AttestedAgentManifest.Load(ManifestYaml);
        var deployed = MatchingDeployed();
        deployed["definition"]!["tools"] = new JsonArray();

        var mismatches = AttestedAgentManifest.Compare(manifest, deployed);

        Assert.Contains(mismatches, m => m.Contains("tools count"));
    }

    [Fact]
    public void Compare_StructuredAgent_PassesWithReorderedSchemaKeys()
    {
        // The deployed fixture's schema has its keys in a different order than the
        // manifest's JSON block — comparison must be canonical, not textual.
        var manifest = AttestedAgentManifest.Load(StructuredManifestYaml);

        Assert.Empty(AttestedAgentManifest.Compare(manifest, StructuredDeployed()));
    }

    [Fact]
    public void Compare_DetectsTextFormatTypeDrift()
    {
        var manifest = AttestedAgentManifest.Load(StructuredManifestYaml);
        var deployed = StructuredDeployed();
        deployed["definition"]!["text"]!["format"]!["type"] = "text";

        var mismatches = AttestedAgentManifest.Compare(manifest, deployed);

        Assert.Contains(mismatches, m => m.Contains("text.format.type"));
    }

    [Fact]
    public void Compare_DetectsSchemaDrift()
    {
        var manifest = AttestedAgentManifest.Load(StructuredManifestYaml);
        var deployed = StructuredDeployed();
        deployed["definition"]!["text"]!["format"]!["schema"]!["properties"]!["concepts"]!["type"] = "string";

        var mismatches = AttestedAgentManifest.Compare(manifest, deployed);

        Assert.Contains(mismatches, m => m.Contains("text.format.schema"));
    }

    [Fact]
    public void Compare_DetectsStrictDrift()
    {
        var manifest = AttestedAgentManifest.Load(StructuredManifestYaml);
        var deployed = StructuredDeployed();
        deployed["definition"]!["text"]!["format"]!["strict"] = false;

        var mismatches = AttestedAgentManifest.Compare(manifest, deployed);

        Assert.Contains(mismatches, m => m.Contains("text.format.strict"));
    }

    [Fact]
    public void CatalogSchema_MatchingManifest_Passes()
    {
        var manifest = AttestedAgentManifest.Load(StructuredManifestYaml);
        // Same schema, reordered keys: canonical comparison must pass.
        var entry = new OutputContractEntry(
            "concept-list",
            "concept-extraction",
            "1",
            """{ "properties": { "concepts": { "type": "array" } }, "required": ["concepts"], "type": "object" }""");

        Assert.Empty(AgentAttestationService.CompareCatalogSchema(entry, manifest));
    }

    [Fact]
    public void CatalogSchema_DriftFromManifest_Fails()
    {
        var manifest = AttestedAgentManifest.Load(StructuredManifestYaml);
        var entry = new OutputContractEntry(
            "concept-list",
            "concept-extraction",
            "1",
            """{ "type": "object", "required": ["items"], "properties": { "items": { "type": "array" } } }""");

        var mismatches = AgentAttestationService.CompareCatalogSchema(entry, manifest);

        Assert.Contains(mismatches, m => m.Contains("differs from the manifest text.format.schema"));
    }

    [Fact]
    public void CatalogSchema_TextEntryAgainstStructuredManifest_Fails()
    {
        var manifest = AttestedAgentManifest.Load(StructuredManifestYaml);
        var entry = new OutputContractEntry("text", "concept-extraction", "1", SchemaJson: null);

        var mismatches = AgentAttestationService.CompareCatalogSchema(entry, manifest);

        Assert.Contains(mismatches, m => m.Contains("declares no schema"));
    }

    [Fact]
    public void CatalogSchema_SchemaEntryAgainstProseManifest_Fails()
    {
        var manifest = AttestedAgentManifest.Load(ManifestYaml);
        var entry = new OutputContractEntry("concept-list", "test-json", "47", """{ "type": "object" }""");

        var mismatches = AgentAttestationService.CompareCatalogSchema(entry, manifest);

        Assert.Contains(mismatches, m => m.Contains("text.format.type is 'text'"));
    }
}

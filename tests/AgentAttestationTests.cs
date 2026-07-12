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
            "tool_choice": "auto"
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
}

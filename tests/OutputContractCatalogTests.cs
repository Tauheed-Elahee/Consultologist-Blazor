using System.Text.Json.Nodes;
using Consultologist.Api.Agents;

namespace Consultologist.Api.Tests;

public class OutputContractCatalogTests
{
    private static string RepoAgentsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Consultologist.sln")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "agents");
    }

    [Fact]
    public void Load_RealCatalog_HasTextAndConceptListEntries()
    {
        var catalog = OutputContractCatalog.Load(RepoAgentsDirectory());

        var text = catalog.GetEntry(OutputContracts.Text);
        Assert.False(string.IsNullOrWhiteSpace(text.AgentName));
        Assert.False(string.IsNullOrWhiteSpace(text.AgentVersion));
        Assert.Null(text.SchemaJson);

        var conceptList = catalog.GetEntry(OutputContracts.ConceptList);
        Assert.False(string.IsNullOrWhiteSpace(conceptList.AgentName));
        Assert.False(string.IsNullOrWhiteSpace(conceptList.AgentVersion));
        Assert.NotNull(conceptList.SchemaJson);
    }

    [Fact]
    public void Load_RealCatalog_ConceptListSchemaMatchesAgentManifest()
    {
        // The catalog's schema file and the schema welded into the pinned agent's
        // published definition must be the same document: the agent enforces at
        // generation time exactly the shape the catalog claims for the contract.
        var agentsDir = RepoAgentsDirectory();
        var catalog = OutputContractCatalog.Load(agentsDir);
        var entry = catalog.GetEntry(OutputContracts.ConceptList);

        var manifest = AttestedAgentManifest.Load(
            File.ReadAllText(Path.Combine(agentsDir, $"{entry.AgentName}.yaml")));

        Assert.Equal(entry.AgentVersion, manifest.Version);
        Assert.Equal("json_schema", manifest.Definition.Text?.Format?.Type);

        var manifestSchema = JsonNode.Parse(manifest.Definition.Text!.Format!.Schema!)!;
        var catalogSchema = JsonNode.Parse(entry.SchemaJson!)!;

        Assert.Equal(catalogSchema.ToJsonString(), manifestSchema.ToJsonString());
    }

    [Fact]
    public void TryResolveContract_MatchesModuloTitleAndDescription()
    {
        var catalog = OutputContractCatalog.Load(RepoAgentsDirectory());
        var schema = JsonNode.Parse(catalog.GetEntry(OutputContracts.ConceptList).SchemaJson!)!.AsObject();
        schema["title"] = "Concept list";
        schema["description"] = "Authored copy";

        Assert.True(catalog.TryResolveContract(schema, out var contractId));
        Assert.Equal(OutputContracts.ConceptList, contractId);
    }

    [Fact]
    public void TryResolveContract_RejectsPropertyDrift()
    {
        var catalog = OutputContractCatalog.Load(RepoAgentsDirectory());
        var drifted = JsonNode.Parse(
            catalog.GetEntry(OutputContracts.ConceptList).SchemaJson!.Replace("\"isActive\"", "\"active\""))!;

        Assert.False(catalog.TryResolveContract(drifted, out _));
    }

    [Fact]
    public void GetEntry_UnknownContract_Throws()
    {
        var catalog = OutputContractCatalog.Load(RepoAgentsDirectory());

        var ex = Assert.Throws<InvalidOperationException>(() => catalog.GetEntry("unknown-shape"));
        Assert.Contains("unknown-shape", ex.Message);
    }

    [Theory]
    [InlineData(null, "catalog not found")]
    [InlineData("not json", "not valid JSON")]
    [InlineData("""{ "contracts": {} }""", "declares no contracts")]
    [InlineData("""{ "contracts": { "text": { "agentName": "a" } } }""", "agentName and agentVersion")]
    [InlineData("""{ "contracts": { "concept-list": { "agentName": "a", "agentVersion": "1" } } }""", "must declare the 'text' entry")]
    [InlineData("""{ "contracts": { "text": { "agentName": "a", "agentVersion": "1", "schemaFile": "missing.json" } } }""", "missing schema file")]
    public void Load_DefectiveCatalog_FailsFast(string? catalogJson, string expectedMessagePart)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"catalog-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            if (catalogJson != null)
            {
                File.WriteAllText(Path.Combine(dir, "output-contracts.json"), catalogJson);
            }

            var ex = Assert.Throws<InvalidOperationException>(() => OutputContractCatalog.Load(dir));
            Assert.Contains(expectedMessagePart, ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

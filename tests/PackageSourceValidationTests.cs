using System.Text.Json;
using Consultologist.Api.Workflow;

namespace Consultologist.Api.Tests;

/// <summary>
/// Validates the actual package sources in the repo's packages/ directory — the same
/// checks the engine applies at load time, so a broken package fails CI before it can
/// be published (publish-time validation per package-format-v2.md).
/// </summary>
public class PackageSourceValidationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Consultologist.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void GeneralPackageSource_IsValid()
    {
        var packageDir = Path.Combine(FindRepoRoot(), "packages", "general");
        Assert.True(Directory.Exists(packageDir), $"package source not found at {packageDir}");

        var manifest = JsonSerializer.Deserialize<WorkflowPackageManifest>(
            File.ReadAllText(Path.Combine(packageDir, "manifest.json")), JsonOptions)!;

        Assert.Equal("general", manifest.Name);
        Assert.True(CalVerVersion.TryParse(manifest.Version, out _), $"version '{manifest.Version}' is not CalVer");

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var paths = (manifest.Prompts ?? new List<WorkflowPromptSpec>()).Select(p => p.File)
            .Concat((manifest.Preludes ?? new Dictionary<string, string>()).Values)
            .Concat((manifest.Schemas ?? new Dictionary<string, string>()).Values)
            .Distinct(StringComparer.Ordinal);

        foreach (var path in paths)
        {
            var full = Path.Combine(packageDir, path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full))
            {
                files[path] = File.ReadAllText(full);
            }
        }

        // Two-stage data gathering, exactly as the store does it: the data table
        // names scalars and collection indexes; each index names its item files.
        foreach (var (_, dataPath) in manifest.Data ?? new Dictionary<string, string>())
        {
            if (!dataPath.EndsWith('/'))
            {
                AddFileIfPresent(files, packageDir, dataPath);
                continue;
            }

            var indexPath = dataPath + WorkflowDataResolver.IndexFileName;
            if (!AddFileIfPresent(files, packageDir, indexPath))
            {
                continue;
            }

            var index = JsonSerializer.Deserialize<WorkflowDataIndexFile>(files[indexPath], JsonOptions);
            foreach (var item in index?.Items ?? new List<WorkflowDataIndexItem>())
            {
                if (!string.IsNullOrWhiteSpace(item.File))
                {
                    AddFileIfPresent(files, packageDir, dataPath + item.File);
                }
            }
        }

        var result = WorkflowPackageValidator.Validate(manifest, files, TestOutputContracts.CatalogSchemas);

        Assert.True(result.IsValid, "validation errors: " + string.Join(" | ", result.Errors));
        Assert.True(result.Warnings.Count == 0, "validation warnings: " + string.Join(" | ", result.Warnings));
    }

    private static bool AddFileIfPresent(Dictionary<string, string> files, string packageDir, string path)
    {
        var full = Path.Combine(packageDir, path.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full))
        {
            return false;
        }

        files[path] = File.ReadAllText(full);
        return true;
    }

    [Fact]
    public void GeneralPackageSource_DeclaresTheCanonicalV6Pipeline()
    {
        var packageDir = Path.Combine(FindRepoRoot(), "packages", "general");
        var manifest = JsonSerializer.Deserialize<WorkflowPackageManifest>(
            File.ReadAllText(Path.Combine(packageDir, "manifest.json")), JsonOptions)!;

        // The repo package is the canonical pipeline in its v6 form (#116): the
        // v5 nodes byte-stable, plus the assemble-note result aggregator that
        // makes the deliverable one document.
        Assert.Equal(6, manifest.SpecVersion);
        Assert.Null(manifest.DerivedFrom);
        Assert.Equal("node:assemble-note", manifest.Result);
        Assert.Equal("data/standards/", manifest.Data!["standards"]);

        var aggregatorNode = manifest.Nodes!.Single(node => node.Aggregate != null);
        Assert.Equal("assemble-note", aggregatorNode.Id);
        Assert.Equal(new List<string> { "node:section-instructions" }, aggregatorNode.Aggregate);
        Assert.Null(aggregatorNode.Prompt);

        var canonical = V5Fixtures.Manifest().Nodes!;
        var promptNodes = manifest.Nodes!.Where(node => node.Aggregate is null).ToList();

        Assert.Equal(
            canonical.Select(node => (node.Id, node.Label, node.Prompt, node.ForEach)),
            promptNodes.Select(node => (node.Id, node.Label, node.Prompt, node.ForEach)));

        foreach (var (declared, expected) in promptNodes.Zip(canonical))
        {
            Assert.Equal(expected.Output, declared.Output);
            Assert.Equal(
                (expected.Bindings ?? new Dictionary<string, WorkflowBindingValue>()).OrderBy(b => b.Key, StringComparer.Ordinal),
                (declared.Bindings ?? new Dictionary<string, WorkflowBindingValue>()).OrderBy(b => b.Key, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void GeneralPackageSchema_IsCanonicallyIdenticalToTheEngineContract()
    {
        var schemaPath = Path.Combine(FindRepoRoot(), "packages", "general", "schemas", "concept-list.json");
        Assert.True(File.Exists(schemaPath), $"schema file not found at {schemaPath}");

        var packageSchema = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(schemaPath));
        var engineSchema = System.Text.Json.Nodes.JsonNode.Parse(TestOutputContracts.ConceptListSchema);

        Assert.Equal(
            WorkflowPackageValidator.CanonicalizeSchema(engineSchema),
            WorkflowPackageValidator.CanonicalizeSchema(packageSchema));
    }
}

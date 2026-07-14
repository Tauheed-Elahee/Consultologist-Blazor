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
        Assert.True(File.Exists(Path.Combine(packageDir, "standards.md")));

        if (manifest.SpecVersion < 2)
        {
            return; // v1 has no prompt content to validate
        }

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

        var result = WorkflowPackageValidator.Validate(manifest, files);

        Assert.True(result.IsValid, "validation errors: " + string.Join(" | ", result.Errors));
        Assert.True(result.Warnings.Count == 0, "validation warnings: " + string.Join(" | ", result.Warnings));
    }

    [Fact]
    public void GeneralPackageSource_DeclaresTheCanonicalDag()
    {
        var packageDir = Path.Combine(FindRepoRoot(), "packages", "general");
        var manifest = JsonSerializer.Deserialize<WorkflowPackageManifest>(
            File.ReadAllText(Path.Combine(packageDir, "manifest.json")), JsonOptions)!;

        Assert.True(manifest.SpecVersion >= 4, "the repo package is expected to be specVersion 4 or newer");
        Assert.NotNull(manifest.Nodes);

        // The first v4 package is the verbatim current pipeline: its declared nodes
        // must equal the DAG the engine synthesizes for v2/v3 packages, so behavior is
        // byte-stable across the format cutover.
        var synthesized = WorkflowNodeDefaults.V3SynthesizedDag(WorkflowSectionStepDefaults.V2Synthesized);

        Assert.Equal(
            synthesized.Select(node => (node.Id, node.Kind, node.Label, node.Prompt, node.Over)),
            manifest.Nodes!.Select(node => (node.Id, node.Kind, node.Label, node.Prompt, node.Over)));

        foreach (var (declared, expected) in manifest.Nodes!.Zip(synthesized))
        {
            Assert.Equal(expected.Output, declared.Output);
            Assert.Equal(
                (expected.Bindings ?? new Dictionary<string, WorkflowBindingValue>()).OrderBy(b => b.Key, StringComparer.Ordinal),
                (declared.Bindings ?? new Dictionary<string, WorkflowBindingValue>()).OrderBy(b => b.Key, StringComparer.Ordinal));

            var expectedSteps = expected.Steps ?? new List<WorkflowMapStepSpec>();
            var declaredSteps = declared.Steps ?? new List<WorkflowMapStepSpec>();
            Assert.Equal(expectedSteps.Select(s => (s.StepId, s.Label)), declaredSteps.Select(s => (s.StepId, s.Label)));

            foreach (var (declaredStep, expectedStep) in declaredSteps.Zip(expectedSteps))
            {
                Assert.Equal(
                    expectedStep.Bindings.OrderBy(b => b.Key, StringComparer.Ordinal),
                    declaredStep.Bindings.OrderBy(b => b.Key, StringComparer.Ordinal));
            }
        }
    }

    [Fact]
    public void GeneralPackageSchema_IsCanonicallyIdenticalToTheEngineContract()
    {
        var schemaPath = Path.Combine(FindRepoRoot(), "packages", "general", "schemas", "concept-list.json");
        Assert.True(File.Exists(schemaPath), $"schema file not found at {schemaPath}");

        var packageSchema = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(schemaPath));
        var engineSchema = System.Text.Json.Nodes.JsonNode.Parse(ConceptOutputContract.SchemaJson);

        Assert.Equal(
            WorkflowPackageValidator.CanonicalizeSchema(engineSchema),
            WorkflowPackageValidator.CanonicalizeSchema(packageSchema));
    }
}

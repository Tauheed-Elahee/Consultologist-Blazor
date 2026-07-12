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
}

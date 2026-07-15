using System.Text.Json;
using Consultologist.Api.Workflow;

namespace Consultologist.Api.Tests;

public class WorkflowDagDiagramTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Consultologist.sln")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }

    private static WorkflowPackageManifest GeneralManifest()
        => JsonSerializer.Deserialize<WorkflowPackageManifest>(
            File.ReadAllText(Path.Combine(RepoRoot(), "packages", "general", "manifest.json")), JsonOptions)!;

    [Fact]
    public void GeneratedDiagram_MatchesCheckedInFile()
    {
        // The snapshot pin: packages/general/dag.mmd is generated, never authored.
        // On a legitimate manifest change, regenerate with
        //   UPDATE_SNAPSHOTS=1 dotnet test --filter WorkflowDagDiagramTests
        // and commit the result alongside the manifest.
        var generated = WorkflowDagDiagram.Generate(GeneralManifest());
        var snapshotPath = Path.Combine(RepoRoot(), "packages", "general", "dag.mmd");

        if (Environment.GetEnvironmentVariable("UPDATE_SNAPSHOTS") == "1")
        {
            File.WriteAllText(snapshotPath, generated);
            return;
        }

        Assert.True(File.Exists(snapshotPath), $"snapshot not found at {snapshotPath} — generate it with UPDATE_SNAPSHOTS=1");
        Assert.Equal(File.ReadAllText(snapshotPath), generated);
    }

    [Fact]
    public void Diagram_DrawsNodesEdgesAndTheMapSubgraph()
    {
        var diagram = WorkflowDagDiagram.Generate(GeneralManifest());

        Assert.StartsWith("flowchart TD", diagram);
        // Inputs as stadium nodes.
        Assert.Contains("input_consult_draft([\"input:consult_draft\"])", diagram);
        // A prompt node with its schema annotation.
        Assert.Contains("extract-patient-concepts<br/>Extracting clinical concepts<br/>output: concept-list", diagram);
        // The diamond's fan-in edge with a renderer annotation.
        Assert.Contains("-->|\"patient_trajectory_concepts (as concept-context)\"|", diagram);
        // The map as a subgraph fed by its over source.
        Assert.Contains("input_sections -->|\"over\"| sections", diagram);
        Assert.Contains("subgraph sections[", diagram);
        // The per-item step chain rides previous_step_output edges.
        Assert.Contains("sections_standard_section_draft -->|\"standard_section_draft\"| sections_patient_section_draft", diagram);
    }

    [Fact]
    public void Generate_RejectsManifestsWithoutNodes()
    {
        var v3 = GeneralManifest() with { Nodes = null };

        Assert.Throws<InvalidOperationException>(() => WorkflowDagDiagram.Generate(v3));
    }
}

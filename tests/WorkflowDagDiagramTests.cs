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
    public void Diagram_DrawsNodesEdgesAndTheForEachSubgraph()
    {
        var diagram = WorkflowDagDiagram.Generate(GeneralManifest());

        Assert.StartsWith("flowchart TD", diagram);
        // Inputs as stadium nodes.
        Assert.Contains("input_consult_draft([\"input:consult_draft\"])", diagram);
        // A scalar node with its schema annotation.
        Assert.Contains("extract-patient-concepts<br/>Extracting clinical concepts<br/>output: concept-list", diagram);
        // The diamond's fan-in edge with a renderer annotation.
        Assert.Contains("-->|\"patient_trajectory_concepts (as concept-context)\"|", diagram);
        // The forEach chain as a per-collection subgraph fed by its collection.
        Assert.Contains("-->|\"forEach\"| foreach_", diagram);
        Assert.Contains("subgraph foreach_", diagram);
        // Item-aligned edges chain the steps inside the box.
        Assert.Contains("standard_section_draft -->|\"standard_section_draft\"| patient_section_draft", diagram);
    }

    [Fact]
    public void Diagram_RendersV5ManifestsNatively()
    {
        var diagram = WorkflowDagDiagram.Generate(V5Fixtures.Manifest());

        // The collection stadium and the per-item subgraph.
        Assert.Contains("data_standards([\"data:standards\"])", diagram);
        Assert.Contains("data_standards -->|\"forEach\"| foreach_data_standards", diagram);
        Assert.Contains("subgraph foreach_data_standards[\"per data:standards item\"]", diagram);
        // item: fields surface in the node labels.
        Assert.Contains("section-instructions<br/>Applying section instructions<br/>item: name, content", diagram);
        // Broadcast edge crosses the boundary.
        Assert.Contains("create_patient_trajectory -->|\"patient_trajectory_concepts (as concept-context)\"| standard_section_draft", diagram);
    }

    [Fact]
    public void Generate_RejectsManifestsWithoutNodes()
    {
        var v3 = GeneralManifest() with { Nodes = null };

        Assert.Throws<InvalidOperationException>(() => WorkflowDagDiagram.Generate(v3));
    }
}

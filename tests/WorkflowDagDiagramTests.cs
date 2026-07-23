using Consultologist.Api.Workflow;

namespace Consultologist.Api.Tests;

// The generator's structural pins run on the test fixtures; the general
// package's dag.mmd snapshot lives with the package in the
// consultologist-workflows repo since #16 (content left the app repo).
public class WorkflowDagDiagramTests
{
    [Fact]
    public void Diagram_DrawsNodesEdgesAndTheForEachSubgraph()
    {
        var diagram = WorkflowDagDiagram.Generate(V6Fixtures.SingleCollection());

        Assert.StartsWith("flowchart TD", diagram);
        // Inputs as stadium nodes.
        Assert.Contains("input_consult_draft([\"input:consult_draft\"])", diagram);
        // A scalar node with its schema annotation.
        Assert.Contains("extract-patient-concepts<br/>Extracting clinical concepts<br/>output: concept-list", diagram);
        // The fan-in edge with a renderer annotation.
        Assert.Contains("-->|\"patient_trajectory_concepts (as concept-context)\"|", diagram);
        // The forEach chain as a per-collection subgraph fed by its collection.
        Assert.Contains("-->|\"forEach\"| foreach_", diagram);
        Assert.Contains("subgraph foreach_", diagram);
        // Item-aligned edges chain the steps inside the box.
        Assert.Contains("standard_section_draft -->|\"standard_section_draft\"| patient_section_draft", diagram);
        // The v6 aggregator box and its ordered source edge.
        Assert.Contains("assemble_note", diagram);
        Assert.Contains("-->|\"aggregate\"| assemble_note", diagram);
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
        var noNodes = V5Fixtures.Manifest() with { Nodes = null };

        Assert.Throws<InvalidOperationException>(() => WorkflowDagDiagram.Generate(noNodes));
    }
}

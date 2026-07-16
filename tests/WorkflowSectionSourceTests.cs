using Consultologist.Api.Workflow;

namespace Consultologist.Api.Tests;

public class WorkflowPackageSectionsTests
{
    [Fact]
    public void Resolve_V5Package_ReadsTheResultNodesCollection()
    {
        var manifest = V5Fixtures.Manifest();
        var files = V5Fixtures.Files(manifest);
        var errors = new List<string>();
        var data = WorkflowDataResolver.Resolve(manifest, files, errors);
        Assert.Empty(errors);

        var package = new WorkflowPackage(
            manifest,
            Nodes: manifest.Nodes,
            Data: data,
            ResultNodeId: "section-instructions");

        var sections = WorkflowPackageSections.Resolve(package);

        Assert.Equal(2, sections.Count);
        Assert.Equal(("hpi", "History of Present Illness", "Document the presenting illness."), (sections[0].Id, sections[0].Name, sections[0].Content));
    }

}

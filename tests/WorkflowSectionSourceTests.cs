using Consultologist.Api.Workflow;

namespace Consultologist.Api.Tests;

public class WorkflowStandardsParserTests
{
    [Fact]
    public void Parse_SplitsIdColonNameHeadings_InOrder()
    {
        var sections = WorkflowStandardsParser.Parse(
            "## hpi: History of Present Illness\n\nChronological prose.\n\n## pmh: Past Medical History\n\nList prior conditions.\n");

        Assert.Equal(2, sections.Count);
        Assert.Equal(("hpi", "History of Present Illness", "Chronological prose."), (sections[0].Id, sections[0].Name, sections[0].Content));
        Assert.Equal(("pmh", "Past Medical History", "List prior conditions."), (sections[1].Id, sections[1].Name, sections[1].Content));
    }

    [Fact]
    public void Parse_HeadingWithoutColon_GeneratesTheId()
    {
        // Byte parity with the retired frontend parser: lowercased, spaces → underscores.
        var sections = WorkflowStandardsParser.Parse("## Social History\n\nAsk about supports.");

        Assert.Equal("social_history", sections[0].Id);
        Assert.Equal("Social History", sections[0].Name);
        Assert.Equal("Ask about supports.", sections[0].Content);
    }

    [Fact]
    public void Parse_HandlesCrLfAndPreambleOutsideSections()
    {
        var sections = WorkflowStandardsParser.Parse("Preamble ignored.\r\n## a: A\r\nBody line.\r\n");

        Assert.Single(sections);
        Assert.Equal("Body line.", sections[0].Content);
    }

    [Fact]
    public void Parse_RealGeneralStandards_MatchesTheNineSections()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Consultologist.sln")))
        {
            dir = dir.Parent;
        }

        var sections = WorkflowStandardsParser.Parse(
            File.ReadAllText(Path.Combine(dir!.FullName, "packages", "general", "standards.md")));

        Assert.Equal(9, sections.Count);
        Assert.Equal("hpi", sections[0].Id);
        Assert.Equal("assessment_plan", sections[^1].Id);
        Assert.All(sections, s => Assert.False(string.IsNullOrWhiteSpace(s.Content)));
    }
}

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
            manifest, string.Empty,
            Nodes: manifest.Nodes,
            Data: data,
            ResultNodeId: "section-instructions");

        var sections = WorkflowPackageSections.Resolve(package);

        Assert.Equal(2, sections.Count);
        Assert.Equal(("hpi", "History of Present Illness", "Document the presenting illness."), (sections[0].Id, sections[0].Name, sections[0].Content));
    }

    [Fact]
    public void Resolve_PreV5Package_ParsesStandardsMarkdown()
    {
        var package = new WorkflowPackage(
            V4Fixtures.Manifest(),
            "## hpi: History of Present Illness\n\nChronological prose.");

        var sections = WorkflowPackageSections.Resolve(package);

        Assert.Single(sections);
        Assert.Equal("hpi", sections[0].Id);
        Assert.Equal("Chronological prose.", sections[0].Content);
    }
}

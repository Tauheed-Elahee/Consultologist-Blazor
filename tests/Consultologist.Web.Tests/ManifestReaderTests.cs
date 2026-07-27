using System.Text.Json;
using Consultologist.Web.Services.Workflow;

namespace Consultologist.Web.Tests;

/// <summary>
/// The reader's v7 sections (#218). Case tolerance matters because the worker
/// serializer can emit PascalCase while repo manifests are camelCase.
/// </summary>
public class ManifestReaderTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void ReadInputs_ReadsIdLabelAndRequired()
    {
        var inputs = WorkflowManifestReader.ReadInputs(Parse("""
            { "inputs": [
                { "id": "consult_draft", "label": "Consult draft", "required": true },
                { "id": "prior_notes", "label": "Prior notes", "required": false }
            ] }
            """));

        Assert.Equal(
            new[] { ("consult_draft", "Consult draft", true), ("prior_notes", "Prior notes", false) },
            inputs.Select(i => (i.Id, i.Label, i.Required)).ToArray());
    }

    [Fact]
    public void ReadInputs_DefaultsRequiredToTrueWhenAbsent()
    {
        var input = Assert.Single(WorkflowManifestReader.ReadInputs(
            Parse("""{ "inputs": [ { "id": "consult_draft", "label": "Consult draft" } ] }""")));

        Assert.True(input.Required);
    }

    [Fact]
    public void ReadInputs_ToleratesPascalCase()
    {
        var input = Assert.Single(WorkflowManifestReader.ReadInputs(
            Parse("""{ "Inputs": [ { "Id": "consult_draft", "Label": "Consult draft", "Required": false } ] }""")));

        Assert.Equal("consult_draft", input.Id);
        Assert.False(input.Required);
    }

    [Fact]
    public void ReadResults_ReadsTheDeclaredSet()
    {
        var results = WorkflowManifestReader.ReadResults(Parse("""
            { "results": [
                { "id": "consult_note", "node": "node:assemble-note", "label": "Consultation note" },
                { "id": "patient_letter", "node": "node:assemble-letter", "label": "Patient letter" }
            ] }
            """));

        Assert.Equal(
            new[] { ("consult_note", "node:assemble-note"), ("patient_letter", "node:assemble-letter") },
            results.Select(r => (r.Id, r.Node)).ToArray());
    }

    [Fact]
    public void LegacyManifest_ReadsNeitherSection()
    {
        var manifest = Parse("""{ "result": "node:assemble-note" }""");

        Assert.Empty(WorkflowManifestReader.ReadInputs(manifest));
        Assert.Empty(WorkflowManifestReader.ReadResults(manifest));
        Assert.Equal("node:assemble-note", WorkflowManifestReader.ReadResultRef(manifest));
    }
}

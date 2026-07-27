using System.Text.Json;
using Consultologist.Web.Services.Workflow;

namespace Consultologist.Web.Tests;

/// <summary>
/// Manifests for the editor tests, in the camelCase the content repo authors
/// (the reader also tolerates the PascalCase the worker serializer can emit).
/// Small but structurally real: a fan, an aggregator, and a deliverable.
/// </summary>
public static class EditorFixtures
{
    public const string PromptFile = "prompts/draft-section.md";
    public const string StandardsIndex = "data/standards/index.json";

    public static WorkflowPackageContentResponse Package(string manifestJson, int specVersion) =>
        new(
            "acct-1234567890ab",
            "v2026.07.1",
            specVersion,
            JsonDocument.Parse(manifestJson).RootElement.Clone(),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PromptFile] = "Draft {{ section_name }} from {{ consult_draft }}.",
                [StandardsIndex] = """
                    { "fields": ["id", "name", "content"], "items": [ { "id": "hpi", "name": "History", "file": "hpi.md" } ] }
                    """,
                ["data/standards/hpi.md"] = "Document the presenting illness."
            });

    /// <summary>The v6 shape: a string result naming an aggregator.</summary>
    public static WorkflowPackageContentResponse V6() => Package("""
        {
          "name": "acct-1234567890ab",
          "version": "v2026.07.1",
          "specVersion": 6,
          "templating": { "engine": "scriban", "engineVersion": "7.2.5" },
          "data": { "standards": "data/standards/" },
          "prompts": [
            { "id": "draft-section", "file": "prompts/draft-section.md",
              "variables": ["section_name", "consult_draft"] }
          ],
          "result": "node:assemble-note",
          "nodes": [
            { "id": "draft-section", "forEach": "data:standards", "label": "Drafting section",
              "prompt": "draft-section",
              "bindings": { "section_name": "item:name", "consult_draft": "input:consult_draft" } },
            { "id": "assemble-note", "label": "Assembling note", "aggregate": ["node:draft-section"] }
          ]
        }
        """, 6);

    /// <summary>The v7 shape: declared inputs and a results list.</summary>
    public static WorkflowPackageContentResponse V7() => Package("""
        {
          "name": "acct-1234567890ab",
          "version": "v2026.07.1",
          "specVersion": 7,
          "templating": { "engine": "scriban", "engineVersion": "7.2.5" },
          "inputs": [
            { "id": "consult_draft", "label": "Consult draft", "required": true },
            { "id": "prior_notes", "label": "Prior notes", "required": false }
          ],
          "data": { "standards": "data/standards/" },
          "prompts": [
            { "id": "draft-section", "file": "prompts/draft-section.md",
              "variables": ["section_name", "consult_draft"] }
          ],
          "results": [
            { "id": "consult_note", "node": "node:assemble-note", "label": "Consultation note" }
          ],
          "nodes": [
            { "id": "draft-section", "forEach": "data:standards", "label": "Drafting section",
              "prompt": "draft-section",
              "bindings": { "section_name": "item:name", "consult_draft": "input:consult_draft" } },
            { "id": "assemble-note", "label": "Assembling note", "aggregate": ["node:draft-section"] }
          ]
        }
        """, 7);
}

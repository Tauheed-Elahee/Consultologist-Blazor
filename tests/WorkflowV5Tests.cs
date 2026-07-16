using System.Text.Json;
using Consultologist.Api.Workflow;

namespace Consultologist.Api.Tests;

/// <summary>
/// The canonical v5 respelling of the general package: the package-format-v5.md
/// normative example as an in-memory fixture.
/// </summary>
public static class V5Fixtures
{
    public const string SchemaPath = "schemas/concept-list.json";
    public const string StandardsDir = "data/standards/";

    public static WorkflowPackageManifest Manifest()
    {
        // Fully standalone: the package-format-v5.md normative example spelled out —
        // no derivation from pre-v5 fixtures or synthesis helpers.
        var prompts = new List<WorkflowPromptSpec>
        {
            new("extract-patient-concepts", "prompts/extract-patient-concepts.md",
                new List<string> { "consult_draft" }, "snomed-tool-guidance"),
            new("identify-problem", "prompts/identify-problem.md",
                new List<string> { "patient_concepts" }, "snomed-tool-guidance"),
            new("create-typical-trajectory", "prompts/create-typical-trajectory.md",
                new List<string> { "problem_concepts" }, "snomed-tool-guidance"),
            new("create-patient-trajectory", "prompts/create-patient-trajectory.md",
                new List<string> { "problem_concepts", "patient_concepts", "typical_trajectory_concepts" },
                "snomed-tool-guidance"),
            new("standard-section-draft", "prompts/standard-section-draft.md",
                new List<string> { "section_name", "patient_trajectory_concepts" }),
            new("patient-section-draft", "prompts/patient-section-draft.md",
                new List<string> { "standard_section_draft", "consult_draft", "section_name" }),
            new("section-instructions", "prompts/section-instructions.md",
                new List<string> { "patient_section_draft", "section_name", "section_standard" })
        };

        var nodes = new List<WorkflowNodeSpec>
        {
            new("extract-patient-concepts", null, "Extracting clinical concepts",
                Prompt: "extract-patient-concepts",
                Bindings: new Dictionary<string, WorkflowBindingValue>(StringComparer.Ordinal)
                {
                    ["consult_draft"] = new("input:consult_draft")
                },
                Output: new WorkflowNodeOutputSpec("concept-list",
                    "The consult could not be processed because clinical concepts could not be extracted from the draft.")),
            new("identify-problem", null, "Identifying primary problem",
                Prompt: "identify-problem",
                Bindings: new Dictionary<string, WorkflowBindingValue>(StringComparer.Ordinal)
                {
                    ["patient_concepts"] = new("node:extract-patient-concepts")
                },
                Output: new WorkflowNodeOutputSpec("concept-list",
                    "No valid disease or problem concept was identified.")),
            new("create-typical-trajectory", null, "Building reference trajectory",
                Prompt: "create-typical-trajectory",
                Bindings: new Dictionary<string, WorkflowBindingValue>(StringComparer.Ordinal)
                {
                    ["problem_concepts"] = new("node:identify-problem")
                },
                Output: new WorkflowNodeOutputSpec("concept-list",
                    "No valid typical trajectory concepts were generated.")),
            new("create-patient-trajectory", null, "Building patient trajectory",
                Prompt: "create-patient-trajectory",
                Bindings: new Dictionary<string, WorkflowBindingValue>(StringComparer.Ordinal)
                {
                    ["problem_concepts"] = new("node:identify-problem"),
                    ["patient_concepts"] = new("node:extract-patient-concepts"),
                    ["typical_trajectory_concepts"] = new("node:create-typical-trajectory")
                },
                Output: new WorkflowNodeOutputSpec("concept-list",
                    "No valid patient trajectory concepts were generated.")),
            new("standard-section-draft", null, "Drafting section",
                Prompt: "standard-section-draft",
                Bindings: new Dictionary<string, WorkflowBindingValue>(StringComparer.Ordinal)
                {
                    ["section_name"] = new("item:name"),
                    ["patient_trajectory_concepts"] = new("node:create-patient-trajectory", "concept-context")
                },
                ForEach: "data:standards"),
            new("patient-section-draft", null, "Applying patient information",
                Prompt: "patient-section-draft",
                Bindings: new Dictionary<string, WorkflowBindingValue>(StringComparer.Ordinal)
                {
                    ["standard_section_draft"] = new("node:standard-section-draft"),
                    ["consult_draft"] = new("input:consult_draft"),
                    ["section_name"] = new("item:name")
                },
                ForEach: "data:standards"),
            new("section-instructions", null, "Applying section instructions",
                Prompt: "section-instructions",
                Bindings: new Dictionary<string, WorkflowBindingValue>(StringComparer.Ordinal)
                {
                    ["patient_section_draft"] = new("node:patient-section-draft"),
                    ["section_name"] = new("item:name"),
                    ["section_standard"] = new("item:content")
                },
                ForEach: "data:standards")
        };

        return new WorkflowPackageManifest(
            "general",
            "v2026.08.1",
            5,
            new WorkflowTemplatingSpec("scriban", "7.2.5"),
            new Dictionary<string, string> { ["snomed-tool-guidance"] = "prompts/_snomed-tool-guidance.md" },
            prompts,
            Schemas: new Dictionary<string, string> { ["concept-list"] = SchemaPath },
            Nodes: nodes,
            DerivedFrom: null,
            Data: new Dictionary<string, string> { ["standards"] = StandardsDir },
            Result: "node:section-instructions");
    }

    public static Dictionary<string, string> Files(WorkflowPackageManifest manifest)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["prompts/_snomed-tool-guidance.md"] = "Use short SNOMED search terms."
        };

        foreach (var prompt in manifest.Prompts!)
        {
            files[prompt.File] = string.Join(
                "\n\n",
                prompt.Variables.Select(v => $"{v} block:\n{{{{ {v} }}}}"));
        }

        files[SchemaPath] = TestOutputContracts.ConceptListSchema;
        files[StandardsDir + "index.json"] = """
            {
              "fields": ["id", "name", "content"],
              "items": [
                { "id": "hpi", "name": "History of Present Illness", "file": "hpi.md" },
                { "id": "pmh", "name": "Past Medical History", "file": "pmh.md" }
              ]
            }
            """;
        files[StandardsDir + "hpi.md"] = "Document the presenting illness.";
        files[StandardsDir + "pmh.md"] = "List prior conditions with dates.";
        return files;
    }

    public static WorkflowPackageValidator.ValidationResult Validate(WorkflowPackageManifest manifest, Dictionary<string, string>? files = null)
        => WorkflowPackageValidator.Validate(manifest, files ?? Files(manifest), TestOutputContracts.CatalogSchemas);
}

public class WorkflowBindingValueTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void Deserializes_StringForm()
    {
        var value = JsonSerializer.Deserialize<WorkflowBindingValue>("\"input:consult_draft\"", JsonOptions)!;

        Assert.Equal("input:consult_draft", value.From);
        Assert.Null(value.As);
    }

    [Fact]
    public void Deserializes_ObjectForm()
    {
        var value = JsonSerializer.Deserialize<WorkflowBindingValue>(
            """{ "from": "node:create-patient-trajectory", "as": "concept-context" }""", JsonOptions)!;

        Assert.Equal("node:create-patient-trajectory", value.From);
        Assert.Equal("concept-context", value.As);
    }

    [Theory]
    [InlineData("\"input:consult_draft\"")]
    [InlineData("""{ "from": "node:x", "as": "concept-context" }""")]
    public void RoundTrips(string json)
    {
        var value = JsonSerializer.Deserialize<WorkflowBindingValue>(json, JsonOptions)!;
        var serialized = JsonSerializer.Serialize(value, JsonOptions);
        var reparsed = JsonSerializer.Deserialize<WorkflowBindingValue>(serialized, JsonOptions)!;

        Assert.Equal(value, reparsed);
    }

    [Theory]
    [InlineData("""{ "as": "concept-context" }""")]
    [InlineData("""{ "from": "node:x", "renderer": "concept-context" }""")]
    [InlineData("42")]
    public void RejectsMalformedValues(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<WorkflowBindingValue>(json, JsonOptions));
    }
}

public class WorkflowNodeBindingSourceParserTests
{
    [Theory]
    [InlineData("input:consult_draft")]
    [InlineData("input:sections")]
    [InlineData("item:name")]
    [InlineData("item:standard")]
    [InlineData("item:id")]
    [InlineData("item:title")] // any field name parses; vocabulary closures are validator rules
    [InlineData("previous_step_output")]
    [InlineData("node:extract-patient-concepts")]
    [InlineData("data:standards")]
    [InlineData("data:clinic-guidelines")]
    public void Parses_EveryVocabularyForm(string raw)
    {
        Assert.True(WorkflowNodeBindingSources.TryParse(raw, out var source, out var error), error);
        Assert.NotNull(source);
    }

    [Theory]
    [InlineData("consult_draft")]
    [InlineData("input:patient_age")]
    [InlineData("node:")]
    [InlineData("data:")]
    [InlineData("output:x")]
    public void Rejects_UnknownForms(string raw)
    {
        Assert.False(WorkflowNodeBindingSources.TryParse(raw, out _, out var error));
        Assert.NotNull(error);
    }
}

public class WorkflowManifestV5Tests
{
    [Fact]
    public void ManifestV5_DeserializesTheNewSurface()
    {
        const string json = """
            {
              "name": "general", "version": "v2026.08.1", "specVersion": 5,
              "derivedFrom": "general@v2026.07.6",
              "data": { "standards": "data/standards/", "clinic-guidelines": "data/clinic-guidelines.md" },
              "result": "node:section-instructions",
              "nodes": [
                { "id": "draft", "forEach": "data:standards", "label": "Drafting",
                  "prompt": "p", "bindings": { "section_name": "item:name" } }
              ]
            }
            """;

        var manifest = JsonSerializer.Deserialize<WorkflowPackageManifest>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.Equal("general@v2026.07.6", manifest.DerivedFrom);
        Assert.Equal("data/standards/", manifest.Data!["standards"]);
        Assert.Equal("node:section-instructions", manifest.Result);
        Assert.Equal("data:standards", manifest.Nodes![0].ForEach);
        Assert.Null(manifest.Nodes[0].Kind);
    }
}

public class WorkflowV5ValidationTests
{
    private static WorkflowNodeSpec Node(WorkflowPackageManifest manifest, string id)
        => manifest.Nodes!.Single(n => n.Id == id);

    private static void Replace(WorkflowPackageManifest manifest, string id, WorkflowNodeSpec replacement)
        => manifest.Nodes![manifest.Nodes.FindIndex(n => n.Id == id)] = replacement;

    [Fact]
    public void Validate_PassesForCanonicalV5Package()
    {
        var result = V5Fixtures.Validate(V5Fixtures.Manifest());

        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void V5Surface_RequiresSpecVersion5()
    {
        var v4 = V4Fixtures.Manifest() with
        {
            DerivedFrom = "general@v2026.07.5",
            Data = new Dictionary<string, string> { ["x"] = "data/x.md" },
            Result = "node:sections"
        };
        var errors = WorkflowPackageValidator.Validate(v4, V4Fixtures.Files(v4), TestOutputContracts.CatalogSchemas).Errors;

        Assert.Contains(errors, e => e.Contains("derivedFrom requires specVersion 5"));
        Assert.Contains(errors, e => e.Contains("data requires specVersion 5"));
        Assert.Contains(errors, e => e.Contains("result requires specVersion 5"));

        var v4WithForEach = V4Fixtures.Manifest();
        Replace(v4WithForEach, "extract-patient-concepts",
            Node(v4WithForEach, "extract-patient-concepts") with { ForEach = "data:standards" });
        Assert.Contains(
            WorkflowPackageValidator.Validate(v4WithForEach, V4Fixtures.Files(v4WithForEach), TestOutputContracts.CatalogSchemas).Errors,
            e => e.Contains("forEach requires specVersion 5"));
    }

    [Theory]
    [InlineData("not-a-ref", "not a valid package reference")]
    [InlineData("general@latest", "never @latest")]
    public void Validate_RejectsMalformedDerivedFrom(string derivedFrom, string expected)
    {
        var manifest = V5Fixtures.Manifest() with { DerivedFrom = derivedFrom };

        Assert.Contains(V5Fixtures.Validate(manifest).Errors, e => e.Contains(expected));
    }

    [Fact]
    public void Validate_AcceptsConcreteDerivedFrom()
    {
        var manifest = V5Fixtures.Manifest() with { DerivedFrom = "general@v2026.07.6" };

        Assert.True(V5Fixtures.Validate(manifest).IsValid);
    }

    [Fact]
    public void Validate_RejectsV4Vocabulary()
    {
        var manifest = V5Fixtures.Manifest();
        Replace(manifest, "standard-section-draft",
            Node(manifest, "standard-section-draft") with { Kind = "prompt", Over = "input:sections" });

        var errors = V5Fixtures.Validate(manifest).Errors;

        Assert.Contains(errors, e => e.Contains("specVersion 5 has one node kind"));
        Assert.Contains(errors, e => e.Contains("declares over"));
    }

    [Theory]
    [InlineData(null, "result is required in specVersion 5")]
    [InlineData("section-instructions", "must be 'node:<id>'")]
    [InlineData("node:nope", "references unknown node 'nope'")]
    [InlineData("node:extract-patient-concepts", "must reference a forEach node")]
    public void Validate_RejectsBadResult(string? result, string expected)
    {
        var manifest = V5Fixtures.Manifest() with { Result = result };

        Assert.Contains(V5Fixtures.Validate(manifest).Errors, e => e.Contains(expected));
    }

    [Theory]
    [InlineData("input:sections", "must be a data: collection reference")]
    [InlineData("data:nope", "unknown data entry 'nope'")]
    public void Validate_RejectsBadForEach(string forEach, string expected)
    {
        var manifest = V5Fixtures.Manifest();
        Replace(manifest, "section-instructions",
            Node(manifest, "section-instructions") with { ForEach = forEach });

        Assert.Contains(V5Fixtures.Validate(manifest).Errors, e => e.Contains(expected));
    }

    [Fact]
    public void Validate_RejectsForEachOverScalarData()
    {
        var manifest = V5Fixtures.Manifest();
        manifest.Data!["notes"] = "data/notes.md";
        var files = V5Fixtures.Files(manifest);
        files["data/notes.md"] = "scalar text";
        Replace(manifest, "section-instructions",
            Node(manifest, "section-instructions") with { ForEach = "data:notes" });

        Assert.Contains(
            V5Fixtures.Validate(manifest, files).Errors,
            e => e.Contains("scalar data entry 'notes' (forEach requires a collection)"));
    }

    [Fact]
    public void Validate_ScalarDataBindingIsValid_CollectionBindingIsNot()
    {
        var manifest = V5Fixtures.Manifest();
        manifest.Data!["clinic-guidelines"] = "data/clinic-guidelines.md";
        var files = V5Fixtures.Files(manifest);
        files["data/clinic-guidelines.md"] = "Local guidance.";

        // Rebind an existing variable to the scalar: valid.
        var node = Node(manifest, "patient-section-draft");
        var bindings = new Dictionary<string, WorkflowBindingValue>(node.Bindings!, StringComparer.Ordinal)
        {
            ["consult_draft"] = new("data:clinic-guidelines")
        };
        Replace(manifest, "patient-section-draft", node with { Bindings = bindings });
        Assert.True(V5Fixtures.Validate(manifest, files).IsValid, string.Join(" | ", V5Fixtures.Validate(manifest, files).Errors));

        // Binding the collection itself: rejected.
        bindings["consult_draft"] = new("data:standards");
        Assert.Contains(
            V5Fixtures.Validate(manifest, files).Errors,
            e => e.Contains("data collection 'standards', which is only iterable via forEach"));
    }

    [Theory]
    [InlineData("previous_step_output", "removed in specVersion 5")]
    [InlineData("input:sections", "removed in specVersion 5")]
    public void Validate_RejectsRetiredSources(string source, string expected)
    {
        var manifest = V5Fixtures.Manifest();
        var node = Node(manifest, "section-instructions");
        var bindings = new Dictionary<string, WorkflowBindingValue>(node.Bindings!, StringComparer.Ordinal)
        {
            ["patient_section_draft"] = new(source)
        };
        Replace(manifest, "section-instructions", node with { Bindings = bindings });

        Assert.Contains(V5Fixtures.Validate(manifest).Errors, e => e.Contains(expected));
    }

    [Fact]
    public void Validate_RejectsItemBindingWithoutForEach()
    {
        var manifest = V5Fixtures.Manifest();
        var node = Node(manifest, "identify-problem");
        var bindings = new Dictionary<string, WorkflowBindingValue>(node.Bindings!, StringComparer.Ordinal)
        {
            ["patient_concepts"] = new("item:name")
        };
        Replace(manifest, "identify-problem", node with { Bindings = bindings });

        Assert.Contains(V5Fixtures.Validate(manifest).Errors, e => e.Contains("but declares no forEach"));
    }

    [Fact]
    public void Validate_RejectsItemFieldOutsideTheCollectionDeclaration()
    {
        var manifest = V5Fixtures.Manifest();
        var node = Node(manifest, "section-instructions");
        var bindings = new Dictionary<string, WorkflowBindingValue>(node.Bindings!, StringComparer.Ordinal)
        {
            ["section_standard"] = new("item:standard") // the v4 field name; the collection declares "content"
        };
        Replace(manifest, "section-instructions", node with { Bindings = bindings });

        Assert.Contains(
            V5Fixtures.Validate(manifest).Errors,
            e => e.Contains("unknown item field 'standard' (the collection declares: id, name, content)"));
    }

    [Fact]
    public void Validate_RejectsAggregateEdge()
    {
        // A scalar node binding a forEach node's output: the closed forEach→scalar edge.
        var manifest = V5Fixtures.Manifest();
        var node = Node(manifest, "identify-problem");
        var bindings = new Dictionary<string, WorkflowBindingValue>(node.Bindings!, StringComparer.Ordinal)
        {
            ["patient_concepts"] = new("node:section-instructions")
        };
        Replace(manifest, "identify-problem", node with { Bindings = bindings });

        Assert.Contains(
            V5Fixtures.Validate(manifest).Errors,
            e => e.Contains("aggregate output is not bindable in specVersion 5.0"));
    }

    [Fact]
    public void Validate_RejectsCrossCollectionEdge()
    {
        var manifest = V5Fixtures.Manifest();
        manifest.Data!["glossary"] = "data/glossary/";
        var files = V5Fixtures.Files(manifest);
        files["data/glossary/index.json"] = """
            { "fields": ["id", "name"], "items": [ { "id": "terms", "name": "Terms" } ] }
            """;
        Replace(manifest, "section-instructions",
            Node(manifest, "section-instructions") with { ForEach = "data:glossary" });

        Assert.Contains(
            V5Fixtures.Validate(manifest, files).Errors,
            e => e.Contains("across collections ('data:glossary' vs 'data:standards')"));
    }

    [Fact]
    public void Validate_RejectsCyclesThroughItemAlignedEdges()
    {
        var manifest = V5Fixtures.Manifest();
        var node = Node(manifest, "standard-section-draft");
        var bindings = new Dictionary<string, WorkflowBindingValue>(node.Bindings!, StringComparer.Ordinal)
        {
            ["section_name"] = new("node:section-instructions")
        };
        Replace(manifest, "standard-section-draft", node with { Bindings = bindings });

        var errors = V5Fixtures.Validate(manifest).Errors;

        Assert.Contains(errors, e => e.Contains("cycle"));
    }

    [Theory]
    [InlineData("""{ "fields": ["name", "content"], "items": [ { "id": "a", "name": "A", "file": "hpi.md" } ] }""", "must include 'id' and 'name'")]
    [InlineData("""{ "fields": ["id", "name", "author"], "items": [ { "id": "a", "name": "A" } ] }""", "declares field 'author' with no source")]
    [InlineData("""{ "fields": ["id", "name", "content"], "items": [ { "id": "a", "name": "A", "file": "hpi.md" }, { "id": "a", "name": "B", "file": "pmh.md" } ] }""", "duplicate item id 'a'")]
    [InlineData("""{ "fields": ["id", "name", "content"], "items": [ { "id": "a", "name": "A", "file": "missing.md" } ] }""", "file 'data/standards/missing.md' is missing")]
    [InlineData("""{ "fields": ["id", "name", "content"], "items": [ { "id": "a", "name": "A" } ] }""", "declares no file")]
    [InlineData("""{ "fields": [], "items": [] }""", "declares no fields")]
    [InlineData("not json", "not valid JSON")]
    public void Validate_RejectsDefectiveCollectionIndexes(string indexJson, string expected)
    {
        var manifest = V5Fixtures.Manifest();
        var files = V5Fixtures.Files(manifest);
        files[V5Fixtures.StandardsDir + "index.json"] = indexJson;

        Assert.Contains(V5Fixtures.Validate(manifest, files).Errors, e => e.Contains(expected));
    }

    [Fact]
    public void Validate_RejectsMissingIndexAndMissingScalar()
    {
        var manifest = V5Fixtures.Manifest();
        manifest.Data!["notes"] = "data/notes.md";
        var files = V5Fixtures.Files(manifest);
        files.Remove(V5Fixtures.StandardsDir + "index.json");

        var errors = V5Fixtures.Validate(manifest, files).Errors;

        Assert.Contains(errors, e => e.Contains("missing 'data/standards/index.json'"));
        Assert.Contains(errors, e => e.Contains("Data entry 'notes' file 'data/notes.md' is missing"));
    }

    [Fact]
    public void Validate_RejectsTwoForEachCollections()
    {
        var manifest = V5Fixtures.Manifest();
        manifest.Data!["glossary"] = "data/glossary/";
        var files = V5Fixtures.Files(manifest);
        files["data/glossary/index.json"] = """
            { "fields": ["id", "name"], "items": [ { "id": "terms", "name": "Terms" } ] }
            """;
        // A disconnected second chain over another collection: no cross edges, still closed.
        manifest.Nodes!.Add(new WorkflowNodeSpec(
            "glossary-node", null, "Glossary", Prompt: "extract-patient-concepts",
            Bindings: new Dictionary<string, WorkflowBindingValue>(StringComparer.Ordinal)
            {
                ["consult_draft"] = new("input:consult_draft")
            },
            ForEach: "data:glossary"));

        Assert.Contains(
            V5Fixtures.Validate(manifest, files).Errors,
            e => e.Contains("All forEach nodes must share one collection in specVersion 5.0"));
    }

    [Fact]
    public void DataResolver_MaterializesContentFromItemFiles()
    {
        var manifest = V5Fixtures.Manifest();
        var errors = new List<string>();

        var data = WorkflowDataResolver.Resolve(manifest, V5Fixtures.Files(manifest), errors);

        Assert.Empty(errors);
        var standards = data.Collections["standards"];
        Assert.Equal(new[] { "id", "name", "content" }, standards.Fields);
        Assert.Equal(2, standards.Items.Count);
        Assert.Equal("hpi", standards.Items[0].Id);
        Assert.Equal("History of Present Illness", standards.Items[0].Fields["name"]);
        Assert.Equal("Document the presenting illness.", standards.Items[0].Fields["content"]);
    }
}

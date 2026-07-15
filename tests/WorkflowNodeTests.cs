using System.Text.Json;
using Consultologist.Api.Workflow;

namespace Consultologist.Api.Tests;

public static class V4Fixtures
{
    public const string SchemaPath = "schemas/concept-list.json";

    /// <summary>The v3 fixture package upgraded to specVersion 4 with the canonical DAG.</summary>
    public static WorkflowPackageManifest Manifest()
    {
        var steps = WorkflowSectionStepDefaults.V2Synthesized
            .Select(step => step with { Bindings = new Dictionary<string, string>(step.Bindings, StringComparer.Ordinal) })
            .ToList();

        return V2Fixtures.Manifest() with
        {
            SpecVersion = 4,
            SectionSteps = null,
            Schemas = new Dictionary<string, string> { [WorkflowNodeDefaults.ConceptListSchemaId] = SchemaPath },
            Nodes = WorkflowNodeDefaults.V3SynthesizedDag(steps).ToList()
        };
    }

    public static Dictionary<string, string> Files(WorkflowPackageManifest manifest)
    {
        var files = V2Fixtures.Files(manifest);
        files[SchemaPath] = TestOutputContracts.ConceptListSchema;
        return files;
    }
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
    [InlineData("previous_step_output")]
    [InlineData("node:extract-patient-concepts")]
    public void Parses_EveryVocabularyForm(string raw)
    {
        Assert.True(WorkflowNodeBindingSources.TryParse(raw, out var source, out var error), error);
        Assert.NotNull(source);
    }

    [Theory]
    [InlineData("consult_draft")]
    [InlineData("input:patient_age")]
    [InlineData("item:title")]
    [InlineData("node:")]
    [InlineData("output:x")]
    public void Rejects_UnknownForms(string raw)
    {
        Assert.False(WorkflowNodeBindingSources.TryParse(raw, out _, out var error));
        Assert.NotNull(error);
    }
}

public class WorkflowManifestV4Tests
{
    [Fact]
    public void ManifestV4_DeserializesNodesAndSchemas()
    {
        const string json = """
            {
              "name": "general",
              "version": "v2026.08.1",
              "specVersion": 4,
              "schemas": { "concept-list": "schemas/concept-list.json" },
              "nodes": [
                { "id": "extract", "kind": "prompt", "label": "Extracting",
                  "prompt": "extract-patient-concepts",
                  "bindings": { "consult_draft": "input:consult_draft" },
                  "output": { "schema": "concept-list", "failIfEmpty": "No concepts." } },
                { "id": "sections", "kind": "map", "label": "Generating sections",
                  "over": "input:sections",
                  "steps": [
                    { "prompt": "standard-section-draft", "label": "Drafting section",
                      "bindings": { "section_name": "item:name",
                                    "patient_trajectory_concepts": { "from": "node:extract", "as": "concept-context" } } }
                  ] }
              ]
            }
            """;

        var manifest = JsonSerializer.Deserialize<WorkflowPackageManifest>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.Equal("schemas/concept-list.json", manifest.Schemas![WorkflowNodeDefaults.ConceptListSchemaId]);
        Assert.Equal(2, manifest.Nodes!.Count);
        Assert.Equal("concept-list", manifest.Nodes[0].Output!.Schema);
        Assert.Equal("No concepts.", manifest.Nodes[0].Output!.FailIfEmpty);
        Assert.Equal("input:sections", manifest.Nodes[1].Over);
        var binding = manifest.Nodes[1].Steps![0].Bindings["patient_trajectory_concepts"];
        Assert.Equal("node:extract", binding.From);
        Assert.Equal("concept-context", binding.As);
    }
}

public class WorkflowNodeValidationTests
{
    private static WorkflowPackageValidator.ValidationResult Validate(WorkflowPackageManifest manifest) =>
        WorkflowPackageValidator.Validate(manifest, V4Fixtures.Files(manifest), TestOutputContracts.CatalogSchemas);

    private static WorkflowNodeSpec Node(WorkflowPackageManifest manifest, string id) =>
        manifest.Nodes!.Single(n => n.Id == id);

    private static void Replace(WorkflowPackageManifest manifest, string id, WorkflowNodeSpec replacement)
    {
        var index = manifest.Nodes!.FindIndex(n => n.Id == id);
        manifest.Nodes[index] = replacement;
    }

    [Fact]
    public void Validate_PassesForCanonicalV4Package()
    {
        var result = Validate(V4Fixtures.Manifest());

        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Validate_RejectsMissingOrEmptyNodes()
    {
        Assert.Contains(
            Validate(V4Fixtures.Manifest() with { Nodes = null }).Errors,
            e => e.Contains("nodes is required"));
        Assert.Contains(
            Validate(V4Fixtures.Manifest() with { Nodes = new List<WorkflowNodeSpec>() }).Errors,
            e => e.Contains("nodes is required"));
    }

    [Fact]
    public void Validate_RejectsNodesOnV3_AndSectionStepsOnV4()
    {
        var v3WithNodes = V3Fixtures.Manifest() with { Nodes = V4Fixtures.Manifest().Nodes };
        Assert.Contains(
            WorkflowPackageValidator.Validate(v3WithNodes, V3Fixtures.Files(v3WithNodes), TestOutputContracts.CatalogSchemas).Errors,
            e => e.Contains("nodes requires specVersion 4"));

        var v4WithSteps = V4Fixtures.Manifest() with
        {
            SectionSteps = WorkflowSectionStepDefaults.V2Synthesized.ToList()
        };
        Assert.Contains(Validate(v4WithSteps).Errors, e => e.Contains("sectionSteps was replaced by nodes"));
    }

    [Fact]
    public void Validate_RejectsDuplicateNodeIds()
    {
        var manifest = V4Fixtures.Manifest();
        manifest.Nodes!.Add(Node(manifest, WorkflowPromptContract.IdentifyProblem));

        Assert.Contains(Validate(manifest).Errors, e => e.Contains("Duplicate node id"));
    }

    [Fact]
    public void Validate_RejectsUnknownKind()
    {
        var manifest = V4Fixtures.Manifest();
        Replace(manifest, WorkflowPromptContract.IdentifyProblem,
            Node(manifest, WorkflowPromptContract.IdentifyProblem) with { Kind = "loop" });

        Assert.Contains(Validate(manifest).Errors, e => e.Contains("unknown kind 'loop'"));
    }

    [Fact]
    public void Validate_RejectsBlankLabel()
    {
        var manifest = V4Fixtures.Manifest();
        Replace(manifest, WorkflowPromptContract.IdentifyProblem,
            Node(manifest, WorkflowPromptContract.IdentifyProblem) with { Label = " " });

        Assert.Contains(Validate(manifest).Errors, e => e.Contains("has no label"));
    }

    [Fact]
    public void Validate_RejectsUndeclaredPromptReference()
    {
        var manifest = V4Fixtures.Manifest();
        Replace(manifest, WorkflowPromptContract.IdentifyProblem,
            Node(manifest, WorkflowPromptContract.IdentifyProblem) with { Prompt = "no-such-prompt" });

        Assert.Contains(Validate(manifest).Errors, e => e.Contains("undeclared prompt 'no-such-prompt'"));
    }

    [Fact]
    public void Validate_RejectsUnparseableBindingSource()
    {
        var manifest = V4Fixtures.Manifest();
        var node = Node(manifest, WorkflowPromptContract.ExtractPatientConcepts);
        node.Bindings![WorkflowPromptContract.ConsultDraft] = new WorkflowBindingValue("draft");

        Assert.Contains(Validate(manifest).Errors, e => e.Contains("unrecognized source 'draft'"));
    }

    [Fact]
    public void Validate_RejectsUnknownNodeReference()
    {
        var manifest = V4Fixtures.Manifest();
        var node = Node(manifest, WorkflowPromptContract.IdentifyProblem);
        node.Bindings![WorkflowPromptContract.PatientConcepts] = new WorkflowBindingValue("node:ghost");

        Assert.Contains(Validate(manifest).Errors, e => e.Contains("unknown node 'ghost'"));
    }

    [Fact]
    public void Validate_RejectsInputSectionsAsPromptBinding()
    {
        var manifest = V4Fixtures.Manifest();
        var node = Node(manifest, WorkflowPromptContract.ExtractPatientConcepts);
        node.Bindings![WorkflowPromptContract.ConsultDraft] = new WorkflowBindingValue(WorkflowNodeBindingSources.InputSections);

        Assert.Contains(Validate(manifest).Errors, e => e.Contains("only valid as a map node's 'over'"));
    }

    [Fact]
    public void Validate_RejectsItemAndPreviousOutsideMapSteps()
    {
        var manifest = V4Fixtures.Manifest();
        var node = Node(manifest, WorkflowPromptContract.ExtractPatientConcepts);
        node.Bindings![WorkflowPromptContract.ConsultDraft] = new WorkflowBindingValue(WorkflowNodeBindingSources.ItemName);
        Assert.Contains(Validate(manifest).Errors, e => e.Contains("only valid inside map steps"));

        var manifest2 = V4Fixtures.Manifest();
        var node2 = Node(manifest2, WorkflowPromptContract.ExtractPatientConcepts);
        node2.Bindings![WorkflowPromptContract.ConsultDraft] = new WorkflowBindingValue(WorkflowNodeBindingSources.PreviousStepOutput);
        Assert.Contains(Validate(manifest2).Errors, e => e.Contains("only valid inside map steps"));
    }

    [Fact]
    public void Validate_RejectsReservedItemId()
    {
        var manifest = V4Fixtures.Manifest();
        var map = Node(manifest, WorkflowNodeDefaults.SectionsMapNodeId);
        map.Steps![0].Bindings[WorkflowPromptContract.SectionName] = new WorkflowBindingValue(WorkflowNodeBindingSources.ItemId);

        Assert.Contains(Validate(manifest).Errors, e => e.Contains("'item:id', which is reserved"));
    }

    [Fact]
    public void Validate_RejectsFirstMapStepPreviousOutput()
    {
        var manifest = V4Fixtures.Manifest();
        var map = Node(manifest, WorkflowNodeDefaults.SectionsMapNodeId);
        map.Steps![0].Bindings[WorkflowPromptContract.SectionName] = new WorkflowBindingValue(WorkflowNodeBindingSources.PreviousStepOutput);

        Assert.Contains(Validate(manifest).Errors, e => e.Contains("first map step cannot bind"));
    }

    [Fact]
    public void Validate_RejectsUnknownRenderer()
    {
        var manifest = V4Fixtures.Manifest();
        var map = Node(manifest, WorkflowNodeDefaults.SectionsMapNodeId);
        map.Steps![0].Bindings[WorkflowPromptContract.PatientTrajectoryConcepts] =
            new WorkflowBindingValue($"node:{WorkflowPromptContract.CreatePatientTrajectory}", "markdown-table");

        Assert.Contains(Validate(manifest).Errors, e => e.Contains("unknown renderer 'markdown-table'"));
    }

    [Fact]
    public void Validate_RejectsRendererOnTextNode()
    {
        var manifest = V4Fixtures.Manifest();
        // Make identify-problem a text node, then render it as concepts downstream.
        Replace(manifest, WorkflowPromptContract.IdentifyProblem,
            Node(manifest, WorkflowPromptContract.IdentifyProblem) with { Output = null });
        var trajectory = Node(manifest, WorkflowPromptContract.CreateTypicalTrajectory);
        trajectory.Bindings![WorkflowPromptContract.ProblemConcepts] =
            new WorkflowBindingValue($"node:{WorkflowPromptContract.IdentifyProblem}", WorkflowConceptRenderers.ConceptBullets);

        Assert.Contains(Validate(manifest).Errors, e => e.Contains("declares no concept-list output"));
    }

    [Fact]
    public void Validate_RejectsBindingsNotMatchingPromptVariables()
    {
        var manifest = V4Fixtures.Manifest();
        var node = Node(manifest, WorkflowPromptContract.ExtractPatientConcepts);
        node.Bindings!["extra_variable"] = new WorkflowBindingValue(WorkflowNodeBindingSources.InputConsultDraft);

        Assert.Contains(Validate(manifest).Errors, e => e.Contains("must exactly match"));
    }

    [Fact]
    public void Validate_RejectsCycles()
    {
        // Two-cycle: extract depends on problem, problem depends on extract.
        var manifest = V4Fixtures.Manifest();
        var extract = Node(manifest, WorkflowPromptContract.ExtractPatientConcepts);
        extract.Bindings![WorkflowPromptContract.ConsultDraft] =
            new WorkflowBindingValue($"node:{WorkflowPromptContract.IdentifyProblem}");

        Assert.Contains(Validate(manifest).Errors, e => e.Contains("contain a cycle"));

        // Self-loop.
        var manifest2 = V4Fixtures.Manifest();
        var problem = Node(manifest2, WorkflowPromptContract.IdentifyProblem);
        problem.Bindings![WorkflowPromptContract.PatientConcepts] =
            new WorkflowBindingValue($"node:{WorkflowPromptContract.IdentifyProblem}");

        Assert.Contains(Validate(manifest2).Errors, e => e.Contains("contain a cycle"));
    }

    [Fact]
    public void Validate_AcceptsDiamondDependencies()
    {
        // create-patient-trajectory already forms a diamond over extract/problem/typical;
        // the canonical package passing is the diamond acceptance proof.
        Assert.True(Validate(V4Fixtures.Manifest()).IsValid);
    }

    [Fact]
    public void Validate_RejectsUndeclaredSchema()
    {
        var manifest = V4Fixtures.Manifest() with { Schemas = null };

        Assert.Contains(Validate(manifest).Errors, e => e.Contains("is not declared in schemas"));
    }

    [Fact]
    public void Validate_RejectsMissingSchemaFile()
    {
        var manifest = V4Fixtures.Manifest();
        var files = V4Fixtures.Files(manifest);
        files.Remove(V4Fixtures.SchemaPath);

        Assert.Contains(
            WorkflowPackageValidator.Validate(manifest, files, TestOutputContracts.CatalogSchemas).Errors,
            e => e.Contains("schemas/concept-list.json") && e.Contains("missing"));
    }

    [Fact]
    public void Validate_SchemaEqualityToleratesTitleAndDescription()
    {
        var manifest = V4Fixtures.Manifest();
        var files = V4Fixtures.Files(manifest);
        var schema = System.Text.Json.Nodes.JsonNode.Parse(TestOutputContracts.ConceptListSchema)!.AsObject();
        schema["title"] = "Concept list";
        schema["description"] = "Concepts extracted from a consult draft.";
        files[V4Fixtures.SchemaPath] = schema.ToJsonString();

        var result = WorkflowPackageValidator.Validate(manifest, files, TestOutputContracts.CatalogSchemas);

        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
    }

    [Fact]
    public void Validate_RejectsSchemaPropertyDrift()
    {
        var manifest = V4Fixtures.Manifest();
        var files = V4Fixtures.Files(manifest);
        files[V4Fixtures.SchemaPath] = TestOutputContracts.ConceptListSchema.Replace("\"isActive\"", "\"active\"");

        Assert.Contains(
            WorkflowPackageValidator.Validate(manifest, files, TestOutputContracts.CatalogSchemas).Errors,
            e => e.Contains("canonically match a catalog output contract"));
    }

    [Fact]
    public void Validate_AcceptsAnyCatalogEntry_NotJustConceptList()
    {
        // The closure is catalog-shaped: a package schema matching a *different*
        // catalog entry is valid — this is what makes new output shapes a
        // zero-code, catalog-plus-agent operation.
        var manifest = V4Fixtures.Manifest();
        var files = V4Fixtures.Files(manifest);
        var summarySchema = """{ "type": "object", "required": ["summary"], "properties": { "summary": { "type": "string" } }, "additionalProperties": false }""";
        files[V4Fixtures.SchemaPath] = summarySchema;

        var twoEntryCatalog = new Dictionary<string, string>(TestOutputContracts.CatalogSchemas, StringComparer.Ordinal)
        {
            ["summary"] = summarySchema
        };

        var result = WorkflowPackageValidator.Validate(manifest, files, twoEntryCatalog);

        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
    }

    [Fact]
    public void Validate_RejectsSecondMapNode()
    {
        var manifest = V4Fixtures.Manifest();
        var map = Node(manifest, WorkflowNodeDefaults.SectionsMapNodeId);
        manifest.Nodes!.Add(map with { Id = "sections-2" });

        Assert.Contains(Validate(manifest).Errors, e => e.Contains("exactly one map node (found 2)"));
    }

    [Fact]
    public void Validate_RejectsMapOverAnythingButSections()
    {
        var manifest = V4Fixtures.Manifest();
        Replace(manifest, WorkflowNodeDefaults.SectionsMapNodeId,
            Node(manifest, WorkflowNodeDefaults.SectionsMapNodeId) with { Over = "input:consult_draft" });

        Assert.Contains(Validate(manifest).Errors, e => e.Contains("'over' must be 'input:sections'"));
    }

    [Fact]
    public void Validate_RejectsBindingTheMapNode()
    {
        var manifest = V4Fixtures.Manifest();
        var node = Node(manifest, WorkflowPromptContract.IdentifyProblem);
        node.Bindings![WorkflowPromptContract.PatientConcepts] =
            new WorkflowBindingValue($"node:{WorkflowNodeDefaults.SectionsMapNodeId}");

        Assert.Contains(Validate(manifest).Errors, e => e.Contains("aggregate output is not bindable"));
    }

    [Fact]
    public void Validate_RejectsSecondUpstreamNodeInMapSteps()
    {
        var manifest = V4Fixtures.Manifest();
        var map = Node(manifest, WorkflowNodeDefaults.SectionsMapNodeId);
        map.Steps![1].Bindings[WorkflowPromptContract.ConsultDraft] =
            new WorkflowBindingValue($"node:{WorkflowPromptContract.ExtractPatientConcepts}", WorkflowConceptRenderers.ConceptContext);

        Assert.Contains(Validate(manifest).Errors, e => e.Contains("at most one upstream node"));
    }

    [Fact]
    public void Validate_RejectsOrphanAndDoublyReferencedPrompts()
    {
        var orphaned = V4Fixtures.Manifest();
        var orphanFiles = V4Fixtures.Files(orphaned);
        orphaned.Prompts!.Add(new WorkflowPromptSpec("unused-prompt", "prompts/unused-prompt.md", new List<string>()));
        orphanFiles["prompts/unused-prompt.md"] = "No variables.";
        Assert.Contains(
            WorkflowPackageValidator.Validate(orphaned, orphanFiles, TestOutputContracts.CatalogSchemas).Errors,
            e => e.Contains("'unused-prompt' is not referenced"));

        var doubled = V4Fixtures.Manifest();
        var problem = Node(doubled, WorkflowPromptContract.IdentifyProblem);
        doubled.Nodes!.Add(problem with { Id = "identify-problem-again" });
        Assert.Contains(Validate(doubled).Errors, e => e.Contains("referenced by more than one node"));
    }

    [Fact]
    public void Validate_AcceptsFreeFormPromptVariablesInV4()
    {
        // The AnalysisPromptIds variable closure lifts in v4: an analysis prompt may
        // declare any variable name as long as bindings cover it.
        var manifest = V4Fixtures.Manifest();
        var files = V4Fixtures.Files(manifest);
        var prompt = manifest.Prompts!.Single(p => p.Id == WorkflowPromptContract.ExtractPatientConcepts);
        prompt.Variables.Clear();
        prompt.Variables.Add("the_referral_letter");
        files["prompts/extract-patient-concepts.md"] = "Extract from:\n{{ the_referral_letter }}";
        var node = Node(manifest, WorkflowPromptContract.ExtractPatientConcepts);
        node.Bindings!.Clear();
        node.Bindings["the_referral_letter"] = new WorkflowBindingValue(WorkflowNodeBindingSources.InputConsultDraft);

        var result = WorkflowPackageValidator.Validate(manifest, files, TestOutputContracts.CatalogSchemas);

        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
    }
}

public class WorkflowNodeDefaultsTests
{
    [Fact]
    public void V3SynthesizedDag_IsTheCanonicalPipeline()
    {
        var nodes = WorkflowNodeDefaults.V3SynthesizedDag(WorkflowSectionStepDefaults.V2Synthesized);

        Assert.Equal(
            new[]
            {
                WorkflowPromptContract.ExtractPatientConcepts,
                WorkflowPromptContract.IdentifyProblem,
                WorkflowPromptContract.CreateTypicalTrajectory,
                WorkflowPromptContract.CreatePatientTrajectory,
                WorkflowNodeDefaults.SectionsMapNodeId
            },
            nodes.Select(n => n.Id));
        Assert.Equal(
            new[]
            {
                "Extracting clinical concepts",
                "Identifying primary problem",
                "Building reference trajectory",
                "Building patient trajectory",
                "Generating sections"
            },
            nodes.Select(n => n.Label));

        // The four compiled failure messages moved verbatim into node data.
        Assert.Equal(
            "The consult could not be processed because clinical concepts could not be extracted from the draft.",
            nodes[0].Output!.FailIfEmpty);
        Assert.Equal("No valid disease or problem concept was identified.", nodes[1].Output!.FailIfEmpty);
        Assert.Equal("No valid typical trajectory concepts were generated.", nodes[2].Output!.FailIfEmpty);
        Assert.Equal("No valid patient trajectory concepts were generated.", nodes[3].Output!.FailIfEmpty);

        // The diamond: patient trajectory consumes all three upstream outputs.
        var trajectory = nodes[3];
        Assert.Equal($"node:{WorkflowPromptContract.IdentifyProblem}", trajectory.Bindings![WorkflowPromptContract.ProblemConcepts].From);
        Assert.Equal($"node:{WorkflowPromptContract.ExtractPatientConcepts}", trajectory.Bindings[WorkflowPromptContract.PatientConcepts].From);
        Assert.Equal($"node:{WorkflowPromptContract.CreateTypicalTrajectory}", trajectory.Bindings[WorkflowPromptContract.TypicalTrajectoryConcepts].From);

        // The map body carries the concept-context rendering of the trajectory node.
        var map = nodes[4];
        Assert.Equal(WorkflowNodeBindingSources.InputSections, map.Over);
        var conceptBinding = map.Steps![0].Bindings[WorkflowPromptContract.PatientTrajectoryConcepts];
        Assert.Equal($"node:{WorkflowPromptContract.CreatePatientTrajectory}", conceptBinding.From);
        Assert.Equal(WorkflowConceptRenderers.ConceptContext, conceptBinding.As);
    }

    [Fact]
    public void LowerMapSteps_InvertsTheSynthesizedMapNode()
    {
        var original = WorkflowSectionStepDefaults.V2Synthesized;
        var map = WorkflowNodeDefaults.V3SynthesizedDag(original).Single(n => n.Kind == WorkflowNodeKinds.Map);

        var lowered = WorkflowNodeDefaults.LowerMapSteps(map);

        Assert.Equal(original.Select(s => (s.StepId, s.Label)), lowered.Select(s => (s.StepId, s.Label)));
        foreach (var (expected, actual) in original.Zip(lowered))
        {
            Assert.Equal(
                expected.Bindings.OrderBy(b => b.Key, StringComparer.Ordinal),
                actual.Bindings.OrderBy(b => b.Key, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void WellKnownConceptSources_CoverTheFourAnalysisNodes()
    {
        Assert.Equal("patient", WorkflowNodeDefaults.WellKnownConceptSources[WorkflowPromptContract.ExtractPatientConcepts]);
        Assert.Equal("problem", WorkflowNodeDefaults.WellKnownConceptSources[WorkflowPromptContract.IdentifyProblem]);
        Assert.Equal("typical-trajectory", WorkflowNodeDefaults.WellKnownConceptSources[WorkflowPromptContract.CreateTypicalTrajectory]);
        Assert.Equal("patient-trajectory", WorkflowNodeDefaults.WellKnownConceptSources[WorkflowPromptContract.CreatePatientTrajectory]);
    }
}

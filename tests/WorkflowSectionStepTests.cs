using System.Text.Json;
using Consultologist.Api.Agents;
using Consultologist.Api.Jobs;
using Consultologist.Api.Models;
using Consultologist.Api.Workflow;

namespace Consultologist.Api.Tests;

public static class V3Fixtures
{
    /// <summary>The v2 fixture package upgraded to specVersion 3 with the canonical step list.</summary>
    public static WorkflowPackageManifest Manifest() => V2Fixtures.Manifest() with
    {
        SpecVersion = 3,
        SectionSteps = WorkflowSectionStepDefaults.V2Synthesized
            .Select(step => step with { Bindings = new Dictionary<string, string>(step.Bindings, StringComparer.Ordinal) })
            .ToList()
    };

    public static Dictionary<string, string> Files(WorkflowPackageManifest manifest) => V2Fixtures.Files(manifest);
}

public class WorkflowSectionStepValidationTests
{
    private static WorkflowPackageValidator.ValidationResult Validate(WorkflowPackageManifest manifest) =>
        WorkflowPackageValidator.Validate(manifest, V3Fixtures.Files(manifest), TestOutputContracts.CatalogSchemas);

    [Fact]
    public void Validate_PassesForWellFormedV3Package()
    {
        var result = Validate(V3Fixtures.Manifest());

        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Validate_RejectsMissingOrEmptySectionSteps()
    {
        Assert.Contains(
            Validate(V3Fixtures.Manifest() with { SectionSteps = null }).Errors,
            e => e.Contains("sectionSteps") && e.Contains("required"));
        Assert.Contains(
            Validate(V3Fixtures.Manifest() with { SectionSteps = new List<WorkflowSectionStepSpec>() }).Errors,
            e => e.Contains("sectionSteps") && e.Contains("required"));
    }

    [Fact]
    public void Validate_RejectsSectionStepsOnV2Package()
    {
        var manifest = V3Fixtures.Manifest() with { SpecVersion = 2 };

        Assert.Contains(Validate(manifest).Errors, e => e.Contains("sectionSteps requires specVersion 3"));
    }

    [Fact]
    public void Validate_RejectsStepReferencingUndeclaredPrompt()
    {
        var manifest = V3Fixtures.Manifest();
        manifest.SectionSteps![0] = manifest.SectionSteps[0] with { Prompt = "no-such-prompt", Id = "step-one" };

        Assert.Contains(Validate(manifest).Errors, e => e.Contains("step-one") && e.Contains("undeclared prompt"));
    }

    [Fact]
    public void Validate_RejectsUnknownBindingSource()
    {
        var manifest = V3Fixtures.Manifest();
        manifest.SectionSteps![0].Bindings[WorkflowPromptContract.SectionName] = "patient_age";

        Assert.Contains(Validate(manifest).Errors, e => e.Contains("unknown source 'patient_age'"));
    }

    [Fact]
    public void Validate_RejectsBindingsNotMatchingDeclaredVariables_BothDirections()
    {
        var missing = V3Fixtures.Manifest();
        missing.SectionSteps![0].Bindings.Remove(WorkflowPromptContract.SectionName);
        Assert.Contains(Validate(missing).Errors, e => e.Contains("must exactly match"));

        var extra = V3Fixtures.Manifest();
        extra.SectionSteps![0].Bindings["undeclared_variable"] = WorkflowStepBindingSources.ConsultDraft;
        Assert.Contains(Validate(extra).Errors, e => e.Contains("must exactly match"));
    }

    [Fact]
    public void Validate_RejectsFirstStepBindingPreviousStepOutput()
    {
        var manifest = V3Fixtures.Manifest();
        manifest.SectionSteps!.RemoveAt(0);
        manifest.Prompts!.RemoveAll(p => p.Id == WorkflowPromptContract.StandardSectionDraft);

        Assert.Contains(
            Validate(manifest).Errors,
            e => e.Contains("first") && e.Contains(WorkflowStepBindingSources.PreviousStepOutput));
    }

    [Fact]
    public void Validate_RejectsDuplicateStepIds_AndAcceptsExplicitIds()
    {
        var duplicated = V3Fixtures.Manifest();
        duplicated.SectionSteps!.Add(duplicated.SectionSteps[^1] with
        {
            Bindings = new Dictionary<string, string>(duplicated.SectionSteps[^1].Bindings, StringComparer.Ordinal)
        });
        Assert.Contains(Validate(duplicated).Errors, e => e.Contains("Duplicate section step id"));

        var disambiguated = V3Fixtures.Manifest();
        disambiguated.SectionSteps!.Add(disambiguated.SectionSteps[^1] with
        {
            Id = "section-instructions-second-pass",
            Bindings = new Dictionary<string, string>(disambiguated.SectionSteps[^1].Bindings, StringComparer.Ordinal)
        });
        var result = Validate(disambiguated);
        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
    }

    [Fact]
    public void Validate_RejectsBlankStepLabel()
    {
        var manifest = V3Fixtures.Manifest();
        manifest.SectionSteps![1] = manifest.SectionSteps[1] with { Label = "  " };

        Assert.Contains(Validate(manifest).Errors, e => e.Contains("no label"));
    }

    [Fact]
    public void Validate_RejectsOrphanProsePrompt()
    {
        var manifest = V3Fixtures.Manifest();
        var files = V3Fixtures.Files(manifest);
        manifest.Prompts!.Add(new WorkflowPromptSpec("unused-prompt", "prompts/unused-prompt.md", new List<string>()));
        files["prompts/unused-prompt.md"] = "No variables.";

        var result = WorkflowPackageValidator.Validate(manifest, files, TestOutputContracts.CatalogSchemas);

        Assert.Contains(result.Errors, e => e.Contains("unused-prompt") && e.Contains("referenced by any section step"));
    }

    [Fact]
    public void Validate_AnalysisPromptsStillRequiredAndClosedInV3()
    {
        var missingAnalysis = V3Fixtures.Manifest();
        missingAnalysis.Prompts!.RemoveAll(p => p.Id == WorkflowPromptContract.IdentifyProblem);
        Assert.Contains(Validate(missingAnalysis).Errors, e => e.Contains("identify-problem") && e.Contains("missing"));

        var badVariable = V3Fixtures.Manifest();
        badVariable.Prompts!.Single(p => p.Id == WorkflowPromptContract.ExtractPatientConcepts)
            .Variables.Add("patient_age");
        Assert.Contains(Validate(badVariable).Errors, e => e.Contains("patient_age") && e.Contains("contract"));
    }

    [Fact]
    public void Validate_AcceptsFreeFormProsePromptAndVariables()
    {
        var manifest = V3Fixtures.Manifest();
        var files = V3Fixtures.Files(manifest);
        manifest.Prompts!.Add(new WorkflowPromptSpec(
            "tighten-prose",
            "prompts/tighten-prose.md",
            new List<string> { "working_draft", "section_title" }));
        manifest.SectionSteps!.Add(new WorkflowSectionStepSpec(
            "tighten-prose",
            "Tightening prose",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["working_draft"] = WorkflowStepBindingSources.PreviousStepOutput,
                ["section_title"] = WorkflowStepBindingSources.SectionName
            }));
        files["prompts/tighten-prose.md"] = "Tighten {{ section_title }}:\n{{ working_draft }}";

        var result = WorkflowPackageValidator.Validate(manifest, files, TestOutputContracts.CatalogSchemas);

        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
    }

    [Fact]
    public void ManifestV3_DeserializesSectionSteps()
    {
        const string json = """
            {
              "name": "general",
              "version": "v2026.08.1",
              "specVersion": 3,
              "sectionSteps": [
                { "prompt": "standard-section-draft", "label": "Drafting section",
                  "bindings": { "section_name": "section_name" } },
                { "id": "second-pass", "prompt": "standard-section-draft", "label": "Second pass",
                  "bindings": { "section_name": "previous_step_output" } }
              ]
            }
            """;

        var manifest = JsonSerializer.Deserialize<WorkflowPackageManifest>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.Equal(2, manifest.SectionSteps!.Count);
        Assert.Equal("standard-section-draft", manifest.SectionSteps[0].StepId);
        Assert.Equal("second-pass", manifest.SectionSteps[1].StepId);
        Assert.Equal("previous_step_output", manifest.SectionSteps[1].Bindings["section_name"]);
    }
}

public class WorkflowSectionStepDefaultsTests
{
    [Fact]
    public void V2Synthesized_IsTheCanonicalThreeStepPipeline()
    {
        var steps = WorkflowSectionStepDefaults.V2Synthesized;

        Assert.Equal(
            new[]
            {
                WorkflowPromptContract.StandardSectionDraft,
                WorkflowPromptContract.PatientSectionDraft,
                WorkflowPromptContract.SectionInstructions
            },
            steps.Select(step => step.StepId));
        Assert.Equal(
            new[] { "Drafting section", "Applying patient information", "Applying section instructions" },
            steps.Select(step => step.Label));
        Assert.Equal(
            WorkflowStepBindingSources.PreviousStepOutput,
            steps[1].Bindings[WorkflowPromptContract.StandardSectionDraftVariable]);
        Assert.Equal(
            WorkflowStepBindingSources.PreviousStepOutput,
            steps[2].Bindings[WorkflowPromptContract.PatientSectionDraftVariable]);
        Assert.DoesNotContain(WorkflowStepBindingSources.PreviousStepOutput, steps[0].Bindings.Values);
    }

    [Fact]
    public void V2Synthesized_ValidatesAgainstTheV2PromptSet()
    {
        // The synthesis must stay consistent with the closed v2 prompt/variable
        // contract; validating the upgraded fixture proves the bindings cover exactly
        // the declared variables.
        var manifest = V3Fixtures.Manifest();

        var result = WorkflowPackageValidator.Validate(manifest, V3Fixtures.Files(manifest), TestOutputContracts.CatalogSchemas);

        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
    }
}

public class ForEachInstanceResolutionTests
{
    // Byte parity with the deleted ProseStepVariableBuilder: a lowered map step's
    // bindings, resolved as a forEach instance, must produce the exact values the
    // prose-step activity used to build.
    private static readonly IReadOnlyList<ClinicalConcept> Concepts = new[]
    {
        new ClinicalConcept("Malignant neoplasm of breast", "disorder", "254837009", true, true, "draft")
    };

    private static readonly IReadOnlyDictionary<string, string> Item = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["id"] = "hpi",
        ["name"] = "History of Present Illness",
        ["standard"] = "Chronological prose."
    };

    private static readonly ConsultNodeDescriptor Trajectory = new(
        "create-patient-trajectory", "Building patient trajectory", OutputContract: "concept-list");

    private static readonly ConsultNodeDescriptor PreviousStep = new(
        "standard-section-draft", "Drafting section", ForEach: "input:sections");

    private static ConsultNodeDescriptor Node(params (string Variable, string From, string? As)[] bindings) => new(
        "patient-section-draft",
        "Applying patient information",
        PromptId: "patient-section-draft",
        Bindings: bindings.ToDictionary(
            b => b.Variable,
            b => new ConsultNodeBindingDescriptor(b.From, b.As),
            StringComparer.Ordinal),
        ForEach: "input:sections");

    private static readonly IReadOnlyDictionary<string, ConsultNodeDescriptor> NodesById =
        new[] { Trajectory, PreviousStep }.ToDictionary(n => n.Id, StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, NodeRunResult> Outputs = new Dictionary<string, NodeRunResult>(StringComparer.Ordinal)
    {
        ["create-patient-trajectory"] = new("{}", Concepts, "in", "out"),
        ["standard-section-draft:hpi"] = new("Previous step prose.", null, "in2", "out2")
    };

    [Fact]
    public void Resolve_MapsEveryLoweredSourceToItsLegacyValue()
    {
        var variables = ConsultNodeVariableResolver.Resolve(
            Node(
                ("draft", "input:consult_draft", null),
                ("name", "item:name", null),
                ("standard", "item:standard", null),
                ("concepts", "node:create-patient-trajectory", "concept-context"),
                ("previous", "node:standard-section-draft", null)),
            "Draft consult text.",
            Item,
            dataScalars: null,
            NodesById,
            Outputs);

        Assert.Equal("Draft consult text.", variables["draft"]);
        Assert.Equal("History of Present Illness", variables["name"]);
        Assert.Equal("Chronological prose.", variables["standard"]);
        Assert.Equal(AgentSectionGenerator.FormatConcepts(Concepts), variables["concepts"]);
        Assert.Equal("Previous step prose.", variables["previous"]);
    }

    [Fact]
    public void Resolve_ItemAlignment_ReadsTheInstancesOwnUpstreamOutput()
    {
        var outputs = new Dictionary<string, NodeRunResult>(Outputs.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal)
        {
            ["standard-section-draft:pmh"] = new("Other section prose.", null, "in3", "out3")
        };
        var pmhItem = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = "pmh", ["name"] = "Past Medical History", ["standard"] = "List."
        };

        var variables = ConsultNodeVariableResolver.Resolve(
            Node(("previous", "node:standard-section-draft", null)),
            "draft", pmhItem, null, NodesById, outputs);

        Assert.Equal("Other section prose.", variables["previous"]);
    }

    [Theory]
    [InlineData("item:title", "which the item does not carry")]
    [InlineData("data:notes", "carries no data scalars")]
    [InlineData("not_a_source", "cannot resolve")]
    public void Resolve_ThrowsOnUnresolvableSources(string from, string expected)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ConsultNodeVariableResolver.Resolve(
            Node(("value", from, null)), "draft", Item, null, NodesById, Outputs));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Resolve_DataScalars_BindByEntryId()
    {
        var variables = ConsultNodeVariableResolver.Resolve(
            Node(("value", "data:clinic-guidelines", null)),
            "draft", Item,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["clinic-guidelines"] = "Local guidance." },
            NodesById, Outputs);

        Assert.Equal("Local guidance.", variables["value"]);
    }
}

public class SectionProseStepEventTests
{
    private static ConsultGenerationJobResponse Response(
        IReadOnlyList<ConsultSectionStepDescriptor>? sectionSteps,
        int completedStepCount,
        int totalStepCount)
    {
        return new ConsultGenerationJobResponse(
            "job-1",
            "user-1",
            ConsultGenerationJobStatuses.Running,
            1,
            0,
            0,
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            false,
            SectionProseProgress: new Dictionary<string, ConsultGenerationSectionProseProgress>
            {
                ["hpi"] = new("hpi", "History of Present Illness", null, completedStepCount, totalStepCount)
            },
            SectionSteps: sectionSteps);
    }

    [Fact]
    public void Candidates_UsePackageStepIdsAndLabels_UnderTheGenericEventName()
    {
        var steps = new[]
        {
            new ConsultSectionStepDescriptor("draft", "Drafting section"),
            new ConsultSectionStepDescriptor("tighten", "Tightening prose")
        };

        var candidates = ConsultGenerationJobs.CreateSemanticEventCandidates(Response(steps, 2, 2))
            .Where(candidate => candidate.EventType == ConsultGenerationSectionProseSteps.EventName)
            .ToList();

        Assert.Equal(2, candidates.Count);
        Assert.Equal("section-prose:hpi:draft", candidates[0].EventKey);
        Assert.Equal("section-prose:hpi:tighten", candidates[1].EventKey);

        var payload = JsonSerializer.Deserialize<ConsultGenerationSectionProseStepEvent>(
            candidates[1].PayloadJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal("tighten", payload.Step);
        Assert.Equal("Tightening prose", payload.Label);
        Assert.Equal("Tightening prose completed.", payload.Message);
        Assert.Equal(2, payload.CompletedStepCount);
        Assert.Equal(2, payload.TotalStepCount);
    }

    [Fact]
    public void Candidates_SkipLegacySnapshotsWithoutStepLists()
    {
        // Pre-milestone-3 snapshots regenerate no prose candidates; their events were
        // materialized while they ran and replay from the event store.
        var candidates = ConsultGenerationJobs.CreateSemanticEventCandidates(
                Response(sectionSteps: null, completedStepCount: 2, totalStepCount: 3))
            .Where(candidate => candidate.EventType == ConsultGenerationSectionProseSteps.EventName)
            .ToList();

        Assert.Empty(candidates);
    }
}

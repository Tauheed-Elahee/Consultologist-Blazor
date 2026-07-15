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

public class ProseStepVariableBuilderTests
{
    private static readonly IReadOnlyList<ClinicalConcept> Concepts = new[]
    {
        new ClinicalConcept("Malignant neoplasm of breast", "disorder", "254837009", true, true, "draft")
    };

    private static ConsultProseStepActivityInput Input(string? previousStepOutput = null) => new(
        "step-id",
        "Draft consult text.",
        Concepts,
        new ConsultGenerationSectionRequest("hpi", "History of Present Illness", "Chronological prose."),
        previousStepOutput,
        "general@v2026.08.1");

    private static WorkflowSectionStepSpec Step(params (string Variable, string Source)[] bindings) => new(
        "prompt-id",
        "Label",
        bindings.ToDictionary(b => b.Variable, b => b.Source, StringComparer.Ordinal),
        "step-id");

    [Fact]
    public void Build_MapsEverySourceToItsInputValue()
    {
        var variables = ProseStepVariableBuilder.Build(
            Step(
                ("draft", WorkflowStepBindingSources.ConsultDraft),
                ("name", WorkflowStepBindingSources.SectionName),
                ("standard", WorkflowStepBindingSources.SectionStandard),
                ("concepts", WorkflowStepBindingSources.PatientTrajectoryConcepts),
                ("previous", WorkflowStepBindingSources.PreviousStepOutput)),
            Input(previousStepOutput: "Previous step prose."));

        Assert.Equal("Draft consult text.", variables["draft"]);
        Assert.Equal("History of Present Illness", variables["name"]);
        Assert.Equal("Chronological prose.", variables["standard"]);
        Assert.Equal(AgentSectionGenerator.FormatConcepts(Concepts), variables["concepts"]);
        Assert.Equal("Previous step prose.", variables["previous"]);
    }

    [Fact]
    public void Build_ThrowsWhenPreviousStepOutputIsBoundButAbsent()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ProseStepVariableBuilder.Build(
            Step(("previous", WorkflowStepBindingSources.PreviousStepOutput)),
            Input(previousStepOutput: null)));

        Assert.Contains("previous_step_output", ex.Message);
    }

    [Fact]
    public void Build_ThrowsOnUnknownSource()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ProseStepVariableBuilder.Build(
            Step(("value", "not_a_source")),
            Input()));

        Assert.Contains("not_a_source", ex.Message);
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

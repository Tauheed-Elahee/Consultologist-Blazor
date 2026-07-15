using System.Reflection;
using System.Text.Json;
using Consultologist.Api.Agents;
using Consultologist.Api.Jobs;
using Consultologist.Api.Models;
using Consultologist.Api.Workflow;
using NSubstitute;

namespace Consultologist.Api.Tests;

/// <summary>
/// The byte-parity seam of the interpreter cutover: resolving variables over the
/// synthesized canonical DAG must produce exactly the dictionaries the four deleted
/// analysis activities built (transcribed verbatim below), and the lowered map steps
/// must feed the prose builder the same values as before.
/// </summary>
public class NodeVariableResolverTests
{
    private const string Draft = "62-year-old woman with newly diagnosed left breast invasive ductal carcinoma.";

    private static readonly IReadOnlyList<ClinicalConcept> PatientConcepts = new[]
    {
        new ClinicalConcept("Malignant neoplasm of breast", "disorder", "254837009", true, true, "patient"),
        new ClinicalConcept("Family support strong", "", "", false, false, "patient")
    };

    private static readonly IReadOnlyList<ClinicalConcept> ProblemConcepts = new[]
    {
        new ClinicalConcept("Malignant neoplasm of breast", "disorder", "254837009", true, true, "problem")
    };

    private static readonly IReadOnlyList<ClinicalConcept> TypicalConcepts = new[]
    {
        new ClinicalConcept("Tamoxifen therapy", "procedure", "75367002", true, true, "typical-trajectory", "adjuvant endocrine therapy")
    };

    private static readonly IReadOnlyList<ClinicalConcept> TrajectoryConcepts = new[]
    {
        new ClinicalConcept("Malignant neoplasm of breast", "disorder", "254837009", true, true, "patient-trajectory")
    };

    private static readonly IReadOnlyList<ConsultNodeDescriptor> Nodes =
        WorkflowNodeDefaults.V3SynthesizedDag(WorkflowSectionStepDefaults.V2Synthesized)
            .Select(Describe)
            .ToList();

    private static readonly IReadOnlyDictionary<string, ConsultNodeDescriptor> NodesById =
        Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, NodeRunResult> Outputs = new Dictionary<string, NodeRunResult>(StringComparer.Ordinal)
    {
        [WorkflowPromptContract.ExtractPatientConcepts] = new("{}", PatientConcepts, "in1", "out1"),
        [WorkflowPromptContract.IdentifyProblem] = new("{}", ProblemConcepts, "in2", "out2"),
        [WorkflowPromptContract.CreateTypicalTrajectory] = new("{}", TypicalConcepts, "in3", "out3"),
        [WorkflowPromptContract.CreatePatientTrajectory] = new("{}", TrajectoryConcepts, "in4", "out4")
    };

    private static ConsultNodeDescriptor Describe(WorkflowNodeSpec node) => new(
        node.Id,
        node.Kind,
        node.Label,
        node.Prompt,
        node.Bindings?.ToDictionary(
            pair => pair.Key,
            pair => new ConsultNodeBindingDescriptor(pair.Value.From, pair.Value.As),
            StringComparer.Ordinal),
        OutputContract: node.Output is null ? null : OutputContracts.ConceptList,
        FailIfEmpty: node.Output?.FailIfEmpty);

    private static Dictionary<string, string> Resolve(string nodeId) =>
        ConsultNodeVariableResolver.Resolve(NodesById[nodeId], Draft, NodesById, Outputs);

    [Fact]
    public void ExtractPatientConcepts_Parity()
    {
        // Deleted activity body: { [ConsultDraft] = input.ConsultDraft }
        Assert.Equal(
            new Dictionary<string, string> { [WorkflowPromptContract.ConsultDraft] = Draft },
            Resolve(WorkflowPromptContract.ExtractPatientConcepts));
    }

    [Fact]
    public void IdentifyProblem_Parity()
    {
        // Deleted activity body: { [PatientConcepts] = ConsultGenerationConceptFormatter.Format(input.PatientConcepts) }
        Assert.Equal(
            new Dictionary<string, string>
            {
                [WorkflowPromptContract.PatientConcepts] = ConsultGenerationConceptFormatter.Format(PatientConcepts)
            },
            Resolve(WorkflowPromptContract.IdentifyProblem));
    }

    [Fact]
    public void CreateTypicalTrajectory_Parity()
    {
        // Deleted activity body: { [ProblemConcepts] = Format(input.ProblemContext) }
        Assert.Equal(
            new Dictionary<string, string>
            {
                [WorkflowPromptContract.ProblemConcepts] = ConsultGenerationConceptFormatter.Format(ProblemConcepts)
            },
            Resolve(WorkflowPromptContract.CreateTypicalTrajectory));
    }

    [Fact]
    public void CreatePatientTrajectory_Parity()
    {
        // Deleted activity body: problem + patient + typical, all analysis-formatted.
        Assert.Equal(
            new Dictionary<string, string>
            {
                [WorkflowPromptContract.ProblemConcepts] = ConsultGenerationConceptFormatter.Format(ProblemConcepts),
                [WorkflowPromptContract.PatientConcepts] = ConsultGenerationConceptFormatter.Format(PatientConcepts),
                [WorkflowPromptContract.TypicalTrajectoryConcepts] = ConsultGenerationConceptFormatter.Format(TypicalConcepts)
            },
            Resolve(WorkflowPromptContract.CreatePatientTrajectory));
    }

    [Fact]
    public void RenderedVariables_StrictRenderAgainstRepoTemplates()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Consultologist.sln")))
        {
            dir = dir.Parent;
        }

        var packageDir = Path.Combine(dir!.FullName, "packages", "general");
        var manifest = JsonSerializer.Deserialize<WorkflowPackageManifest>(
            File.ReadAllText(Path.Combine(packageDir, "manifest.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        foreach (var node in Nodes.Where(n => n.Kind == WorkflowNodeKinds.Prompt))
        {
            var spec = manifest.Prompts!.Single(p => p.Id == node.PromptId);
            var template = new WorkflowPromptTemplate(
                spec.Id,
                File.ReadAllText(Path.Combine(packageDir, spec.File.Replace('/', Path.DirectorySeparatorChar))),
                spec.Variables,
                spec.Prelude is null
                    ? null
                    : File.ReadAllText(Path.Combine(packageDir, manifest.Preludes![spec.Prelude].Replace('/', Path.DirectorySeparatorChar))));

            var rendered = PromptTemplateRenderer.Render(template, Resolve(node.Id));

            Assert.False(string.IsNullOrWhiteSpace(rendered));
        }
    }

    [Fact]
    public void Render_DispatchesToTheBytePinnedFormatters()
    {
        Assert.Equal(
            ConsultGenerationConceptFormatter.Format(PatientConcepts),
            ConsultNodeVariableResolver.Render(WorkflowConceptRenderers.ConceptBullets, PatientConcepts));
        Assert.Equal(
            AgentSectionGenerator.FormatConcepts(TrajectoryConcepts),
            ConsultNodeVariableResolver.Render(WorkflowConceptRenderers.ConceptContext, TrajectoryConcepts));
        Assert.Throws<InvalidOperationException>(() => ConsultNodeVariableResolver.Render("markdown", PatientConcepts));
    }

    [Fact]
    public void LoweredMapSteps_FeedTheProseBuilderTheSameValues()
    {
        var map = WorkflowNodeDefaults.V3SynthesizedDag(WorkflowSectionStepDefaults.V2Synthesized)
            .Single(n => n.Kind == WorkflowNodeKinds.Map);
        var lowered = WorkflowNodeDefaults.LowerMapSteps(map);
        var section = new ConsultGenerationSectionRequest("hpi", "History of Present Illness", "Chronological prose.");

        var input = new ConsultProseStepActivityInput(
            lowered[0].StepId, Draft, TrajectoryConcepts, section, null, "general@v2026.07.5");
        var variables = ProseStepVariableBuilder.Build(
            lowered[0] with { Bindings = lowered[0].Bindings }, input);

        // The R3 pin: the concept-context rendering carries source: patient-trajectory.
        Assert.Equal(AgentSectionGenerator.FormatConcepts(TrajectoryConcepts), variables[WorkflowPromptContract.PatientTrajectoryConcepts]);
        Assert.Contains("source: patient-trajectory", variables[WorkflowPromptContract.PatientTrajectoryConcepts]);
        Assert.Equal(section.Name, variables[WorkflowPromptContract.SectionName]);
    }
}

public class ProvenanceHashTests
{
    [Fact]
    public void Sha256Hex_MatchesKnownVector()
    {
        // echo -n "abc" | sha256sum
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            ConsultGenerationProvenance.Sha256Hex("abc"));
    }
}

public class ConsultGenerationNodeEntityTests
{
    private static readonly PropertyInfo StateProperty = typeof(ConsultGenerationJobEntity)
        .GetProperty("State", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;

    private static (ConsultGenerationJobEntity Entity, Func<ConsultGenerationJobState> State) CreateEntity()
    {
        var entity = new ConsultGenerationJobEntity(Substitute.For<IConsultGenerationJobIndexStore>());
        var state = ConsultGenerationJobState.Create(
            "job-1", "user-1", new[] { new ConsultGenerationSectionRequest("hpi", "History of Present Illness", "std") });
        StateProperty.SetValue(entity, state);
        return (entity, () => (ConsultGenerationJobState)StateProperty.GetValue(entity)!);
    }

    [Fact]
    public void MarkNodeCompleted_RecordsOutputAndCounts()
    {
        var (entity, state) = CreateEntity();

        entity.MarkNodeCompleted(new ConsultGenerationNodeUpdate(
            "extract-patient-concepts", "Extracting clinical concepts",
            new[] { new ClinicalConcept("Term", "disorder", "1", true, true, "patient") },
            "hash-in", "hash-out", 1, 5));

        var node = state().NodeOutputs!["extract-patient-concepts"];
        Assert.Equal(3, state().SchemaVersion);
        Assert.Equal(ConsultGenerationNodeStatuses.Completed, node.Status);
        Assert.Equal("hash-in", node.InputHash);
        Assert.Equal("hash-out", node.OutputHash);
        Assert.Single(node.Concepts!);
        Assert.Equal(1, state().CompletedStageCount);
        Assert.Equal(5, state().TotalStageCount);
        Assert.Contains(state().History, h => h is { Kind: "success", Label: "Extracting clinical concepts" });
    }

    [Fact]
    public void MarkMapNodeStarted_SetsSectionsRunning()
    {
        var (entity, state) = CreateEntity();

        entity.MarkMapNodeStarted(new ConsultGenerationNodeUpdate("sections", "Generating sections", null, null, null, 4, 5));

        Assert.Equal(ConsultGenerationNodeStatuses.Running, state().NodeOutputs!["sections"].Status);
        Assert.All(state().Sections.Values, s => Assert.Equal(ConsultGenerationSectionStatuses.Running, s.Status));
    }

    [Fact]
    public void MarkNodeFailed_RecordsSkippedSetAndFailsJob()
    {
        var (entity, state) = CreateEntity();

        entity.MarkNodeFailed(new ConsultGenerationNodeFailure(
            "identify-problem", "Identifying primary problem",
            "identify-problem-failed", "No valid disease or problem concept was identified.",
            new[]
            {
                new ConsultSectionStepDescriptor("create-typical-trajectory", "Building reference trajectory"),
                new ConsultSectionStepDescriptor("sections", "Generating sections")
            })).GetAwaiter().GetResult();

        var s = state();
        Assert.Equal("identify-problem-failed", s.AnalysisStatus);
        Assert.Equal(ConsultGenerationJobStatuses.Failed, s.Status);
        Assert.Equal(ConsultGenerationNodeStatuses.Failed, s.NodeOutputs!["identify-problem"].Status);
        Assert.Equal(ConsultGenerationNodeStatuses.Skipped, s.NodeOutputs["sections"].Status);
        Assert.Contains(s.History, h => h is { Kind: "skipped", Label: "Building reference trajectory" });
        Assert.Contains(s.History, h => h.Kind == "skipped" && h.Label.Contains("History of Present Illness"));
    }

    [Fact]
    public void FinalizeJob_CompletesTheRunningMapNode_AndCatchesUpTheCounts()
    {
        var (entity, state) = CreateEntity();
        entity.MarkNodeCompleted(new ConsultGenerationNodeUpdate("extract", "Extracting clinical concepts", null, "i", "o", 4, 5));
        entity.MarkMapNodeStarted(new ConsultGenerationNodeUpdate("sections", "Generating sections", null, null, null, 4, 5));

        entity.FinalizeJob(new ConsultGenerationJobFinalize(ConsultGenerationJobStatuses.Completed)).GetAwaiter().GetResult();

        Assert.Equal(ConsultGenerationNodeStatuses.Completed, state().NodeOutputs!["sections"].Status);
        // The map completes here, not through MarkNodeCompleted — the count catches up
        // (completed jobs previously reported 4/5).
        Assert.Equal(state().TotalStageCount, state().CompletedStageCount);
    }
}

public class SchemaVersion2SnapshotToleranceTests
{
    // A trimmed but shape-faithful pre-DAG (SchemaVersion 2) entity state snapshot.
    private const string Fixture = """
        {
          "JobId": "legacy-job",
          "AppUserId": "user-1",
          "Status": "Completed",
          "SchemaVersion": 2,
          "AnalysisStatus": "section-generation-started",
          "CompletedStageCount": 6,
          "TotalStageCount": 6,
          "PatientConcepts": [
            { "Term": "Malignant neoplasm of breast", "Type": "disorder", "Id": "254837009",
              "IsSnomedConcept": true, "IsActive": true, "Source": "patient", "Support": null }
          ],
          "Sections": {
            "hpi": { "Id": "hpi", "Name": "History of Present Illness", "Status": "Completed",
                     "GeneratedText": "Prose.", "ProseStepStatus": "section-instructions",
                     "CompletedProseStepCount": 3, "TotalProseStepCount": 3 }
          },
          "SectionSteps": [
            { "Id": "standard-section-draft", "Label": "Drafting section" },
            { "Id": "patient-section-draft", "Label": "Applying patient information" },
            { "Id": "section-instructions", "Label": "Applying section instructions" }
          ],
          "History": [ { "Kind": "success", "Label": "Concepts extracted", "Detail": null, "OccurredAt": "2026-07-13T13:07:08+00:00" } ],
          "WorkflowPackage": "general@v2026.07.4",
          "EffectiveInputHash": "703f1b53",
          "AgentVersion": "47"
        }
        """;

    [Fact]
    public void LegacySnapshot_DeserializesAndProducesLegacyEvents()
    {
        var state = JsonSerializer.Deserialize<ConsultGenerationJobState>(Fixture)!;

        Assert.Equal(2, state.SchemaVersion);
        Assert.Null(state.NodeOutputs);
        Assert.Null(state.Nodes);
        Assert.Single(state.PatientConcepts);

        var response = state.ToResponse();
        Assert.Null(response.NodeOutputs);

        var candidates = ConsultGenerationJobs.CreateSemanticEventCandidates(response);

        // Legacy snapshots regenerate no stage or node candidates; their events were
        // materialized while they ran and replay from the event store.
        Assert.DoesNotContain(candidates, c => c.EventKey.StartsWith("analysis:"));
        Assert.DoesNotContain(candidates, c => c.EventType == ConsultGenerationNodeEvents.EventName);
        Assert.Contains(candidates, c => c.EventType == "snapshot");
    }
}

public class NodeEventCandidateTests
{
    private static ConsultGenerationJobResponse Response(
        string mapStatus = ConsultGenerationNodeStatuses.Running,
        string? analysisStatus = null,
        string? analysisError = null)
    {
        var nodes = new[]
        {
            new ConsultNodeDescriptor("extract", WorkflowNodeKinds.Prompt, "Extracting clinical concepts"),
            new ConsultNodeDescriptor("sections", WorkflowNodeKinds.Map, "Generating sections")
        };
        var outputs = new Dictionary<string, ConsultGenerationNodeStatusResponse>
        {
            ["extract"] = new("extract", "Extracting clinical concepts", ConsultGenerationNodeStatuses.Completed, "in", "out"),
            ["sections"] = new("sections", "Generating sections", mapStatus)
        };

        return new ConsultGenerationJobResponse(
            "job-1", "user-1", ConsultGenerationJobStatuses.Running, 1, 0, 0,
            new Dictionary<string, string>(), new Dictionary<string, string>(), false,
            SchemaVersion: 3,
            AnalysisStatus: analysisStatus,
            AnalysisError: analysisError,
            Nodes: nodes,
            NodeOutputs: outputs);
    }

    [Fact]
    public void Candidates_EmitNodeEventsInDescriptorOrder()
    {
        var candidates = ConsultGenerationJobs.CreateSemanticEventCandidates(Response())
            .Where(c => c.EventType == ConsultGenerationNodeEvents.EventName)
            .ToList();

        Assert.Equal(new[] { "node:extract", "node:sections" }, candidates.Select(c => c.EventKey));
        Assert.Contains("Extracting clinical concepts completed.", candidates[0].PayloadJson);
        Assert.Contains("Generating sections started.", candidates[1].PayloadJson);
    }

    [Fact]
    public void Candidates_EmitErrorForFailedNodeStatus()
    {
        var candidates = ConsultGenerationJobs.CreateSemanticEventCandidates(
            Response(mapStatus: ConsultGenerationNodeStatuses.Skipped,
                     analysisStatus: "identify-problem-failed",
                     analysisError: "No valid disease or problem concept was identified."));

        Assert.Contains(candidates, c => c.EventType == "error" && c.EventKey == "error:identify-problem-failed");
    }

    [Fact]
    public void Candidates_SkipLegacyStageLoopForNodeSnapshots()
    {
        var candidates = ConsultGenerationJobs.CreateSemanticEventCandidates(Response());

        Assert.DoesNotContain(candidates, c => c.EventKey.StartsWith("analysis:"));
    }
}

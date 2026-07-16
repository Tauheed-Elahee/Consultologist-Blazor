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
/// canonical v5 DAG must produce exactly the dictionaries the deleted pre-DAG
/// analysis activities built (transcribed verbatim below), and forEach instances
/// must resolve the same values the deleted prose-step builder did.
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
        V5Fixtures.Manifest().Nodes!
            .Select(Describe)
            .ToList();

    private static readonly IReadOnlyDictionary<string, ConsultNodeDescriptor> NodesById =
        Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, NodeRunResult> Outputs = new Dictionary<string, NodeRunResult>(StringComparer.Ordinal)
    {
        ["extract-patient-concepts"] = new("{}", PatientConcepts, "in1", "out1"),
        ["identify-problem"] = new("{}", ProblemConcepts, "in2", "out2"),
        ["create-typical-trajectory"] = new("{}", TypicalConcepts, "in3", "out3"),
        ["create-patient-trajectory"] = new("{}", TrajectoryConcepts, "in4", "out4")
    };

    private static ConsultNodeDescriptor Describe(WorkflowNodeSpec node) => new(
        node.Id,
        node.Label,
        node.Prompt,
        node.Bindings?.ToDictionary(
            pair => pair.Key,
            pair => new ConsultNodeBindingDescriptor(pair.Value.From, pair.Value.As),
            StringComparer.Ordinal),
        OutputContract: node.Output is null ? null : OutputContracts.ConceptList,
        FailIfEmpty: node.Output?.FailIfEmpty,
        ForEach: node.ForEach);

    private static Dictionary<string, string> Resolve(string nodeId) =>
        ConsultNodeVariableResolver.Resolve(NodesById[nodeId], Draft, null, null, NodesById, Outputs);

    [Fact]
    public void ExtractPatientConcepts_Parity()
    {
        // Deleted activity body: { [ConsultDraft] = input.ConsultDraft }
        Assert.Equal(
            new Dictionary<string, string> { ["consult_draft"] = Draft },
            Resolve("extract-patient-concepts"));
    }

    [Fact]
    public void IdentifyProblem_Parity()
    {
        // Deleted activity body: { [PatientConcepts] = ConsultGenerationConceptFormatter.Format(input.PatientConcepts) }
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["patient_concepts"] = ConsultGenerationConceptFormatter.Format(PatientConcepts)
            },
            Resolve("identify-problem"));
    }

    [Fact]
    public void CreateTypicalTrajectory_Parity()
    {
        // Deleted activity body: { [ProblemConcepts] = Format(input.ProblemContext) }
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["problem_concepts"] = ConsultGenerationConceptFormatter.Format(ProblemConcepts)
            },
            Resolve("create-typical-trajectory"));
    }

    [Fact]
    public void CreatePatientTrajectory_Parity()
    {
        // Deleted activity body: problem + patient + typical, all analysis-formatted.
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["problem_concepts"] = ConsultGenerationConceptFormatter.Format(ProblemConcepts),
                ["patient_concepts"] = ConsultGenerationConceptFormatter.Format(PatientConcepts),
                ["typical_trajectory_concepts"] = ConsultGenerationConceptFormatter.Format(TypicalConcepts)
            },
            Resolve("create-patient-trajectory"));
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

        foreach (var node in Nodes.Where(n => n.ForEach is null))
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
    public void LoweredForEachChain_ResolvesTheSameValuesTheProseBuilderDid()
    {
        var firstStep = Nodes.First(n => n.ForEach != null);
        var item = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = "hpi", ["name"] = "History of Present Illness", ["standard"] = "Chronological prose."
        };

        var variables = ConsultNodeVariableResolver.Resolve(firstStep, Draft, item, null, NodesById, Outputs);

        // The R3 pin: the concept-context rendering carries source: patient-trajectory.
        Assert.Equal(AgentSectionGenerator.FormatConcepts(TrajectoryConcepts), variables["patient_trajectory_concepts"]);
        Assert.Contains("source: patient-trajectory", variables["patient_trajectory_concepts"]);
        Assert.Equal("History of Present Illness", variables["section_name"]);
    }
}

public class ConsultNodeSchedulerTests
{
    private static readonly ConsultNodeDescriptor Trajectory = new(
        "create-patient-trajectory", "Building patient trajectory", OutputContract: "concept-list");

    private static readonly ConsultNodeDescriptor Step1 = new(
        "standard-section-draft", "Drafting section",
        Bindings: new Dictionary<string, ConsultNodeBindingDescriptor>
        {
            ["patient_trajectory_concepts"] = new("node:create-patient-trajectory", "concept-context")
        },
        ForEach: "input:sections");

    private static readonly ConsultNodeDescriptor Step2 = new(
        "patient-section-draft", "Applying patient information",
        Bindings: new Dictionary<string, ConsultNodeBindingDescriptor>
        {
            ["standard_section_draft"] = new("node:standard-section-draft")
        },
        ForEach: "input:sections");

    private static readonly IReadOnlyDictionary<string, ConsultNodeDescriptor> NodesById =
        new[] { Trajectory, Step1, Step2 }.ToDictionary(n => n.Id, StringComparer.Ordinal);

    private static Dictionary<string, NodeRunResult> Outputs(params string[] keys) =>
        keys.ToDictionary(k => k, _ => new NodeRunResult("x", null, "i", "o"), StringComparer.Ordinal);

    [Fact]
    public void InstanceKeys_ScalarById_InstancesComposite()
    {
        Assert.Equal("extract", ConsultNodeScheduler.InstanceKey("extract", null));
        Assert.Equal("standard-section-draft:hpi", ConsultNodeScheduler.InstanceKey("standard-section-draft", "hpi"));
    }

    [Fact]
    public void ForEachInstance_WaitsOnBroadcastScalarDependencies()
    {
        Assert.False(ConsultNodeScheduler.InstanceReady(Step1, "hpi", NodesById, Outputs()));
        Assert.True(ConsultNodeScheduler.InstanceReady(Step1, "hpi", NodesById, Outputs("create-patient-trajectory")));
    }

    [Fact]
    public void ItemAlignment_UnlocksPerItem_NotPerWave()
    {
        // The conservatism removal: section hpi's second step is ready the moment
        // ITS first step completes — section pmh's first step is still pending.
        var outputs = Outputs("create-patient-trajectory", "standard-section-draft:hpi");

        Assert.True(ConsultNodeScheduler.InstanceReady(Step2, "hpi", NodesById, outputs));
        Assert.False(ConsultNodeScheduler.InstanceReady(Step2, "pmh", NodesById, outputs));
    }

    [Fact]
    public void NodeDependencies_ReadTheNodeEdges()
    {
        Assert.Equal(new[] { "standard-section-draft" }, ConsultNodeScheduler.NodeDependencies(Step2));
        Assert.Empty(ConsultNodeScheduler.NodeDependencies(Trajectory));
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

    [Fact]
    public void DraftOnlyHash_PinsTheCanonicalShape()
    {
        var request = new ConsultGenerationRequest("Draft text.");

        // Canonical shape pin: {"consultDraft":"Draft text."} — the definition jobs
        // record as effectiveInputHashVersion 2.
        Assert.Equal(
            ConsultGenerationProvenance.Sha256Hex("""{"consultDraft":"Draft text."}"""),
            ConsultGenerationProvenance.ComputeDraftOnlyHash(request));
    }
}

public class StartRequestValidationTests
{
    [Fact]
    public void ValidateRequest_RequiresBodyAndDraft()
    {
        Assert.Equal("Request body is required.", ConsultGenerationJobs.ValidateRequest(null));
        Assert.Equal("ConsultDraft is required.", ConsultGenerationJobs.ValidateRequest(new ConsultGenerationRequest(" ")));
        Assert.Null(ConsultGenerationJobs.ValidateRequest(new ConsultGenerationRequest("Draft.")));
    }
}

public class ConsultGenerationNodeEntityTests
{
    private static readonly PropertyInfo StateProperty = typeof(ConsultGenerationJobEntity)
        .GetProperty("State", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;

    private static IReadOnlyDictionary<string, string> Item(string id, string name) =>
        new Dictionary<string, string>(StringComparer.Ordinal) { ["id"] = id, ["name"] = name, ["content"] = "std" };

    private static (ConsultGenerationJobEntity Entity, Func<ConsultGenerationJobState> State) CreateEntity()
    {
        var entity = new ConsultGenerationJobEntity(Substitute.For<IConsultGenerationJobIndexStore>());
        var state = ConsultGenerationJobState.Create(
            "job-1", "user-1", new[] { Item("hpi", "History of Present Illness") });
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
        Assert.Equal(5, state().SchemaVersion);
        Assert.Equal(ConsultGenerationNodeStatuses.Completed, node.Status);
        Assert.Equal("hash-in", node.InputHash);
        Assert.Equal("hash-out", node.OutputHash);
        Assert.Single(node.Concepts!);
        Assert.Equal(1, state().CompletedStageCount);
        Assert.Equal(5, state().TotalStageCount);
        Assert.Contains(state().History, h => h is { Kind: "success", Label: "Extracting clinical concepts" });
    }

    [Fact]
    public void MarkNodeItemCompleted_RecordsPerItemProvenanceAndSectionProgress()
    {
        var (entity, state) = CreateEntity();

        entity.MarkNodeItemCompleted(new ConsultGenerationNodeItemUpdate(
            "standard-section-draft", "Drafting section", "hpi", "History of Present Illness",
            null, "hash-in", "hash-out", 1, 3));

        var s = state();
        Assert.Equal(5, s.SchemaVersion);
        var output = s.NodeOutputs!["standard-section-draft:hpi"];
        Assert.Equal("standard-section-draft", output.NodeId);
        Assert.Equal("hpi", output.ItemId);
        Assert.Equal(ConsultGenerationNodeStatuses.Completed, output.Status);
        Assert.Equal("hash-in", output.InputHash);
        Assert.Equal("hash-out", output.OutputHash);

        var section = s.Sections["hpi"];
        Assert.Equal(ConsultGenerationSectionStatuses.Running, section.Status);
        Assert.Equal("standard-section-draft", section.ProseStepStatus);
        Assert.Equal(1, section.CompletedProseStepCount);
        Assert.Equal(3, section.TotalProseStepCount);

        // The per-item entry surfaces on the response under its composite key.
        var response = s.ToResponse();
        Assert.Equal("hash-in", response.NodeOutputs!["standard-section-draft:hpi"].InputHash);
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
    public void Initialize_RecordsAgentVersionsWriteOnce_AndSurfacesThemOnTheResponse()
    {
        var (entity, state) = CreateEntity();
        var items = new[] { Item("hpi", "History of Present Illness") };

        entity.Initialize(new ConsultGenerationJobInitialize(
            "job-1", "user-1", items,
            AgentVersions: new Dictionary<string, string> { ["text"] = "47", ["concept-list"] = "1" })).GetAwaiter().GetResult();

        // Write-once: a second Initialize (Durable replay) must not overwrite.
        entity.Initialize(new ConsultGenerationJobInitialize(
            "job-1", "user-1", items,
            AgentVersions: new Dictionary<string, string> { ["text"] = "99" })).GetAwaiter().GetResult();

        Assert.Equal("47", state().AgentVersions!["text"]);
        Assert.Equal("1", state().AgentVersions!["concept-list"]);

        var response = state().ToResponse();
        Assert.Equal("47", response.AgentVersions!["text"]);
        Assert.Equal("1", response.AgentVersions!["concept-list"]);
    }

    [Fact]
    public void FinalizeJob_CompletesLingeringRunningNodes_AndCatchesUpTheCounts()
    {
        var (entity, state) = CreateEntity();
        entity.MarkNodeCompleted(new ConsultGenerationNodeUpdate("extract", "Extracting clinical concepts", null, "i", "o", 4, 5));
        // A node left Running (legacy in-flight tolerance) completes at finalize.
        state().GetOrAddNodeOutput("sections", "Generating sections").Status = ConsultGenerationNodeStatuses.Running;

        entity.FinalizeJob(new ConsultGenerationJobFinalize(ConsultGenerationJobStatuses.Completed)).GetAwaiter().GetResult();

        Assert.Equal(ConsultGenerationNodeStatuses.Completed, state().NodeOutputs!["sections"].Status);
        Assert.Equal(state().TotalStageCount, state().CompletedStageCount);
    }
}

public class LegacySnapshotToleranceTests
{
    // A trimmed pre-rebase entity snapshot carrying fields the v5-only purge deleted
    // (pre-DAG concept lists, scalar agent versions). Old job records must keep
    // deserializing — unknown JSON fields are ignored — and ToResponse must not throw.
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
          "History": [ { "Kind": "success", "Label": "Concepts extracted", "Detail": null, "OccurredAt": "2026-07-13T13:07:08+00:00" } ],
          "WorkflowPackage": "general@v2026.07.4",
          "EffectiveInputHash": "703f1b53",
          "AgentVersion": "47",
          "ConceptAgentVersion": "1"
        }
        """;

    [Fact]
    public void LegacySnapshot_DeserializesAndToResponseDoesNotThrow()
    {
        var state = JsonSerializer.Deserialize<ConsultGenerationJobState>(Fixture)!;

        Assert.Equal(2, state.SchemaVersion);
        Assert.Null(state.NodeOutputs);
        Assert.Null(state.Nodes);

        var response = state.ToResponse();

        Assert.Equal("Prose.", response.GeneratedSections["hpi"]);
        Assert.Null(response.NodeOutputs);
        Assert.Contains(
            ConsultGenerationJobs.CreateSemanticEventCandidates(response),
            c => c.EventType == "snapshot");
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
            new ConsultNodeDescriptor("extract", "Extracting clinical concepts"),
            new ConsultNodeDescriptor("sections", "Generating sections", ForEach: "data:standards")
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
    public void Candidates_EmitOnlyCompletedNodes_InDescriptorOrder()
    {
        // A running forEach node emits nothing at the node level — its per-item
        // progress rides the section-prose-step events instead.
        var running = ConsultGenerationJobs.CreateSemanticEventCandidates(Response())
            .Where(c => c.EventType == ConsultGenerationNodeEvents.EventName)
            .ToList();

        Assert.Equal(new[] { "node:extract" }, running.Select(c => c.EventKey));
        Assert.Contains("Extracting clinical concepts completed.", running[0].PayloadJson);

        var completed = ConsultGenerationJobs.CreateSemanticEventCandidates(
                Response(mapStatus: ConsultGenerationNodeStatuses.Completed))
            .Where(c => c.EventType == ConsultGenerationNodeEvents.EventName)
            .ToList();

        Assert.Equal(new[] { "node:extract", "node:sections" }, completed.Select(c => c.EventKey));
        Assert.Contains("Generating sections completed.", completed[1].PayloadJson);
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

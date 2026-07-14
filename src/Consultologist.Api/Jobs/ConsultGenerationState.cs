using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Net.ServerSentEvents;
using Consultologist.Api.Agents;
using Consultologist.Api.Auth;
using Consultologist.Api.Models;
using Consultologist.Api.Workflow;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Entities;
using Microsoft.Extensions.Logging;

namespace Consultologist.Api.Jobs;

public sealed class ConsultGenerationJobEntity : TaskEntity<ConsultGenerationJobState>
{
    private readonly IConsultGenerationJobIndexStore _indexStore;

    public ConsultGenerationJobEntity(IConsultGenerationJobIndexStore indexStore)
    {
        _indexStore = indexStore;
    }

    public async Task Initialize(ConsultGenerationJobInitialize input)
    {
        if (State == null || State.Sections.Count == 0)
        {
            State = ConsultGenerationJobState.Create(input.JobId, input.AppUserId, input.Sections);
        }
        else
        {
            State.JobId = string.IsNullOrWhiteSpace(State.JobId) ? input.JobId : State.JobId;
            State.AppUserId = string.IsNullOrWhiteSpace(State.AppUserId) ? input.AppUserId : State.AppUserId;

            if (State.CreatedAtUtc == default)
            {
                State.CreatedAtUtc = DateTimeOffset.UtcNow;
            }

            if (State.TotalSectionCount == 0)
            {
                State.TotalSectionCount = input.Sections.Count;
            }

            foreach (var section in input.Sections)
            {
                State.GetOrAddSection(section.Id, section.Name);
            }
        }

        State.WorkflowPackage ??= input.WorkflowPackage;
        State.EffectiveInputHash ??= input.EffectiveInputHash;
        State.AgentVersion ??= input.AgentVersion;
        State.SectionSteps ??= input.SectionSteps?.ToList();
        State.ConceptAgentVersion ??= input.ConceptAgentVersion;
        State.Nodes ??= input.Nodes?.ToList();

        await _indexStore.UpsertAsync(State.ToIndexEntry(), CancellationToken.None);
    }

    public async Task MarkRunning()
    {
        var state = EnsureState();
        state.Status = ConsultGenerationJobStatuses.Running;
        state.StartedAtUtc ??= DateTimeOffset.UtcNow;
        State = state;

        await _indexStore.UpsertAsync(state.ToIndexEntry(), CancellationToken.None);
    }

    public void MarkAnalysisStage(ConsultGenerationAnalysisUpdate input)
    {
        var state = EnsureState();
        ApplyAnalysisUpdate(state, input);
        State = state;
    }

    public void MarkSectionGenerationStarted(ConsultGenerationAnalysisUpdate input)
    {
        var state = EnsureState();
        ApplyAnalysisUpdate(state, input);

        foreach (var section in state.Sections.Values.Where(section => section.Status == ConsultGenerationSectionStatuses.Pending))
        {
            section.Status = ConsultGenerationSectionStatuses.Running;
        }

        State = state;
    }

    public async Task MarkAnalysisFailed(ConsultGenerationAnalysisUpdate input)
    {
        var state = EnsureState();
        var completedBeforeFailure = state.CompletedStageCount;
        ApplyAnalysisUpdate(state, input);

        for (var i = completedBeforeFailure + 1; i < ConsultGenerationAnalysisStatuses.OrderedStages.Length; i++)
        {
            state.History.Add(new JobHistoryEvent(
                "skipped",
                GetStageHistoryLabel(ConsultGenerationAnalysisStatuses.OrderedStages[i]),
                null,
                DateTimeOffset.UtcNow));
        }

        foreach (var section in state.Sections.Values.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            state.History.Add(new JobHistoryEvent("skipped", $"Section not reached: {section.Name}", null, DateTimeOffset.UtcNow));
        }

        state.Status = ConsultGenerationJobStatuses.Failed;
        state.CompletedAtUtc = DateTimeOffset.UtcNow;
        State = state;

        await _indexStore.UpsertAsync(state.ToIndexEntry(), CancellationToken.None);
    }

    public async Task CompleteSection(SectionGenerationResult result)
    {
        var state = EnsureState();
        var section = state.GetOrAddSection(result.SectionId, result.SectionName);
        section.Status = ConsultGenerationSectionStatuses.Completed;
        section.GeneratedText = result.GeneratedText ?? string.Empty;
        section.Error = null;
        section.CompletedAtUtc = DateTimeOffset.UtcNow;
        state.History.Add(new JobHistoryEvent("success", $"Section completed: {result.SectionName}", null, DateTimeOffset.UtcNow));
        State = state;

        await _indexStore.UpsertAsync(state.ToIndexEntry(), CancellationToken.None);
    }

    public async Task FailSection(SectionGenerationResult result)
    {
        var state = EnsureState();
        var section = state.GetOrAddSection(result.SectionId, result.SectionName);
        section.Status = ConsultGenerationSectionStatuses.Failed;
        section.GeneratedText = null;
        section.Error = string.IsNullOrWhiteSpace(result.Error) ? "Section generation failed." : result.Error;
        section.CompletedAtUtc = DateTimeOffset.UtcNow;
        state.History.Add(new JobHistoryEvent("failure", $"Section failed: {result.SectionName}", section.Error, DateTimeOffset.UtcNow));
        State = state;

        await _indexStore.UpsertAsync(state.ToIndexEntry(), CancellationToken.None);
    }

    public void MarkSectionProseStep(ConsultGenerationSectionProseStepUpdate input)
    {
        var state = EnsureState();
        var section = state.GetOrAddSection(input.SectionId, input.SectionName);
        section.ProseStepStatus = input.ProseStepStatus;
        section.CompletedProseStepCount = input.CompletedProseStepCount;
        section.TotalProseStepCount = state.SectionSteps?.Count ?? ConsultGenerationSectionProseSteps.TotalStepCount;
        State = state;
    }

    public void MarkNodeCompleted(ConsultGenerationNodeUpdate input)
    {
        var state = EnsureState();
        state.SchemaVersion = 3;

        var node = state.GetOrAddNodeOutput(input.NodeId, input.Label);
        node.Status = ConsultGenerationNodeStatuses.Completed;
        node.Concepts = input.Concepts?.ToList();
        node.InputHash = input.InputHash;
        node.OutputHash = input.OutputHash;
        node.CompletedAtUtc = DateTimeOffset.UtcNow;

        state.CompletedStageCount = input.CompletedNodeCount;
        state.TotalStageCount = input.TotalNodeCount;
        state.History.Add(new JobHistoryEvent("success", input.Label, null, DateTimeOffset.UtcNow));
        State = state;
    }

    public void MarkMapNodeStarted(ConsultGenerationNodeUpdate input)
    {
        var state = EnsureState();
        state.SchemaVersion = 3;

        var node = state.GetOrAddNodeOutput(input.NodeId, input.Label);
        node.Status = ConsultGenerationNodeStatuses.Running;

        foreach (var section in state.Sections.Values.Where(section => section.Status == ConsultGenerationSectionStatuses.Pending))
        {
            section.Status = ConsultGenerationSectionStatuses.Running;
        }

        state.History.Add(new JobHistoryEvent("success", input.Label, null, DateTimeOffset.UtcNow));
        State = state;
    }

    public async Task MarkNodeFailed(ConsultGenerationNodeFailure input)
    {
        var state = EnsureState();
        state.SchemaVersion = 3;

        var node = state.GetOrAddNodeOutput(input.NodeId, input.Label);
        node.Status = ConsultGenerationNodeStatuses.Failed;
        node.Error = input.Error;
        node.CompletedAtUtc = DateTimeOffset.UtcNow;

        state.AnalysisStatus = input.Status;
        state.AnalysisError = input.Error;
        state.History.Add(new JobHistoryEvent("failure", input.Label, input.Error, DateTimeOffset.UtcNow));

        // The orchestrator holds the graph, so it computes the unreached set; skipped
        // nodes get entries and Skipped status here.
        foreach (var skipped in input.SkippedNodes)
        {
            var skippedNode = state.GetOrAddNodeOutput(skipped.Id, skipped.Label);
            skippedNode.Status = ConsultGenerationNodeStatuses.Skipped;
            state.History.Add(new JobHistoryEvent("skipped", skipped.Label, null, DateTimeOffset.UtcNow));
        }

        foreach (var section in state.Sections.Values.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            state.History.Add(new JobHistoryEvent("skipped", $"Section not reached: {section.Name}", null, DateTimeOffset.UtcNow));
        }

        state.Status = ConsultGenerationJobStatuses.Failed;
        state.CompletedAtUtc = DateTimeOffset.UtcNow;
        State = state;

        await _indexStore.UpsertAsync(state.ToIndexEntry(), CancellationToken.None);
    }

    public async Task FinalizeJob(ConsultGenerationJobFinalize input)
    {
        var state = EnsureState();
        state.Status = input.Status;
        state.CompletedAtUtc = DateTimeOffset.UtcNow;

        foreach (var node in (state.NodeOutputs?.Values ?? Enumerable.Empty<ConsultNodeOutputState>())
            .Where(node => node.Status == ConsultGenerationNodeStatuses.Running))
        {
            node.Status = ConsultGenerationNodeStatuses.Completed;
            node.CompletedAtUtc = DateTimeOffset.UtcNow;
        }

        if (input.Status == ConsultGenerationJobStatuses.Completed)
        {
            state.History.Add(new JobHistoryEvent("success", "Done", null, DateTimeOffset.UtcNow));
        }
        else if (input.Status == ConsultGenerationJobStatuses.Failed)
        {
            state.FailureError = input.Error;
            state.History.Add(new JobHistoryEvent("failure", "Failed", input.Error, DateTimeOffset.UtcNow));

            var finishedIds = state.Sections.Values
                .Where(s => s.Status is ConsultGenerationSectionStatuses.Completed or ConsultGenerationSectionStatuses.Failed)
                .Select(s => s.Id)
                .ToHashSet();

            foreach (var section in state.Sections.Values
                .Where(s => !finishedIds.Contains(s.Id))
                .OrderBy(s => s.Id, StringComparer.Ordinal))
            {
                state.History.Add(new JobHistoryEvent("skipped", $"Section not reached: {section.Name}", null, DateTimeOffset.UtcNow));
            }
        }

        State = state;

        await _indexStore.UpsertAsync(state.ToIndexEntry(), CancellationToken.None);
    }

    [Function(nameof(ConsultGenerationJobEntity))]
    public static Task RunEntityAsync([EntityTrigger] TaskEntityDispatcher dispatcher)
    {
        return dispatcher.DispatchAsync<ConsultGenerationJobEntity>();
    }

    private ConsultGenerationJobState EnsureState()
    {
        return State ?? ConsultGenerationJobState.Create(string.Empty, string.Empty, Array.Empty<ConsultGenerationSectionRequest>());
    }

    private static void ApplyAnalysisUpdate(ConsultGenerationJobState state, ConsultGenerationAnalysisUpdate input)
    {
        state.SchemaVersion = 2;
        state.AnalysisStatus = input.AnalysisStatus;
        state.AnalysisError = input.AnalysisError;
        state.CompletedStageCount = input.CompletedStageCount;
        state.TotalStageCount = ConsultGenerationAnalysisStatuses.TotalStageCount;

        if (input.PatientConcepts != null)
        {
            state.PatientConcepts = input.PatientConcepts.ToList();
        }

        if (input.ProblemContext != null)
        {
            state.ProblemContext = input.ProblemContext.ToList();
        }

        if (input.TypicalTrajectoryConcepts != null)
        {
            state.TypicalTrajectoryConcepts = input.TypicalTrajectoryConcepts.ToList();
        }

        if (input.PatientTrajectoryConcepts != null)
        {
            state.PatientTrajectoryConcepts = input.PatientTrajectoryConcepts.ToList();
        }

        if (input.ValidationWarnings != null)
        {
            state.ValidationWarnings.AddRange(input.ValidationWarnings);
        }

        var isFailure = input.AnalysisStatus.EndsWith("-failed", StringComparison.Ordinal);
        state.History.Add(new JobHistoryEvent(
            isFailure ? "failure" : "success",
            GetStageHistoryLabel(input.AnalysisStatus),
            isFailure ? input.AnalysisError : null,
            DateTimeOffset.UtcNow));
    }

    private static string GetStageHistoryLabel(string stage) => stage switch
    {
        ConsultGenerationAnalysisStatuses.AnalysisStarted => "Analysis started",
        ConsultGenerationAnalysisStatuses.ConceptsExtracted => "Concepts extracted",
        ConsultGenerationAnalysisStatuses.ConceptExtractionFailed => "Concept extraction failed",
        ConsultGenerationAnalysisStatuses.ProblemIdentified => "Primary problem identified",
        ConsultGenerationAnalysisStatuses.ProblemIdentificationFailed => "Problem identification failed",
        ConsultGenerationAnalysisStatuses.TypicalTrajectoryCreated => "Reference trajectory created",
        ConsultGenerationAnalysisStatuses.TypicalTrajectoryFailed => "Reference trajectory failed",
        ConsultGenerationAnalysisStatuses.PatientTrajectoryCreated => "Patient trajectory created",
        ConsultGenerationAnalysisStatuses.PatientTrajectoryFailed => "Patient trajectory failed",
        ConsultGenerationAnalysisStatuses.SectionGenerationStarted => "Section generation started",
        _ => stage
    };
}

public sealed record ConsultGenerationOrchestrationInput(
    ConsultGenerationRequest Request,
    string AppUserId,
    string? WorkflowPackage = null,
    string? EffectiveInputHash = null,
    string? AgentVersion = null,
    IReadOnlyList<ConsultSectionStepDescriptor>? SectionSteps = null,
    string? ConceptAgentVersion = null,
    IReadOnlyList<ConsultNodeDescriptor>? Nodes = null);

public sealed record ConsultGenerationJobInitialize(
    string JobId,
    string AppUserId,
    IReadOnlyList<ConsultGenerationSectionRequest> Sections,
    string? WorkflowPackage = null,
    string? EffectiveInputHash = null,
    string? AgentVersion = null,
    IReadOnlyList<ConsultSectionStepDescriptor>? SectionSteps = null,
    string? ConceptAgentVersion = null,
    IReadOnlyList<ConsultNodeDescriptor>? Nodes = null);

public sealed record ConsultGenerationNodeUpdate(
    string NodeId,
    string Label,
    IReadOnlyList<ClinicalConcept>? Concepts,
    string? InputHash,
    string? OutputHash,
    int CompletedNodeCount,
    int TotalNodeCount);

public sealed record ConsultGenerationNodeFailure(
    string NodeId,
    string Label,
    string Status,
    string Error,
    IReadOnlyList<ConsultSectionStepDescriptor> SkippedNodes);

/// <summary>
/// Input of the generic prose-step activity. Carries the step id rather than the step
/// definition: the workflow package ref is pinned to an immutable version at job start,
/// so re-resolving it inside the activity is deterministic.
/// </summary>
public sealed record ConsultProseStepActivityInput(
    string StepId,
    string ConsultDraft,
    IReadOnlyList<ClinicalConcept> PatientTrajectoryConcepts,
    ConsultGenerationSectionRequest Section,
    string? PreviousStepOutput = null,
    string? WorkflowPackage = null);

public sealed record ConsultGenerationJobFinalize(string Status, string? Error = null);

public sealed record ConsultGenerationSectionProseStepUpdate(
    string SectionId,
    string SectionName,
    string ProseStepStatus,
    int CompletedProseStepCount);

public sealed record ConsultGenerationAnalysisUpdate(
    string AnalysisStatus,
    int CompletedStageCount,
    string? AnalysisError,
    IReadOnlyList<ClinicalConcept>? PatientConcepts,
    IReadOnlyList<ClinicalConcept>? ProblemContext,
    IReadOnlyList<ClinicalConcept>? TypicalTrajectoryConcepts,
    IReadOnlyList<ClinicalConcept>? PatientTrajectoryConcepts,
    IReadOnlyList<ConsultGenerationValidationWarning>? ValidationWarnings)
{
    public static ConsultGenerationAnalysisUpdate Stage(
        string analysisStatus,
        int completedStageCount,
        IReadOnlyList<ClinicalConcept>? patientConcepts = null,
        IReadOnlyList<ClinicalConcept>? problemContext = null,
        IReadOnlyList<ClinicalConcept>? typicalTrajectoryConcepts = null,
        IReadOnlyList<ClinicalConcept>? patientTrajectoryConcepts = null,
        IReadOnlyList<ConsultGenerationValidationWarning>? validationWarnings = null)
    {
        return new ConsultGenerationAnalysisUpdate(
            analysisStatus,
            completedStageCount,
            null,
            patientConcepts,
            problemContext,
            typicalTrajectoryConcepts,
            patientTrajectoryConcepts,
            validationWarnings);
    }

    public static ConsultGenerationAnalysisUpdate Failure(string analysisStatus, string analysisError)
    {
        return new ConsultGenerationAnalysisUpdate(
            analysisStatus,
            0,
            analysisError,
            null,
            null,
            null,
            null,
            null);
    }
}

public sealed class ConsultGenerationJobState
{
    public string JobId { get; set; } = string.Empty;
    public string AppUserId { get; set; } = string.Empty;
    public string Status { get; set; } = ConsultGenerationJobStatuses.Queued;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public int TotalSectionCount { get; set; }
    public int SchemaVersion { get; set; } = 2;
    public string? AnalysisStatus { get; set; }
    public string? AnalysisError { get; set; }
    public List<ClinicalConcept> PatientConcepts { get; set; } = new();
    public List<ClinicalConcept> ProblemContext { get; set; } = new();
    public List<ClinicalConcept> TypicalTrajectoryConcepts { get; set; } = new();
    public List<ClinicalConcept> PatientTrajectoryConcepts { get; set; } = new();
    public int CompletedStageCount { get; set; }
    public int TotalStageCount { get; set; } = ConsultGenerationAnalysisStatuses.TotalStageCount;
    public List<ConsultGenerationValidationWarning> ValidationWarnings { get; set; } = new();
    public Dictionary<string, ConsultGenerationSectionState> Sections { get; set; } = new();
    public List<JobHistoryEvent> History { get; set; } = new();
    public string? FailureError { get; set; }
    public string? WorkflowPackage { get; set; }
    public string? EffectiveInputHash { get; set; }
    public string? AgentVersion { get; set; }
    public string? ConceptAgentVersion { get; set; }
    public List<ConsultSectionStepDescriptor>? SectionSteps { get; set; }
    public List<ConsultNodeDescriptor>? Nodes { get; set; }
    public Dictionary<string, ConsultNodeOutputState>? NodeOutputs { get; set; }

    public static ConsultGenerationJobState Create(
        string jobId,
        string appUserId,
        IReadOnlyList<ConsultGenerationSectionRequest> sections)
    {
        return new ConsultGenerationJobState
        {
            JobId = jobId,
            AppUserId = appUserId,
            Status = ConsultGenerationJobStatuses.Queued,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            TotalSectionCount = sections.Count,
            Sections = sections.ToDictionary(
                section => section.Id,
                section => new ConsultGenerationSectionState
                {
                    Id = section.Id,
                    Name = section.Name,
                    Status = ConsultGenerationSectionStatuses.Pending
                })
        };
    }

    public ConsultGenerationJobIndexEntry ToIndexEntry()
    {
        return new ConsultGenerationJobIndexEntry(
            JobId,
            AppUserId,
            Status,
            CreatedAtUtc,
            StartedAtUtc,
            CompletedAtUtc,
            TotalSectionCount,
            Sections.Values.Count(s => s.Status == ConsultGenerationSectionStatuses.Completed),
            Sections.Values.Count(s => s.Status == ConsultGenerationSectionStatuses.Failed));
    }

    public ConsultNodeOutputState GetOrAddNodeOutput(string nodeId, string label)
    {
        NodeOutputs ??= new Dictionary<string, ConsultNodeOutputState>(StringComparer.Ordinal);

        if (!NodeOutputs.TryGetValue(nodeId, out var node))
        {
            node = new ConsultNodeOutputState { NodeId = nodeId, Label = label };
            NodeOutputs[nodeId] = node;
        }

        return node;
    }

    public ConsultGenerationSectionState GetOrAddSection(string sectionId, string sectionName)
    {
        if (!Sections.TryGetValue(sectionId, out var section))
        {
            section = new ConsultGenerationSectionState
            {
                Id = sectionId,
                Name = sectionName,
                Status = ConsultGenerationSectionStatuses.Pending
            };

            Sections[sectionId] = section;
        }

        return section;
    }

    public ConsultGenerationJobResponse ToResponse()
    {
        var completedSections = Sections.Values
            .Where(section => section.Status == ConsultGenerationSectionStatuses.Completed)
            .ToDictionary(section => section.Id, section => section.GeneratedText ?? string.Empty);

        var failedSections = Sections.Values
            .Where(section => section.Status == ConsultGenerationSectionStatuses.Failed)
            .ToDictionary(section => section.Id, section => section.Error ?? "Section generation failed.");

        var sectionProseProgress = Sections.Values
            .ToDictionary(
                section => section.Id,
                section => new ConsultGenerationSectionProseProgress(
                    section.Id,
                    section.Name,
                    section.ProseStepStatus,
                    section.CompletedProseStepCount,
                    section.TotalProseStepCount));

        return new ConsultGenerationJobResponse(
            JobId,
            AppUserId,
            Status,
            Sections.Count,
            completedSections.Count,
            failedSections.Count,
            completedSections,
            failedSections,
            completedSections.Count > 0,
            SchemaVersion,
            AnalysisStatus,
            AnalysisError,
            PatientConcepts,
            ProblemContext,
            TypicalTrajectoryConcepts,
            PatientTrajectoryConcepts,
            CompletedStageCount,
            TotalStageCount,
            ValidationWarnings,
            sectionProseProgress,
            CreatedAtUtc: CreatedAtUtc,
            StartedAtUtc: StartedAtUtc,
            CompletedAtUtc: CompletedAtUtc,
            RuntimeFailureError: FailureError,
            History: History.Count > 0 ? History.AsReadOnly() : null,
            WorkflowPackage: WorkflowPackage,
            EffectiveInputHash: EffectiveInputHash,
            AgentVersion: AgentVersion,
            SectionSteps: SectionSteps,
            ConceptAgentVersion: ConceptAgentVersion,
            Nodes: Nodes,
            NodeOutputs: NodeOutputs?.ToDictionary(
                pair => pair.Key,
                pair => new ConsultGenerationNodeStatusResponse(
                    pair.Value.NodeId,
                    pair.Value.Label,
                    pair.Value.Status,
                    pair.Value.InputHash,
                    pair.Value.OutputHash,
                    pair.Value.CompletedAtUtc,
                    pair.Value.Error)));
    }
}

public sealed class ConsultGenerationSectionState
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = ConsultGenerationSectionStatuses.Pending;
    public string? GeneratedText { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string? ProseStepStatus { get; set; }
    public int CompletedProseStepCount { get; set; }
    public int TotalProseStepCount { get; set; } = ConsultGenerationSectionProseSteps.TotalStepCount;
}

/// <summary>Per-node run state: status, concepts (for JSON nodes), and provenance hashes.</summary>
public sealed class ConsultNodeOutputState
{
    public string NodeId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Status { get; set; } = ConsultGenerationNodeStatuses.Running;
    public List<ClinicalConcept>? Concepts { get; set; }
    public string? InputHash { get; set; }
    public string? OutputHash { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string? Error { get; set; }
}

public static class ConsultGenerationNodeStatuses
{
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Skipped = "Skipped";
}

public static class ConsultGenerationJobStatuses
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

public static class ConsultGenerationActivityNames
{
    public const string RunProseStep = "run-prose-step";
    public const string RunPromptNode = "run-prompt-node";
}

public static class ConsultGenerationAnalysisStatuses
{
    public const string AnalysisStarted = "analysis-started";
    public const string ConceptsExtracted = "concepts-extracted";
    public const string ProblemIdentified = "problem-identified";
    public const string TypicalTrajectoryCreated = "typical-trajectory-created";
    public const string PatientTrajectoryCreated = "patient-trajectory-created";
    public const string SectionGenerationStarted = "section-generation-started";
    public const string ConceptExtractionFailed = "concept-extraction-failed";
    public const string ProblemIdentificationFailed = "problem-identification-failed";
    public const string TypicalTrajectoryFailed = "typical-trajectory-failed";
    public const string PatientTrajectoryFailed = "patient-trajectory-failed";

    public static readonly string[] OrderedStages =
    [
        AnalysisStarted,
        ConceptsExtracted,
        ProblemIdentified,
        TypicalTrajectoryCreated,
        PatientTrajectoryCreated,
        SectionGenerationStarted
    ];

    public static int TotalStageCount => OrderedStages.Length;
}

public static class ConsultGenerationSectionProseSteps
{
    /// <summary>The single SSE event name for every prose step; the payload carries the step id and label.</summary>
    public const string EventName = "section-prose-step";

    /// <summary>Deserialization default for pre-milestone-3 job snapshots without a step list.</summary>
    public const int TotalStepCount = 3;
}

public static class ConsultGenerationSectionStatuses
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

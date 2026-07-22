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
        if (State == null || State.Blocks.Count == 0)
        {
            State = ConsultGenerationJobState.Create(input.JobId, input.AppUserId, input.Items);
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
                State.TotalSectionCount = input.Items.Count;
            }

            foreach (var item in input.Items)
            {
                State.GetOrAddBlock(item["id"], item.GetValueOrDefault("name", item["id"]));
            }
        }

        State.WorkflowPackage ??= input.WorkflowPackage;
        State.EffectiveInputHash ??= input.EffectiveInputHash;
        State.SectionSteps ??= input.SectionSteps?.ToList();
        State.Nodes ??= input.Nodes?.ToList();
        State.EffectiveInputHashVersion ??= input.EffectiveInputHashVersion;
        State.CatalogRef ??= input.CatalogRef;

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

    public async Task CompleteSection(SectionGenerationResult result)
    {
        var state = EnsureState();
        var block = state.GetOrAddBlock(result.SectionId, result.SectionName);
        block.Status = ConsultGenerationSectionStatuses.Completed;
        block.GeneratedText = result.GeneratedText ?? string.Empty;
        block.Error = null;
        block.CompletedAtUtc = DateTimeOffset.UtcNow;
        state.History.Add(new JobHistoryEvent("success", $"Section completed: {result.SectionName}", null, DateTimeOffset.UtcNow));
        State = state;

        await _indexStore.UpsertAsync(state.ToIndexEntry(), CancellationToken.None);
    }

    /// <summary>
    /// v6: stores the result aggregator's rendered output — the assembled
    /// document that IS the deliverable (package-format-v6-design.md § 4).
    /// </summary>
    public async Task CompleteDocument(string text)
    {
        var state = EnsureState();
        state.SchemaVersion = 6;
        state.AssembledDocument = text;
        state.History.Add(new JobHistoryEvent("success", "Assembled document produced.", null, DateTimeOffset.UtcNow));
        State = state;

        await _indexStore.UpsertAsync(state.ToIndexEntry(), CancellationToken.None);
    }

    public async Task FailSection(SectionGenerationResult result)
    {
        var state = EnsureState();
        var block = state.GetOrAddBlock(result.SectionId, result.SectionName);
        block.Status = ConsultGenerationSectionStatuses.Failed;
        block.GeneratedText = null;
        block.Error = string.IsNullOrWhiteSpace(result.Error) ? "Section generation failed." : result.Error;
        block.CompletedAtUtc = DateTimeOffset.UtcNow;
        state.History.Add(new JobHistoryEvent("failure", $"Section failed: {result.SectionName}", block.Error, DateTimeOffset.UtcNow));
        State = state;

        await _indexStore.UpsertAsync(state.ToIndexEntry(), CancellationToken.None);
    }

    /// <summary>
    /// One forEach instance completed: records the per-item node output (composite
    /// "nodeId:itemId" key, per-item provenance hashes) and the item's chain
    /// progress — the fields section-prose-step events are synthesized from.
    /// </summary>
    public void MarkNodeItemCompleted(ConsultGenerationNodeItemUpdate input)
    {
        var state = EnsureState();
        state.SchemaVersion = 6;

        var output = state.GetOrAddNodeOutput($"{input.NodeId}:{input.ItemId}", input.Label);
        output.NodeId = input.NodeId;
        output.ItemId = input.ItemId;
        output.Status = ConsultGenerationNodeStatuses.Completed;
        output.Concepts = input.Concepts?.ToList();
        output.InputHash = input.InputHash;
        output.OutputHash = input.OutputHash;
        output.CompletedAtUtc = DateTimeOffset.UtcNow;

        var progress = state.GetOrAddItemProgress(input.ItemId, input.ItemName);
        progress.ProseStepStatus = input.NodeId;
        progress.CompletedProseStepCount = input.CompletedChainCount;
        progress.TotalProseStepCount = input.TotalChainCount;
        State = state;
    }

    public void MarkNodeCompleted(ConsultGenerationNodeUpdate input)
    {
        var state = EnsureState();
        state.SchemaVersion = 6;

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

    public async Task MarkNodeFailed(ConsultGenerationNodeFailure input)
    {
        var state = EnsureState();
        state.SchemaVersion = 6;

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

        foreach (var block in state.Blocks.Values.OrderBy(b => b.Id, StringComparer.Ordinal))
        {
            state.History.Add(new JobHistoryEvent("skipped", $"Section not reached: {block.Name}", null, DateTimeOffset.UtcNow));
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

            // The map node completes here rather than through MarkNodeCompleted, so
            // the stage/node count catches up here too (was reported 4/5 on completed
            // jobs otherwise).
            if (input.Status == ConsultGenerationJobStatuses.Completed)
            {
                state.CompletedStageCount = state.TotalStageCount;
            }
        }

        if (input.Status == ConsultGenerationJobStatuses.Completed)
        {
            state.History.Add(new JobHistoryEvent("success", "Done", null, DateTimeOffset.UtcNow));
        }
        else if (input.Status == ConsultGenerationJobStatuses.Failed)
        {
            state.FailureError = input.Error;
            state.History.Add(new JobHistoryEvent("failure", "Failed", input.Error, DateTimeOffset.UtcNow));

            foreach (var block in state.Blocks.Values
                .Where(b => b.Status is not (ConsultGenerationSectionStatuses.Completed or ConsultGenerationSectionStatuses.Failed))
                .OrderBy(b => b.Id, StringComparer.Ordinal))
            {
                state.History.Add(new JobHistoryEvent("skipped", $"Section not reached: {block.Name}", null, DateTimeOffset.UtcNow));
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
        return State ?? ConsultGenerationJobState.Create(string.Empty, string.Empty, Array.Empty<IReadOnlyDictionary<string, string>>());
    }

}

public sealed record ConsultGenerationOrchestrationInput(
    ConsultGenerationRequest Request,
    string AppUserId,
    string? WorkflowPackage = null,
    string? EffectiveInputHash = null,
    IReadOnlyList<ConsultSectionStepDescriptor>? SectionSteps = null,
    IReadOnlyList<ConsultNodeDescriptor>? Nodes = null,
    string? ResultNodeId = null,
    IReadOnlyList<IReadOnlyDictionary<string, string>>? Items = null,
    IReadOnlyDictionary<string, string>? DataScalars = null,
    int EffectiveInputHashVersion = 2,
    string? CatalogRef = null,
    // v6 (package-format-v6-design.md): one item set per fanned collection,
    // keyed by collection id. Non-null selects the v6 path; Items then carries
    // the deliverable's BLOCKS (the result aggregator's expansion) for the
    // entity's section model, while the fan reads these sets.
    IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>>? Collections = null);

public sealed record ConsultGenerationJobInitialize(
    string JobId,
    string AppUserId,
    IReadOnlyList<IReadOnlyDictionary<string, string>> Items,
    string? WorkflowPackage = null,
    string? EffectiveInputHash = null,
    IReadOnlyList<ConsultSectionStepDescriptor>? SectionSteps = null,
    IReadOnlyList<ConsultNodeDescriptor>? Nodes = null,
    int EffectiveInputHashVersion = 2,
    string? CatalogRef = null);

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

public sealed record ConsultGenerationJobFinalize(string Status, string? Error = null);

/// <summary>
/// One forEach instance's completion: per-item provenance plus the item's chain
/// progress (the fields section-prose-step events are synthesized from).
/// </summary>
public sealed record ConsultGenerationNodeItemUpdate(
    string NodeId,
    string Label,
    string ItemId,
    string ItemName,
    IReadOnlyList<ClinicalConcept>? Concepts,
    string? InputHash,
    string? OutputHash,
    int CompletedChainCount,
    int TotalChainCount);

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
    public int CompletedStageCount { get; set; }
    public int TotalStageCount { get; set; }
    // The deliverable's blocks (#175): v5 = the sections; v6 = the result
    // aggregator's expansion (composite "sourceNodeId:itemId" keys for forEach
    // sources, node ids for scalar sources).
    public Dictionary<string, ConsultGenerationBlockState> Blocks { get; set; } = new();

    // Per-forEach-item chain progress (#175), keyed by the plain item id —
    // the fields section-prose-step events are synthesized from. Disjoint
    // from Blocks by design; the old dual-purpose Sections dict is gone
    // (stored records were wiped prerelease, no legacy shape survives).
    public Dictionary<string, ConsultGenerationItemProgressState> ItemProgress { get; set; } = new();
    public List<JobHistoryEvent> History { get; set; } = new();
    public string? FailureError { get; set; }
    public string? WorkflowPackage { get; set; }
    public string? EffectiveInputHash { get; set; }
    // The effective-input hash definition this job used: null/1 = draft+sections
    // (v2-v4 packages), 2 = draft only (v5 packages, package-format-v5.md).
    public int? EffectiveInputHashVersion { get; set; }

    // LEGACY, read-only since #105: records ≤ 2026-07-17 stored the contract →
    // agent-version map; later records carry catalogRef only (the catalog version
    // document holds the mapping). Kept so old records keep serving their map.
    public Dictionary<string, string>? AgentVersions { get; set; }

    // The concrete output-contract catalog version this job ran under
    // (output-contracts@vYYYY.MM.N) — the registry artifact resolving every
    // agentVersions entry (#93; docs/customizable-workflow/provenance.md).
    public string? CatalogRef { get; set; }
    public List<ConsultSectionStepDescriptor>? SectionSteps { get; set; }
    public List<ConsultNodeDescriptor>? Nodes { get; set; }
    public Dictionary<string, ConsultNodeOutputState>? NodeOutputs { get; set; }

    // v6: the result aggregator's rendered output — the deliverable itself
    // (stored text, the same species as sections' GeneratedText).
    public string? AssembledDocument { get; set; }

    public static ConsultGenerationJobState Create(
        string jobId,
        string appUserId,
        IReadOnlyList<IReadOnlyDictionary<string, string>> items)
    {
        return new ConsultGenerationJobState
        {
            JobId = jobId,
            AppUserId = appUserId,
            Status = ConsultGenerationJobStatuses.Queued,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            TotalSectionCount = items.Count,
            Blocks = items.ToDictionary(
                item => item["id"],
                item => new ConsultGenerationBlockState
                {
                    Id = item["id"],
                    Name = item.GetValueOrDefault("name", item["id"]),
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
            Blocks.Values.Count(b => b.Status == ConsultGenerationSectionStatuses.Completed),
            Blocks.Values.Count(b => b.Status == ConsultGenerationSectionStatuses.Failed));
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

    public ConsultGenerationBlockState GetOrAddBlock(string blockId, string blockName)
    {
        if (!Blocks.TryGetValue(blockId, out var block))
        {
            block = new ConsultGenerationBlockState
            {
                Id = blockId,
                Name = blockName,
                Status = ConsultGenerationSectionStatuses.Pending
            };

            Blocks[blockId] = block;
        }

        return block;
    }

    public ConsultGenerationItemProgressState GetOrAddItemProgress(string itemId, string itemName)
    {
        if (!ItemProgress.TryGetValue(itemId, out var progress))
        {
            progress = new ConsultGenerationItemProgressState { Id = itemId, Name = itemName };
            ItemProgress[itemId] = progress;
        }

        return progress;
    }

    public ConsultGenerationJobResponse ToResponse()
    {
        var completedSections = Blocks.Values
            .Where(block => block.Status == ConsultGenerationSectionStatuses.Completed)
            .ToDictionary(block => block.Id, block => block.GeneratedText ?? string.Empty);

        var failedSections = Blocks.Values
            .Where(block => block.Status == ConsultGenerationSectionStatuses.Failed)
            .ToDictionary(block => block.Id, block => block.Error ?? "Section generation failed.");

        var sectionProseProgress = ItemProgress.Values
            .ToDictionary(
                progress => progress.Id,
                progress => new ConsultGenerationSectionProseProgress(
                    progress.Id,
                    progress.Name,
                    progress.ProseStepStatus,
                    progress.CompletedProseStepCount,
                    progress.TotalProseStepCount));

        return new ConsultGenerationJobResponse(
            JobId,
            AppUserId,
            Status,
            // The stored scalar (the phase-7 decision), with the block count as
            // the seed-time fallback.
            TotalSectionCount > 0 ? TotalSectionCount : Blocks.Count,
            completedSections.Count,
            failedSections.Count,
            completedSections,
            failedSections,
            completedSections.Count > 0,
            SchemaVersion,
            AnalysisStatus,
            AnalysisError,
            CompletedStageCount,
            TotalStageCount,
            sectionProseProgress,
            CreatedAtUtc: CreatedAtUtc,
            StartedAtUtc: StartedAtUtc,
            CompletedAtUtc: CompletedAtUtc,
            RuntimeFailureError: FailureError,
            History: History.Count > 0 ? History.AsReadOnly() : null,
            WorkflowPackage: WorkflowPackage,
            EffectiveInputHash: EffectiveInputHash,
            SectionSteps: SectionSteps,
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
                    pair.Value.Error)),
            AgentVersions: AgentVersions,
            EffectiveInputHashVersion: EffectiveInputHashVersion,
            CatalogRef: CatalogRef,
            // Derived, never stored: the deliverable hash of a partial job is
            // undefined, so only completed jobs carry it (provenance.md).
            WorkflowOutputHash: Status != ConsultGenerationJobStatuses.Completed
                ? null
                : AssembledDocument != null
                    ? ConsultGenerationProvenance.ComputeAssembledDocumentHash(AssembledDocument)
                    : ConsultGenerationProvenance.ComputeWorkflowOutputHash(completedSections),
            WorkflowOutputHashVersion: Status != ConsultGenerationJobStatuses.Completed
                ? null
                : AssembledDocument != null
                    ? ConsultGenerationProvenance.AssembledDocumentHashVersion
                    : ConsultGenerationProvenance.WorkflowOutputHashVersion,
            AssembledDocument: Status == ConsultGenerationJobStatuses.Completed ? AssembledDocument : null);
    }
}

/// <summary>One deliverable block: its status and, when finished, its text or error.</summary>
public sealed class ConsultGenerationBlockState
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = ConsultGenerationSectionStatuses.Pending;
    public string? GeneratedText { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

/// <summary>One forEach item's chain progress — the section-prose-step source.</summary>
public sealed class ConsultGenerationItemProgressState
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ProseStepStatus { get; set; }
    public int CompletedProseStepCount { get; set; }
    public int TotalProseStepCount { get; set; } = ConsultGenerationSectionProseSteps.TotalStepCount;
}

/// <summary>Per-node run state: status, concepts (for JSON nodes), and provenance hashes.</summary>
public sealed class ConsultNodeOutputState
{
    public string NodeId { get; set; } = string.Empty;

    // Set on per-item entries (composite "nodeId:itemId" keys); null on scalar nodes.
    public string? ItemId { get; set; }
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
    public const string RunPromptNode = "run-prompt-node";
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

using Consultologist.Api.Agents;
using Consultologist.Api.Models;
using Consultologist.Api.Workflow;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Entities;
using Microsoft.Extensions.Logging;

namespace Consultologist.Api.Jobs;

public sealed class ConsultGenerationOrchestrator
{
    // Agent activity calls are nondeterministic: a retry re-runs the same prompt and the
    // agent issues fresh tool calls, so a transient tool failure (e.g. a rejected SNOMED
    // search) rarely repeats. Configuration errors (InvalidOperationException from
    // AgentSectionGenerator) are excluded so they fail fast instead of burning retries.
    // See docs/SNOMED_TOOL_FAILURES.md.
    private static readonly TaskOptions AgentActivityRetryOptions = TaskOptions.FromRetryPolicy(
        new RetryPolicy(
            maxNumberOfAttempts: 3,
            firstRetryInterval: TimeSpan.FromSeconds(5),
            backoffCoefficient: 2.0)
        {
            HandleFailure = failure => !failure.IsCausedBy<InvalidOperationException>()
        });

    [Function(nameof(ConsultGenerationOrchestrator))]
    public async Task RunAsync([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var input = context.GetInput<ConsultGenerationOrchestrationInput>()
            ?? throw new InvalidOperationException("Consult generation request input is required.");
        var request = input.Request;
        var logger = context.CreateReplaySafeLogger(nameof(ConsultGenerationOrchestrator));

        var entityId = new EntityInstanceId(nameof(ConsultGenerationJobEntity), context.InstanceId);

        try
        {
        var nodes = input.Nodes is { Count: > 0 }
            ? input.Nodes
            : throw new InvalidOperationException("Consult generation input carries no workflow nodes; the job start snapshots them from the workflow package.");
        var resultNodeId = input.ResultNodeId
            ?? throw new InvalidOperationException("Consult generation input names no result node; the job start snapshots it from the workflow package.");

        // The work items: v5 jobs carry the one collection's items; v6 jobs carry
        // per-collection sets in Collections and the deliverable's BLOCKS in Items
        // (the result aggregator's expansion, feeding the entity's section model).
        var items = input.Items is { Count: > 0 }
            ? input.Items
            : throw new InvalidOperationException("Consult generation input carries no work items; the job start snapshots them from the workflow package.");
        var collections = input.Collections;
        var v6 = collections is { Count: > 0 };

        await context.Entities.CallEntityAsync(
            entityId,
            nameof(ConsultGenerationJobEntity.Initialize),
            new ConsultGenerationJobInitialize(
                context.InstanceId,
                input.AppUserId,
                items,
                input.WorkflowPackage,
                input.EffectiveInputHash,
                input.ItemSteps,
                nodes,
                input.EffectiveInputHashVersion,
                input.CatalogRef));

        await context.Entities.CallEntityAsync(entityId, nameof(ConsultGenerationJobEntity.MarkRunning));

        logger.LogInformation(
            "ConsultGenerationOrchestrator started. JobId={JobId}, AppUserId={AppUserId}, ItemCount={ItemCount}, NodeCount={NodeCount}",
            context.InstanceId,
            input.AppUserId,
            items.Count,
            nodes.Count);

        var totalBlockCount = items.Count;
        var completedBlockCount = 0;
        var failedBlockCount = 0;
        var totalNodeCount = nodes.Count;
        var completedNodeCount = 0;

        void PublishStatus(string status) => context.SetCustomStatus(new
        {
            status,
            totalBlockCount,
            completedBlockCount,
            failedBlockCount,
            completedNodeCount,
            totalNodeCount
        });

        PublishStatus(ConsultGenerationJobStatuses.Running);

        // The one-kind interpreter: a per-(node, item) ready loop over the snapshotted
        // descriptors. A scalar node runs once; a forEach node runs once per item,
        // instance i depending item-aligned on same-collection upstream instances.
        // Determinism rules — start order derives only from manifest order plus
        // recorded activity completions; variable rendering is pure over recorded
        // results (docs/customizable-workflow/package-format-v5.md).
        var nodesById = nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var chainNodes = nodes.Where(node => node.ForEach != null).ToList();
        var aggregators = nodes.Where(node => node.Aggregate != null).ToList();
        var outputs = new Dictionary<string, NodeRunResult>(StringComparer.Ordinal);
        var pendingTasks = new Dictionary<Task<NodeRunResult>, (string NodeId, string? ItemId)>();
        var started = new HashSet<string>(StringComparer.Ordinal);
        var failedItems = new HashSet<string>(StringComparer.Ordinal);
        var failedAggregators = new Dictionary<string, string>(StringComparer.Ordinal);
        var nodeLevelCompleted = new HashSet<string>(StringComparer.Ordinal);

        string CollectionIdOf(ConsultNodeDescriptor node) =>
            node.ForEach!.StartsWith(WorkflowNodeBindingSources.DataPrefix, StringComparison.Ordinal)
                ? node.ForEach[WorkflowNodeBindingSources.DataPrefix.Length..]
                : node.ForEach;

        // v6: each forEach node fans over ITS collection's items; v5 keeps the
        // single shared set. Failed-item keys are collection-scoped in v6 (item
        // ids are unique per collection, not globally).
        IReadOnlyList<IReadOnlyDictionary<string, string>> FanItems(ConsultNodeDescriptor node) =>
            v6
                ? collections!.GetValueOrDefault(CollectionIdOf(node))
                    ?? throw new InvalidOperationException($"Node '{node.Id}' fans collection '{CollectionIdOf(node)}', which the job snapshot does not carry.")
                : items;

        string FailedKey(ConsultNodeDescriptor node, string itemId) =>
            v6 ? $"{CollectionIdOf(node)}:{itemId}" : itemId;

        string ItemName(ConsultNodeDescriptor node, string itemId) =>
            FanItems(node).First(item => item["id"] == itemId).GetValueOrDefault("name", itemId);

        // v6 deliverable blocks: the result aggregator's direct sources.
        var resultNode = nodesById.GetValueOrDefault(resultNodeId);
        var resultSourceIds = v6 && resultNode?.Aggregate != null
            ? resultNode.Aggregate
                .Select(sourceRef => sourceRef.StartsWith(WorkflowNodeBindingSources.NodePrefix, StringComparison.Ordinal)
                    ? sourceRef[WorkflowNodeBindingSources.NodePrefix.Length..]
                    : sourceRef)
                .ToList()
            : new List<string>();

        void StartInstance(ConsultNodeDescriptor node, IReadOnlyDictionary<string, string>? item)
        {
            var instanceKey = ConsultNodeScheduler.InstanceKey(node.Id, item?["id"]);
            started.Add(instanceKey);

            var variables = ConsultNodeVariableResolver.Resolve(
                node, request.ConsultDraft, item, input.DataScalars, nodesById, outputs);

            pendingTasks[context.CallActivityAsync<NodeRunResult>(
                ConsultGenerationActivityNames.RunPromptNode,
                new ConsultPromptNodeActivityInput(
                    instanceKey,
                    node.PromptId!,
                    variables,
                    input.WorkflowPackage,
                    node.OutputContract,
                    node.ConceptSource),
                AgentActivityRetryOptions)] = (node.Id, item?["id"]);
        }

        void StartReadyInstances()
        {
            foreach (var node in nodes)
            {
                if (node.Aggregate != null)
                {
                    continue; // Aggregators compose inline, never as activities.
                }

                if (node.ForEach is null)
                {
                    if (!started.Contains(node.Id)
                        && ConsultNodeScheduler.InstanceReady(node, null, nodesById, outputs))
                    {
                        StartInstance(node, null);
                    }

                    continue;
                }

                foreach (var item in FanItems(node))
                {
                    var itemId = item["id"];

                    if (failedItems.Contains(FailedKey(node, itemId))
                        || started.Contains(ConsultNodeScheduler.InstanceKey(node.Id, itemId)))
                    {
                        continue;
                    }

                    if (ConsultNodeScheduler.InstanceReady(node, itemId, nodesById, outputs))
                    {
                        StartInstance(node, item);
                    }
                }
            }
        }

        async Task MarkFullyCompletedChainNodesAsync()
        {
            // A forEach node completes at the node level once every item is either
            // done for it or failed earlier in its chain — this is what drives the
            // stage checklist and the node-completed events.
            foreach (var node in chainNodes)
            {
                if (nodeLevelCompleted.Contains(node.Id))
                {
                    continue;
                }

                var allSettled = FanItems(node).All(item =>
                    failedItems.Contains(FailedKey(node, item["id"]))
                    || outputs.ContainsKey(ConsultNodeScheduler.InstanceKey(node.Id, item["id"])));

                if (!allSettled)
                {
                    continue;
                }

                nodeLevelCompleted.Add(node.Id);
                completedNodeCount++;

                await context.Entities.CallEntityAsync(
                    entityId,
                    nameof(ConsultGenerationJobEntity.MarkNodeCompleted),
                    new ConsultGenerationNodeUpdate(node.Id, node.Label, null, null, null, completedNodeCount, totalNodeCount));
            }
        }

        async Task FailItemAsync(ConsultNodeDescriptor node, string itemId, string error)
        {
            failedItems.Add(FailedKey(node, itemId));

            if (!v6)
            {
                failedBlockCount++;

                await context.Entities.CallEntityAsync(
                    entityId,
                    nameof(ConsultGenerationJobEntity.FailBlock),
                    new BlockGenerationResult(itemId, ItemName(node, itemId), false, null, error));
            }
            else
            {
                // v6 blocks: every result-source block over this collection fails —
                // the item can no longer reach the document.
                foreach (var source in resultSourceIds
                    .Select(id => nodesById.GetValueOrDefault(id))
                    .Where(source => source?.ForEach != null
                        && string.Equals(CollectionIdOf(source), CollectionIdOf(node), StringComparison.Ordinal)))
                {
                    failedBlockCount++;

                    await context.Entities.CallEntityAsync(
                        entityId,
                        nameof(ConsultGenerationJobEntity.FailBlock),
                        new BlockGenerationResult($"{source!.Id}:{itemId}", ItemName(node, itemId), false, null, error));
                }
            }

            // Downstream item-aligned instances never start; nodes waiting only on
            // this item may now be node-level complete.
            await MarkFullyCompletedChainNodesAsync();
        }

        // Aggregators compose inline once every source settles — deterministic,
        // no activity, no retries; a failed contributing item fails the aggregator
        // loud (never a partial document), cascading downstream by absence
        // (package-format-v6-design.md §§ 3, 6).
        async Task TryCompleteAggregatorsAsync()
        {
            var progressed = true;

            while (progressed)
            {
                progressed = false;

                foreach (var aggregator in aggregators)
                {
                    if (outputs.ContainsKey(aggregator.Id) || failedAggregators.ContainsKey(aggregator.Id))
                    {
                        continue;
                    }

                    var parts = new List<ConsultAggregateRenderer.Part>();
                    var sourceHashes = new List<string>();
                    var failedContributors = new List<string>();
                    var allSettled = true;

                    foreach (var sourceRef in aggregator.Aggregate!)
                    {
                        var sourceId = sourceRef.StartsWith(WorkflowNodeBindingSources.NodePrefix, StringComparison.Ordinal)
                            ? sourceRef[WorkflowNodeBindingSources.NodePrefix.Length..]
                            : sourceRef;
                        var source = nodesById[sourceId];

                        if (source.ForEach != null)
                        {
                            var sourceItems = FanItems(source);
                            var failed = sourceItems
                                .Where(item => failedItems.Contains(FailedKey(source, item["id"])))
                                .Select(item => item["id"])
                                .ToList();

                            if (failed.Count > 0)
                            {
                                failedContributors.AddRange(failed.Select(id => $"{sourceId}:{id}"));
                                continue;
                            }

                            if (!sourceItems.All(item => outputs.ContainsKey(ConsultNodeScheduler.InstanceKey(sourceId, item["id"]))))
                            {
                                allSettled = false;
                                continue;
                            }

                            parts.Add(new ConsultAggregateRenderer.ForEachPart(sourceItems
                                .Select(item => (
                                    item.GetValueOrDefault("name", item["id"]),
                                    outputs[ConsultNodeScheduler.InstanceKey(sourceId, item["id"])].RawOutput.Trim()))
                                .ToList()));
                            sourceHashes.AddRange(sourceItems
                                .Select(item => outputs[ConsultNodeScheduler.InstanceKey(sourceId, item["id"])].OutputHash));
                        }
                        else if (failedAggregators.ContainsKey(sourceId))
                        {
                            failedContributors.Add(sourceId);
                        }
                        else if (outputs.TryGetValue(sourceId, out var sourceResult))
                        {
                            parts.Add(new ConsultAggregateRenderer.ScalarPart(sourceResult.RawOutput.Trim()));
                            sourceHashes.Add(sourceResult.OutputHash);
                        }
                        else
                        {
                            allSettled = false;
                        }
                    }

                    if (failedContributors.Count > 0)
                    {
                        failedAggregators[aggregator.Id] =
                            $"{aggregator.Label} could not assemble: {string.Join(", ", failedContributors)} did not complete.";
                        progressed = true;
                        continue;
                    }

                    if (!allSettled)
                    {
                        continue;
                    }

                    var rendered = ConsultAggregateRenderer.Render(parts);
                    var aggregateResult = new NodeRunResult(
                        rendered,
                        null,
                        ConsultGenerationProvenance.ComputeAggregateInputHash(sourceHashes),
                        ConsultGenerationProvenance.Sha256Hex(rendered));
                    outputs[aggregator.Id] = aggregateResult;
                    completedNodeCount++;
                    progressed = true;

                    await context.Entities.CallEntityAsync(
                        entityId,
                        nameof(ConsultGenerationJobEntity.MarkNodeCompleted),
                        new ConsultGenerationNodeUpdate(
                            aggregator.Id, aggregator.Label, null,
                            aggregateResult.InputHash, aggregateResult.OutputHash,
                            completedNodeCount, totalNodeCount));

                    if (string.Equals(aggregator.Id, resultNodeId, StringComparison.Ordinal))
                    {
                        await context.Entities.CallEntityAsync(
                            entityId,
                            nameof(ConsultGenerationJobEntity.CompleteDocument),
                            rendered);
                    }
                }
            }
        }

        StartReadyInstances();

        while (pendingTasks.Count > 0)
        {
            var completedTask = await Task.WhenAny(pendingTasks.Keys);
            var (nodeId, itemId) = pendingTasks[completedTask];
            pendingTasks.Remove(completedTask);
            var node = nodesById[nodeId];

            if (itemId is null)
            {
                // Scalar node: an activity failure (post-retries) is a runtime job
                // failure, and an empty declared-required output fails the job with
                // the node's message — unchanged semantics from the wave interpreter.
                var result = await completedTask;

                if (node.FailIfEmpty != null && (result.Concepts?.Count ?? 0) == 0)
                {
                    await FailNodeAsync(
                        context, entityId, node, nodes, outputs,
                        totalBlockCount, completedBlockCount, failedBlockCount, logger);
                    return;
                }

                outputs[nodeId] = result;
                completedNodeCount++;

                await context.Entities.CallEntityAsync(
                    entityId,
                    nameof(ConsultGenerationJobEntity.MarkNodeCompleted),
                    new ConsultGenerationNodeUpdate(
                        node.Id, node.Label, result.Concepts, result.InputHash, result.OutputHash,
                        completedNodeCount, totalNodeCount));

                if (v6 && resultSourceIds.Contains(nodeId))
                {
                    // A scalar source of the result aggregator is one block.
                    completedBlockCount++;

                    await context.Entities.CallEntityAsync(
                        entityId,
                        nameof(ConsultGenerationJobEntity.CompleteBlock),
                        new BlockGenerationResult(nodeId, node.Label, true, result.RawOutput.Trim(), null));
                }
            }
            else
            {
                // forEach instance: failures are per-item — the section fails, the
                // job continues (the section-failure semantics the map era had).
                NodeRunResult result;

                try
                {
                    result = await completedTask;
                }
                catch (Exception ex)
                {
                    await FailItemAsync(node, itemId, $"{node.Label} failed: {ex.Message}");
                    PublishStatus(ConsultGenerationJobStatuses.Running);
                    StartReadyInstances();
                    continue;
                }

                if (node.FailIfEmpty != null && (result.Concepts?.Count ?? 0) == 0)
                {
                    await FailItemAsync(node, itemId, node.FailIfEmpty);
                    PublishStatus(ConsultGenerationJobStatuses.Running);
                    StartReadyInstances();
                    continue;
                }

                outputs[ConsultNodeScheduler.InstanceKey(nodeId, itemId)] = result;

                var chain = v6
                    ? chainNodes.Where(chainNode => string.Equals(CollectionIdOf(chainNode), CollectionIdOf(node), StringComparison.Ordinal)).ToList()
                    : chainNodes;
                var completedChainCount = chain.Count(chainNode =>
                    outputs.ContainsKey(ConsultNodeScheduler.InstanceKey(chainNode.Id, itemId)));

                await context.Entities.CallEntityAsync(
                    entityId,
                    nameof(ConsultGenerationJobEntity.MarkNodeItemCompleted),
                    new ConsultGenerationNodeItemUpdate(
                        node.Id, node.Label, itemId, ItemName(node, itemId),
                        result.Concepts, result.InputHash, result.OutputHash,
                        completedChainCount, chain.Count));

                if (!v6 && nodeId == resultNodeId)
                {
                    completedBlockCount++;

                    await context.Entities.CallEntityAsync(
                        entityId,
                        nameof(ConsultGenerationJobEntity.CompleteBlock),
                        new BlockGenerationResult(itemId, ItemName(node, itemId), true, result.RawOutput.Trim(), null));
                }
                else if (v6 && resultSourceIds.Contains(nodeId))
                {
                    // v6 blocks stream as the result aggregator's sources complete.
                    completedBlockCount++;

                    await context.Entities.CallEntityAsync(
                        entityId,
                        nameof(ConsultGenerationJobEntity.CompleteBlock),
                        new BlockGenerationResult($"{nodeId}:{itemId}", ItemName(node, itemId), true, result.RawOutput.Trim(), null));
                }

                await MarkFullyCompletedChainNodesAsync();
            }

            await TryCompleteAggregatorsAsync();
            PublishStatus(ConsultGenerationJobStatuses.Running);
            StartReadyInstances();
        }

        var expectedInstances = nodes.Where(node => node.Aggregate is null).Sum(node => node.ForEach is null
            ? 1
            : FanItems(node).Count(item => !failedItems.Contains(FailedKey(node, item["id"]))));
        if (started.Count(key => outputs.ContainsKey(key)) < expectedInstances && failedItems.Count == 0)
        {
            // Unreachable for validated packages (the graph is acyclic and complete);
            // defensive so a bad snapshot fails loud instead of hanging.
            throw new InvalidOperationException("Workflow nodes could not all be scheduled; the node graph is not executable.");
        }

        // A final pass settles aggregator failure state for the job outcome.
        await TryCompleteAggregatorsAsync();

        string finalStatus;
        string? finalError = null;

        if (v6)
        {
            // The deliverable is the assembled document: no document, no consult
            // (fail-loud all the way to the job, package-format-v6-design.md § 3).
            var documentProduced = outputs.ContainsKey(resultNodeId);
            finalStatus = documentProduced
                ? ConsultGenerationJobStatuses.Completed
                : ConsultGenerationJobStatuses.Failed;
            finalError = documentProduced
                ? null
                : failedAggregators.GetValueOrDefault(resultNodeId)
                    ?? failedAggregators.Values.FirstOrDefault()
                    ?? "The assembled document could not be produced.";
        }
        else
        {
            finalStatus = completedBlockCount > 0
                ? ConsultGenerationJobStatuses.Completed
                : ConsultGenerationJobStatuses.Failed;
        }

        logger.LogInformation(
            "ConsultGenerationOrchestrator completed. JobId={JobId}, Status={Status}, Completed={CompletedSectionCount}, Failed={FailedSectionCount}, Total={TotalSectionCount}",
            context.InstanceId,
            finalStatus,
            completedBlockCount,
            failedBlockCount,
            totalBlockCount);

        await context.Entities.CallEntityAsync(
            entityId,
            nameof(ConsultGenerationJobEntity.FinalizeJob),
            new ConsultGenerationJobFinalize(finalStatus, finalError));

        PublishStatus(finalStatus);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex,
                "ConsultGenerationOrchestrator unhandled exception. JobId={JobId}, ExceptionType={ExceptionType}, Message={Message}",
                context.InstanceId,
                ex.GetType().FullName,
                ex.Message);

            try
            {
                await context.Entities.CallEntityAsync(
                    entityId,
                    nameof(ConsultGenerationJobEntity.FinalizeJob),
                    new ConsultGenerationJobFinalize(ConsultGenerationJobStatuses.Failed, ex.Message));
            }
            catch (Exception cleanupEx)
            {
                logger.LogWarning(
                    cleanupEx,
                    "ConsultGenerationOrchestrator FinalizeJob cleanup failed. JobId={JobId}, ExceptionType={ExceptionType}, Message={Message}",
                    context.InstanceId,
                    cleanupEx.GetType().FullName,
                    cleanupEx.Message);
            }

            throw;
        }
    }

    private static async Task FailNodeAsync(
        TaskOrchestrationContext context,
        EntityInstanceId entityId,
        ConsultNodeDescriptor failedNode,
        IReadOnlyList<ConsultNodeDescriptor> nodes,
        IReadOnlyDictionary<string, NodeRunResult> outputs,
        int totalBlockCount,
        int completedBlockCount,
        int failedBlockCount,
        ILogger logger)
    {
        // Keeps the "-failed" suffix convention the SSE failure path keys on.
        var analysisStatus = $"{failedNode.Id}-failed";
        var analysisError = failedNode.FailIfEmpty!;

        logger.LogWarning(
            "Consult generation node failed. JobId={JobId}, NodeId={NodeId}, Reason={Reason}",
            context.InstanceId,
            failedNode.Id,
            analysisError);

        // The orchestrator holds the graph, so it computes the unreached set — every
        // node neither completed nor the failed one, in manifest order.
        var skippedNodes = nodes
            .Where(node => node.Id != failedNode.Id && !outputs.ContainsKey(node.Id))
            .Select(node => new ConsultItemStepDescriptor(node.Id, node.Label))
            .ToList();

        await context.Entities.CallEntityAsync(
            entityId,
            nameof(ConsultGenerationJobEntity.MarkNodeFailed),
            new ConsultGenerationNodeFailure(failedNode.Id, failedNode.Label, analysisStatus, analysisError, skippedNodes));

        context.SetCustomStatus(new
        {
            status = ConsultGenerationJobStatuses.Failed,
            totalBlockCount,
            completedBlockCount,
            failedBlockCount,
            analysisStatus,
            analysisError
        });
    }
}

/// <summary>
/// The pure scheduling calculus of the one-kind interpreter: instance identity and
/// per-(node, item) readiness. Kept free of Durable types so the wave semantics are
/// unit-testable (docs/customizable-workflow/package-format-v5.md).
/// </summary>
internal static class ConsultNodeScheduler
{
    /// <summary>Scalar nodes key by id; forEach instances by "nodeId:itemId".</summary>
    public static string InstanceKey(string nodeId, string? itemId)
        => itemId is null ? nodeId : $"{nodeId}:{itemId}";

    /// <summary>
    /// A scalar node is ready when every node: dependency completed; a forEach
    /// instance additionally requires same-collection dependencies item-aligned
    /// (the validator guarantees no aggregate or cross-collection edges exist).
    /// </summary>
    public static bool InstanceReady(
        ConsultNodeDescriptor node,
        string? itemId,
        IReadOnlyDictionary<string, ConsultNodeDescriptor> nodesById,
        IReadOnlyDictionary<string, NodeRunResult> outputs)
    {
        return NodeDependencies(node).All(dependencyId =>
            outputs.ContainsKey(InstanceKey(
                dependencyId,
                nodesById[dependencyId].ForEach is null ? null : itemId)));
    }

    public static IEnumerable<string> NodeDependencies(ConsultNodeDescriptor node)
    {
        return (node.Bindings ?? new Dictionary<string, ConsultNodeBindingDescriptor>())
            .Values
            .Where(binding => binding.From.StartsWith(WorkflowNodeBindingSources.NodePrefix, StringComparison.Ordinal))
            .Select(binding => binding.From[WorkflowNodeBindingSources.NodePrefix.Length..]);
    }
}

/// <summary>
/// Resolves a node instance's bindings to rendered variable values — pure functions
/// over the orchestration input and recorded activity results, so orchestrator-side
/// use is replay-safe. Renderer implementations stay where the byte-pinning tests
/// point: ConsultGenerationConceptFormatter and AgentSectionGenerator.FormatConcepts.
/// </summary>
internal static class ConsultNodeVariableResolver
{
    public static Dictionary<string, string> Resolve(
        ConsultNodeDescriptor node,
        string consultDraft,
        IReadOnlyDictionary<string, string>? item,
        IReadOnlyDictionary<string, string>? dataScalars,
        IReadOnlyDictionary<string, ConsultNodeDescriptor> nodesById,
        IReadOnlyDictionary<string, NodeRunResult> outputs)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (variable, binding) in node.Bindings ?? new Dictionary<string, ConsultNodeBindingDescriptor>())
        {
            variables[variable] = binding.From switch
            {
                WorkflowNodeBindingSources.InputConsultDraft => consultDraft,
                _ when binding.From.StartsWith("item:", StringComparison.Ordinal)
                    => (item ?? throw new InvalidOperationException(
                            $"Node '{node.Id}' binding '{variable}' reads '{binding.From}' outside a forEach instance."))
                        .GetValueOrDefault(binding.From["item:".Length..])
                        ?? throw new InvalidOperationException(
                            $"Node '{node.Id}' binding '{variable}' reads item field '{binding.From["item:".Length..]}', which the item does not carry."),
                _ when binding.From.StartsWith(WorkflowNodeBindingSources.DataPrefix, StringComparison.Ordinal)
                    => (dataScalars ?? throw new InvalidOperationException(
                            $"Node '{node.Id}' binding '{variable}' reads '{binding.From}' but the job carries no data scalars."))
                        .GetValueOrDefault(binding.From[WorkflowNodeBindingSources.DataPrefix.Length..])
                        ?? throw new InvalidOperationException(
                            $"Node '{node.Id}' binding '{variable}' reads unknown data entry '{binding.From[WorkflowNodeBindingSources.DataPrefix.Length..]}'."),
                _ when binding.From.StartsWith(WorkflowNodeBindingSources.NodePrefix, StringComparison.Ordinal)
                    => RenderNodeOutput(node.Id, variable, binding, item, nodesById, outputs),
                _ => throw new InvalidOperationException(
                    $"Node '{node.Id}' binding '{variable}' has source '{binding.From}', which a node cannot resolve.")
            };
        }

        return variables;
    }

    public static string Render(string renderer, IReadOnlyList<ClinicalConcept> concepts) => renderer switch
    {
        WorkflowConceptRenderers.ConceptBullets => ConsultGenerationConceptFormatter.Format(concepts),
        WorkflowConceptRenderers.ConceptContext => AgentSectionGenerator.FormatConcepts(concepts),
        _ => throw new InvalidOperationException($"Unknown concept renderer '{renderer}'.")
    };

    private static string RenderNodeOutput(
        string nodeId,
        string variable,
        ConsultNodeBindingDescriptor binding,
        IReadOnlyDictionary<string, string>? item,
        IReadOnlyDictionary<string, ConsultNodeDescriptor> nodesById,
        IReadOnlyDictionary<string, NodeRunResult> outputs)
    {
        var targetId = binding.From[WorkflowNodeBindingSources.NodePrefix.Length..];
        var target = nodesById[targetId];
        var result = outputs[ConsultNodeScheduler.InstanceKey(
            targetId,
            target.ForEach is null ? null : item?["id"]
                ?? throw new InvalidOperationException(
                    $"Node '{nodeId}' binding '{variable}' targets forEach node '{targetId}' outside a forEach instance."))];

        if (target.OutputContract is null)
        {
            return result.RawOutput;
        }

        return Render(binding.As ?? WorkflowConceptRenderers.ConceptBullets, result.Concepts
            ?? throw new InvalidOperationException($"Node '{nodeId}' binding '{variable}' targets '{targetId}', which recorded no concepts."));
    }
}

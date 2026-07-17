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

        // The work items: the package data collection, snapshotted at job start.
        var items = input.Items is { Count: > 0 }
            ? input.Items
            : throw new InvalidOperationException("Consult generation input carries no work items; the job start snapshots them from the workflow package.");

        await context.Entities.CallEntityAsync(
            entityId,
            nameof(ConsultGenerationJobEntity.Initialize),
            new ConsultGenerationJobInitialize(
                context.InstanceId,
                input.AppUserId,
                items,
                input.WorkflowPackage,
                input.EffectiveInputHash,
                input.SectionSteps,
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

        var totalSectionCount = items.Count;
        var completedSectionCount = 0;
        var failedSectionCount = 0;
        var totalNodeCount = nodes.Count;
        var completedNodeCount = 0;

        void PublishStatus(string status) => context.SetCustomStatus(new
        {
            status,
            totalSectionCount,
            completedSectionCount,
            failedSectionCount,
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
        var outputs = new Dictionary<string, NodeRunResult>(StringComparer.Ordinal);
        var pendingTasks = new Dictionary<Task<NodeRunResult>, (string NodeId, string? ItemId)>();
        var started = new HashSet<string>(StringComparer.Ordinal);
        var failedItems = new HashSet<string>(StringComparer.Ordinal);
        var nodeLevelCompleted = new HashSet<string>(StringComparer.Ordinal);

        string ItemName(string itemId) =>
            items.First(item => item["id"] == itemId).GetValueOrDefault("name", itemId);

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
                if (node.ForEach is null)
                {
                    if (!started.Contains(node.Id)
                        && ConsultNodeScheduler.InstanceReady(node, null, nodesById, outputs))
                    {
                        StartInstance(node, null);
                    }

                    continue;
                }

                foreach (var item in items)
                {
                    var itemId = item["id"];

                    if (failedItems.Contains(itemId)
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

                var allSettled = items.All(item =>
                    failedItems.Contains(item["id"])
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

        async Task FailItemAsync(string itemId, string error)
        {
            failedItems.Add(itemId);
            failedSectionCount++;

            await context.Entities.CallEntityAsync(
                entityId,
                nameof(ConsultGenerationJobEntity.FailSection),
                new SectionGenerationResult(itemId, ItemName(itemId), false, null, error));

            // Downstream item-aligned instances never start; nodes waiting only on
            // this item may now be node-level complete.
            await MarkFullyCompletedChainNodesAsync();
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
                        totalSectionCount, completedSectionCount, failedSectionCount, logger);
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
                    await FailItemAsync(itemId, $"{node.Label} failed: {ex.Message}");
                    PublishStatus(ConsultGenerationJobStatuses.Running);
                    StartReadyInstances();
                    continue;
                }

                if (node.FailIfEmpty != null && (result.Concepts?.Count ?? 0) == 0)
                {
                    await FailItemAsync(itemId, node.FailIfEmpty);
                    PublishStatus(ConsultGenerationJobStatuses.Running);
                    StartReadyInstances();
                    continue;
                }

                outputs[ConsultNodeScheduler.InstanceKey(nodeId, itemId)] = result;

                var completedChainCount = chainNodes.Count(chainNode =>
                    outputs.ContainsKey(ConsultNodeScheduler.InstanceKey(chainNode.Id, itemId)));

                await context.Entities.CallEntityAsync(
                    entityId,
                    nameof(ConsultGenerationJobEntity.MarkNodeItemCompleted),
                    new ConsultGenerationNodeItemUpdate(
                        node.Id, node.Label, itemId, ItemName(itemId),
                        result.Concepts, result.InputHash, result.OutputHash,
                        completedChainCount, chainNodes.Count));

                if (nodeId == resultNodeId)
                {
                    completedSectionCount++;

                    await context.Entities.CallEntityAsync(
                        entityId,
                        nameof(ConsultGenerationJobEntity.CompleteSection),
                        new SectionGenerationResult(itemId, ItemName(itemId), true, result.RawOutput.Trim(), null));
                }

                await MarkFullyCompletedChainNodesAsync();
            }

            PublishStatus(ConsultGenerationJobStatuses.Running);
            StartReadyInstances();
        }

        var expectedInstances = nodes.Sum(node => node.ForEach is null
            ? 1
            : items.Count(item => !failedItems.Contains(item["id"])));
        if (started.Count(key => outputs.ContainsKey(key)) < expectedInstances && failedItems.Count == 0)
        {
            // Unreachable for validated packages (the graph is acyclic and complete);
            // defensive so a bad snapshot fails loud instead of hanging.
            throw new InvalidOperationException("Workflow nodes could not all be scheduled; the node graph is not executable.");
        }

        var finalStatus = completedSectionCount > 0
            ? ConsultGenerationJobStatuses.Completed
            : ConsultGenerationJobStatuses.Failed;

        logger.LogInformation(
            "ConsultGenerationOrchestrator completed. JobId={JobId}, Completed={CompletedSectionCount}, Failed={FailedSectionCount}, Total={TotalSectionCount}",
            context.InstanceId,
            completedSectionCount,
            failedSectionCount,
            totalSectionCount);

        await context.Entities.CallEntityAsync(
            entityId,
            nameof(ConsultGenerationJobEntity.FinalizeJob),
            new ConsultGenerationJobFinalize(finalStatus));

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
        int totalSectionCount,
        int completedSectionCount,
        int failedSectionCount,
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
            .Select(node => new ConsultSectionStepDescriptor(node.Id, node.Label))
            .ToList();

        await context.Entities.CallEntityAsync(
            entityId,
            nameof(ConsultGenerationJobEntity.MarkNodeFailed),
            new ConsultGenerationNodeFailure(failedNode.Id, failedNode.Label, analysisStatus, analysisError, skippedNodes));

        context.SetCustomStatus(new
        {
            status = ConsultGenerationJobStatuses.Failed,
            totalSectionCount,
            completedSectionCount,
            failedSectionCount,
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

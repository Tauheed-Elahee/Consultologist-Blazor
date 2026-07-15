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
        var sectionSteps = input.SectionSteps is { Count: > 0 }
            ? input.SectionSteps
            : throw new InvalidOperationException("Consult generation input carries no section steps; the job start snapshots them from the workflow package.");
        var nodes = input.Nodes is { Count: > 0 }
            ? input.Nodes
            : throw new InvalidOperationException("Consult generation input carries no workflow nodes; the job start snapshots them from the workflow package.");

        await context.Entities.CallEntityAsync(
            entityId,
            nameof(ConsultGenerationJobEntity.Initialize),
            new ConsultGenerationJobInitialize(
                context.InstanceId,
                input.AppUserId,
                request.Sections,
                input.WorkflowPackage,
                input.EffectiveInputHash,
                input.AgentVersion,
                sectionSteps,
                input.ConceptAgentVersion,
                nodes,
                input.AgentVersions));

        await context.Entities.CallEntityAsync(entityId, nameof(ConsultGenerationJobEntity.MarkRunning));

        logger.LogInformation(
            "ConsultGenerationOrchestrator started. JobId={JobId}, AppUserId={AppUserId}, SectionCount={SectionCount}",
            context.InstanceId,
            input.AppUserId,
            request.Sections.Count);

        var totalSectionCount = request.Sections.Count;
        var completedSectionCount = 0;
        var failedSectionCount = 0;
        var totalNodeCount = nodes.Count;
        var completedNodeCount = 0;

        context.SetCustomStatus(new
        {
            status = ConsultGenerationJobStatuses.Running,
            totalSectionCount,
            completedSectionCount,
            failedSectionCount,
            completedNodeCount,
            totalNodeCount
        });

        // The interpreter: a topological wave walk over the snapshotted node
        // descriptors. Determinism rules — node start order derives only from manifest
        // order plus recorded activity completions; no clock, no Guid, no environment
        // reads; variable rendering is pure over recorded results.
        var nodesById = nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var outputs = new Dictionary<string, NodeRunResult>(StringComparer.Ordinal);
        var pendingNodeTasks = new Dictionary<Task<NodeRunResult>, string>();
        var started = new HashSet<string>(StringComparer.Ordinal);
        var promptNodes = nodes.Where(node => node.Kind == WorkflowNodeKinds.Prompt).ToList();

        void StartReadyPromptNodes()
        {
            foreach (var node in promptNodes)
            {
                if (started.Contains(node.Id) || !NodeDependencies(node).All(outputs.ContainsKey))
                {
                    continue;
                }

                started.Add(node.Id);
                var variables = ConsultNodeVariableResolver.Resolve(node, request.ConsultDraft, nodesById, outputs);

                pendingNodeTasks[context.CallActivityAsync<NodeRunResult>(
                    ConsultGenerationActivityNames.RunPromptNode,
                    new ConsultPromptNodeActivityInput(
                        node.Id,
                        node.PromptId!,
                        variables,
                        input.WorkflowPackage,
                        node.OutputContract,
                        node.ConceptSource),
                    AgentActivityRetryOptions)] = node.Id;
            }
        }

        StartReadyPromptNodes();

        while (pendingNodeTasks.Count > 0)
        {
            var completedTask = await Task.WhenAny(pendingNodeTasks.Keys);
            var nodeId = pendingNodeTasks[completedTask];
            pendingNodeTasks.Remove(completedTask);
            var node = nodesById[nodeId];

            var result = await completedTask;

            if (node.FailIfEmpty != null && (result.Concepts?.Count ?? 0) == 0)
            {
                await FailNodeAsync(
                    context,
                    entityId,
                    node,
                    nodes,
                    outputs,
                    totalSectionCount,
                    completedSectionCount,
                    failedSectionCount,
                    logger);
                return;
            }

            outputs[nodeId] = result;
            completedNodeCount++;

            await context.Entities.CallEntityAsync(
                entityId,
                nameof(ConsultGenerationJobEntity.MarkNodeCompleted),
                new ConsultGenerationNodeUpdate(
                    node.Id,
                    node.Label,
                    result.Concepts,
                    result.InputHash,
                    result.OutputHash,
                    completedNodeCount,
                    totalNodeCount));

            context.SetCustomStatus(new
            {
                status = ConsultGenerationJobStatuses.Running,
                totalSectionCount,
                completedSectionCount,
                failedSectionCount,
                completedNodeCount,
                totalNodeCount
            });

            StartReadyPromptNodes();
        }

        if (started.Count < promptNodes.Count)
        {
            // Unreachable for validated packages (the graph is acyclic and complete);
            // defensive so a bad snapshot fails loud instead of hanging.
            throw new InvalidOperationException("Workflow nodes could not all be scheduled; the node graph is not executable.");
        }

        // The (single, terminal) map node runs after the prompt waves — a deliberate
        // scheduling conservatism the v4.0 closure permits; revisit if a package ever
        // wants a side analysis node concurrent with section generation.
        var mapNode = nodes.Single(node => node.Kind == WorkflowNodeKinds.Map);
        var mapConcepts = mapNode.ConceptsNodeId != null
            ? outputs[mapNode.ConceptsNodeId].Concepts ?? Array.Empty<ClinicalConcept>()
            : Array.Empty<ClinicalConcept>();

        await context.Entities.CallEntityAsync(
            entityId,
            nameof(ConsultGenerationJobEntity.MarkMapNodeStarted),
            new ConsultGenerationNodeUpdate(mapNode.Id, mapNode.Label, null, null, null, completedNodeCount, totalNodeCount));

        logger.LogInformation(
            "ConsultGenerationOrchestrator section generation started. JobId={JobId}, SectionCount={SectionCount}",
            context.InstanceId,
            totalSectionCount);

        var pendingTasks = new List<Task<SectionGenerationResult>>();
        var taskSections = new Dictionary<Task<SectionGenerationResult>, ConsultGenerationSectionRequest>();

        foreach (var section in request.Sections)
        {
            var task = GenerateSectionPipelineAsync(
                context,
                entityId,
                request.ConsultDraft,
                mapConcepts,
                section,
                input.WorkflowPackage,
                sectionSteps);

            pendingTasks.Add(task);
            taskSections[task] = section;
        }

        while (pendingTasks.Count > 0)
        {
            var completedTask = await Task.WhenAny(pendingTasks);
            pendingTasks.Remove(completedTask);
            var section = taskSections[completedTask];

            SectionGenerationResult result;

            try
            {
                result = await completedTask;
            }
            catch (Exception ex)
            {
                result = new SectionGenerationResult(section.Id, section.Name, false, null, ex.Message);
            }

            if (result.Success)
            {
                completedSectionCount++;
                await context.Entities.CallEntityAsync(
                    entityId,
                    nameof(ConsultGenerationJobEntity.CompleteSection),
                    result);
            }
            else
            {
                failedSectionCount++;
                await context.Entities.CallEntityAsync(
                    entityId,
                    nameof(ConsultGenerationJobEntity.FailSection),
                    result);
            }

            context.SetCustomStatus(new
            {
                status = ConsultGenerationJobStatuses.Running,
                totalSectionCount,
                completedSectionCount,
                failedSectionCount,
                completedNodeCount,
                totalNodeCount
            });
        }

        completedNodeCount++;

        var finalStatus = completedSectionCount > 0
            ? ConsultGenerationJobStatuses.Completed
            : ConsultGenerationJobStatuses.Failed;

        logger.LogInformation(
            "ConsultGenerationOrchestrator section generation completed. JobId={JobId}, Completed={CompletedSectionCount}, Failed={FailedSectionCount}, Total={TotalSectionCount}",
            context.InstanceId,
            completedSectionCount,
            failedSectionCount,
            totalSectionCount);

        await context.Entities.CallEntityAsync(
            entityId,
            nameof(ConsultGenerationJobEntity.FinalizeJob),
            new ConsultGenerationJobFinalize(finalStatus));

        logger.LogInformation(
            "ConsultGenerationOrchestrator finalized. JobId={JobId}, Status={Status}",
            context.InstanceId,
            finalStatus);

        context.SetCustomStatus(new
        {
            status = finalStatus,
            totalSectionCount,
            completedSectionCount,
            failedSectionCount,
            completedNodeCount,
            totalNodeCount
        });
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

    private static IEnumerable<string> NodeDependencies(ConsultNodeDescriptor node)
    {
        return (node.Bindings ?? new Dictionary<string, ConsultNodeBindingDescriptor>())
            .Values
            .Where(binding => binding.From.StartsWith(WorkflowNodeBindingSources.NodePrefix, StringComparison.Ordinal))
            .Select(binding => binding.From[WorkflowNodeBindingSources.NodePrefix.Length..]);
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

    private static async Task<SectionGenerationResult> GenerateSectionPipelineAsync(
        TaskOrchestrationContext context,
        EntityInstanceId entityId,
        string consultDraft,
        IReadOnlyList<ClinicalConcept> patientTrajectoryConcepts,
        ConsultGenerationSectionRequest section,
        string? workflowPackage,
        IReadOnlyList<ConsultSectionStepDescriptor> steps)
    {
        var currentStepLabel = steps[0].Label;

        try
        {
            string? previousStepOutput = null;

            for (var i = 0; i < steps.Count; i++)
            {
                currentStepLabel = steps[i].Label;

                previousStepOutput = await context.CallActivityAsync<string>(
                    ConsultGenerationActivityNames.RunProseStep,
                    new ConsultProseStepActivityInput(
                        steps[i].Id,
                        consultDraft,
                        patientTrajectoryConcepts,
                        section,
                        previousStepOutput,
                        workflowPackage),
                    AgentActivityRetryOptions);

                await context.Entities.CallEntityAsync(
                    entityId,
                    nameof(ConsultGenerationJobEntity.MarkSectionProseStep),
                    new ConsultGenerationSectionProseStepUpdate(
                        section.Id,
                        section.Name,
                        steps[i].Id,
                        i + 1));
            }

            return new SectionGenerationResult(section.Id, section.Name, true, previousStepOutput!.Trim(), null);
        }
        catch (Exception ex)
        {
            return new SectionGenerationResult(section.Id, section.Name, false, null, $"{currentStepLabel} failed: {ex.Message}");
        }
    }
}

/// <summary>
/// Resolves a prompt node's bindings to rendered variable values — pure functions over
/// the orchestration input and recorded activity results, so orchestrator-side use is
/// replay-safe. Renderer implementations stay where the byte-pinning tests point:
/// ConsultGenerationConceptFormatter and AgentSectionGenerator.FormatConcepts.
/// </summary>
internal static class ConsultNodeVariableResolver
{
    public static Dictionary<string, string> Resolve(
        ConsultNodeDescriptor node,
        string consultDraft,
        IReadOnlyDictionary<string, ConsultNodeDescriptor> nodesById,
        IReadOnlyDictionary<string, NodeRunResult> outputs)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (variable, binding) in node.Bindings ?? new Dictionary<string, ConsultNodeBindingDescriptor>())
        {
            variables[variable] = binding.From switch
            {
                WorkflowNodeBindingSources.InputConsultDraft => consultDraft,
                _ when binding.From.StartsWith(WorkflowNodeBindingSources.NodePrefix, StringComparison.Ordinal)
                    => RenderNodeOutput(node.Id, variable, binding, nodesById, outputs),
                _ => throw new InvalidOperationException(
                    $"Node '{node.Id}' binding '{variable}' has source '{binding.From}', which a prompt node cannot resolve.")
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
        IReadOnlyDictionary<string, ConsultNodeDescriptor> nodesById,
        IReadOnlyDictionary<string, NodeRunResult> outputs)
    {
        var targetId = binding.From[WorkflowNodeBindingSources.NodePrefix.Length..];
        var target = nodesById[targetId];
        var result = outputs[targetId];

        if (target.OutputContract is null)
        {
            return result.RawOutput;
        }

        return Render(binding.As ?? WorkflowConceptRenderers.ConceptBullets, result.Concepts
            ?? throw new InvalidOperationException($"Node '{nodeId}' binding '{variable}' targets '{targetId}', which recorded no concepts."));
    }
}

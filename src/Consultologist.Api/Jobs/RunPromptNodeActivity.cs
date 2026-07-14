using System.Diagnostics;
using Consultologist.Api.Agents;
using Consultologist.Api.Models;
using Consultologist.Api.Workflow;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Consultologist.Api.Jobs;

/// <summary>
/// Input of the generic prompt-node activity. The orchestrator resolves the bindings
/// to rendered variable values itself (pure functions over recorded activity results);
/// the activity re-resolves the pinned immutable package only for the template text.
/// </summary>
public sealed record ConsultPromptNodeActivityInput(
    string NodeId,
    string PromptId,
    Dictionary<string, string> Variables,
    string? WorkflowPackage = null,
    bool HasJsonOutput = false,
    string? ConceptSource = null);

/// <summary>
/// One node run. Deserialized concepts ride the recorded activity result so Durable
/// replay hands the orchestrator identical data without re-parsing; the hashes form
/// the per-node verification chain.
/// </summary>
public sealed record NodeRunResult(
    string RawOutput,
    IReadOnlyList<ClinicalConcept>? Concepts,
    string InputHash,
    string OutputHash);

/// <summary>
/// The generic DAG prompt node: renders the node's prompt with orchestrator-resolved
/// variables and sends it to the agent the node's output type selects — the
/// structured-output concept agent for schema-typed nodes (the schema is welded to
/// that agent's definition, package-format-v4.md), the prose agent for text nodes.
/// </summary>
public sealed class RunPromptNodeActivity
{
    private readonly IWorkflowPackageStore _packageStore;
    private readonly AgentSectionGenerator _agent;
    private readonly ILogger<RunPromptNodeActivity> _logger;

    public RunPromptNodeActivity(
        IWorkflowPackageStore packageStore,
        AgentSectionGenerator agent,
        ILogger<RunPromptNodeActivity> logger)
    {
        _packageStore = packageStore;
        _agent = agent;
        _logger = logger;
    }

    [Function(ConsultGenerationActivityNames.RunPromptNode)]
    public async Task<NodeRunResult> RunAsync(
        [ActivityTrigger] ConsultPromptNodeActivityInput input,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            _logger.LogInformation(
                "Starting prompt node. NodeId={NodeId}, PromptId={PromptId}, HasJsonOutput={HasJsonOutput}, Package={Package}",
                input.NodeId,
                input.PromptId,
                input.HasJsonOutput,
                input.WorkflowPackage);

            if (!WorkflowPackageRef.TryParse(input.WorkflowPackage, out var packageRef))
            {
                throw new InvalidOperationException(
                    $"Prompt node '{input.NodeId}' has no usable workflow package ref ('{input.WorkflowPackage}').");
            }

            var package = await _packageStore.ResolveAsync(packageRef!, cancellationToken);

            if (package.Prompts is null || !package.Prompts.TryGetValue(input.PromptId, out var prompt))
            {
                throw new InvalidOperationException(
                    $"Workflow package {package.Ref} has no prompt '{input.PromptId}' for node '{input.NodeId}'.");
            }

            var rendered = PromptTemplateRenderer.Render(prompt, input.Variables);
            var inputHash = ConsultGenerationProvenance.Sha256Hex(rendered);

            var rawOutput = await _agent.SendPromptAsync(
                input.NodeId,
                rendered,
                useConceptAgent: input.HasJsonOutput,
                cancellationToken);
            var outputHash = ConsultGenerationProvenance.Sha256Hex(rawOutput);

            var concepts = input.HasJsonOutput
                ? ConceptOutputContract.Deserialize(rawOutput, input.ConceptSource ?? input.NodeId)
                : null;

            _logger.LogInformation(
                "Prompt node completed. NodeId={NodeId}, ConceptCount={ConceptCount}, InputHash={InputHash}, OutputHash={OutputHash}, ElapsedMs={ElapsedMs}",
                input.NodeId,
                concepts?.Count,
                inputHash,
                outputHash,
                stopwatch.ElapsedMilliseconds);

            Console.Error.WriteLine(
                $"[PromptNode] NodeId={input.NodeId}; ConceptCount={concepts?.Count.ToString() ?? "-"}; InputHash={inputHash}; OutputHash={outputHash}; ElapsedMs={stopwatch.ElapsedMilliseconds}");

            return new NodeRunResult(rawOutput, concepts, inputHash, outputHash);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Prompt node failed. NodeId={NodeId}, PromptId={PromptId}, ExceptionType={ExceptionType}, Message={Message}, ElapsedMs={ElapsedMs}",
                input.NodeId,
                input.PromptId,
                ex.GetType().FullName,
                ex.Message,
                stopwatch.ElapsedMilliseconds);

            throw;
        }
    }
}

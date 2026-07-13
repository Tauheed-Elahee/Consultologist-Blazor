using System.Diagnostics;
using Consultologist.Api.Agents;
using Consultologist.Api.Workflow;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Consultologist.Api.Jobs;

/// <summary>
/// The generic per-section prose step: looks up the step in the job's pinned workflow
/// package, renders its prompt with the declared bindings, and sends it to the agent.
/// Replaces the three fixed prose activities (milestone 3,
/// docs/customizable-workflow/package-format-v3.md).
/// </summary>
public sealed class RunProseStepActivity
{
    private readonly IWorkflowPackageStore _packageStore;
    private readonly AgentSectionGenerator _agent;
    private readonly ILogger<RunProseStepActivity> _logger;

    public RunProseStepActivity(
        IWorkflowPackageStore packageStore,
        AgentSectionGenerator agent,
        ILogger<RunProseStepActivity> logger)
    {
        _packageStore = packageStore;
        _agent = agent;
        _logger = logger;
    }

    [Function(ConsultGenerationActivityNames.RunProseStep)]
    public async Task<string> RunAsync(
        [ActivityTrigger] ConsultProseStepActivityInput input,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var section = input.Section;

        try
        {
            _logger.LogInformation(
                "Starting prose step. StepId={StepId}, SectionId={SectionId}, SectionName={SectionName}, Package={Package}",
                input.StepId,
                section.Id,
                section.Name,
                input.WorkflowPackage);

            if (!WorkflowPackageRef.TryParse(input.WorkflowPackage, out var packageRef))
            {
                throw new InvalidOperationException(
                    $"Prose step '{input.StepId}' has no usable workflow package ref ('{input.WorkflowPackage}').");
            }

            var package = await _packageStore.ResolveAsync(packageRef!, cancellationToken);

            var step = package.SectionSteps?.FirstOrDefault(s => string.Equals(s.StepId, input.StepId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"Workflow package {package.Ref} has no section step '{input.StepId}'.");

            if (package.Prompts is null || !package.Prompts.TryGetValue(step.Prompt, out var prompt))
            {
                throw new InvalidOperationException(
                    $"Workflow package {package.Ref} has no prompt '{step.Prompt}' for section step '{input.StepId}'.");
            }

            var variables = ProseStepVariableBuilder.Build(step, input);
            var rendered = PromptTemplateRenderer.Render(prompt, variables);

            var prose = await _agent.SendPromptAsync($"{input.StepId}:{section.Name}", rendered, cancellationToken);
            var trimmedProse = prose.Trim();

            _logger.LogInformation(
                "Prose step completed. StepId={StepId}, SectionId={SectionId}, SectionName={SectionName}, ResponseLength={ResponseLength}, ElapsedMs={ElapsedMs}",
                input.StepId,
                section.Id,
                section.Name,
                trimmedProse.Length,
                stopwatch.ElapsedMilliseconds);

            return trimmedProse;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Prose step failed. StepId={StepId}, SectionId={SectionId}, SectionName={SectionName}, ExceptionType={ExceptionType}, Message={Message}, ElapsedMs={ElapsedMs}",
                input.StepId,
                section.Id,
                section.Name,
                ex.GetType().FullName,
                ex.Message,
                stopwatch.ElapsedMilliseconds);

            throw;
        }
    }
}

/// <summary>
/// Maps a section step's declared bindings to the engine-supplied values for one
/// activity invocation. Pure; the binding vocabulary is the engine-owned half of the
/// specVersion-3 contract.
/// </summary>
internal static class ProseStepVariableBuilder
{
    public static Dictionary<string, string> Build(WorkflowSectionStepSpec step, ConsultProseStepActivityInput input)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (variable, source) in step.Bindings)
        {
            variables[variable] = source switch
            {
                WorkflowStepBindingSources.ConsultDraft => input.ConsultDraft,
                WorkflowStepBindingSources.SectionName => input.Section.Name,
                WorkflowStepBindingSources.SectionStandard => input.Section.Standard,
                WorkflowStepBindingSources.PatientTrajectoryConcepts => AgentSectionGenerator.FormatConcepts(input.PatientTrajectoryConcepts),
                WorkflowStepBindingSources.PreviousStepOutput => input.PreviousStepOutput
                    ?? throw new InvalidOperationException(
                        $"Section step '{step.StepId}' binds '{variable}' to previous_step_output but no previous step output exists."),
                _ => throw new InvalidOperationException(
                    $"Section step '{step.StepId}' binds '{variable}' to unknown source '{source}'.")
            };
        }

        return variables;
    }
}

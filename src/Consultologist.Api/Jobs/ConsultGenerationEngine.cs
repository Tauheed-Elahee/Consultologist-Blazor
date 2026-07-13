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
                sectionSteps));

        await context.Entities.CallEntityAsync(entityId, nameof(ConsultGenerationJobEntity.MarkRunning));

        logger.LogInformation(
            "ConsultGenerationOrchestrator started. JobId={JobId}, AppUserId={AppUserId}, SectionCount={SectionCount}",
            context.InstanceId,
            input.AppUserId,
            request.Sections.Count);

        var totalSectionCount = request.Sections.Count;
        var completedSectionCount = 0;
        var failedSectionCount = 0;

        context.SetCustomStatus(new
        {
            status = ConsultGenerationJobStatuses.Running,
            totalSectionCount,
            completedSectionCount,
            failedSectionCount
        });

        await context.Entities.CallEntityAsync(
            entityId,
            nameof(ConsultGenerationJobEntity.MarkAnalysisStage),
            ConsultGenerationAnalysisUpdate.Stage(ConsultGenerationAnalysisStatuses.AnalysisStarted, 1));

        var patientConcepts = await context.CallActivityAsync<ConceptExtractionResult>(
            nameof(ExtractPatientConceptsActivity),
            new ConsultGenerationConceptActivityInput(request.ConsultDraft, input.WorkflowPackage),
            AgentActivityRetryOptions);

        if (patientConcepts.Concepts.Count == 0)
        {
            await FailPreprocessingAsync(
                context,
                entityId,
                ConsultGenerationAnalysisStatuses.ConceptExtractionFailed,
                "The consult could not be processed because clinical concepts could not be extracted from the draft.",
                totalSectionCount,
                completedSectionCount,
                failedSectionCount,
                logger);
            return;
        }

        await context.Entities.CallEntityAsync(
            entityId,
            nameof(ConsultGenerationJobEntity.MarkAnalysisStage),
            ConsultGenerationAnalysisUpdate.Stage(
                ConsultGenerationAnalysisStatuses.ConceptsExtracted,
                2,
                patientConcepts.Concepts,
                null,
                null,
                null,
                patientConcepts.ValidationWarnings));

        var problemContext = await context.CallActivityAsync<ConceptExtractionResult>(
            nameof(IdentifyProblemActivity),
            new ConsultGenerationProblemActivityInput(patientConcepts.Concepts, input.WorkflowPackage),
            AgentActivityRetryOptions);

        if (problemContext.Concepts.Count == 0)
        {
            await FailPreprocessingAsync(
                context,
                entityId,
                ConsultGenerationAnalysisStatuses.ProblemIdentificationFailed,
                "No valid disease or problem concept was identified.",
                totalSectionCount,
                completedSectionCount,
                failedSectionCount,
                logger);
            return;
        }

        await context.Entities.CallEntityAsync(
            entityId,
            nameof(ConsultGenerationJobEntity.MarkAnalysisStage),
            ConsultGenerationAnalysisUpdate.Stage(
                ConsultGenerationAnalysisStatuses.ProblemIdentified,
                3,
                null,
                problemContext.Concepts,
                null,
                null,
                problemContext.ValidationWarnings));

        var typicalTrajectory = await context.CallActivityAsync<ConceptExtractionResult>(
            nameof(CreateTypicalTrajectoryActivity),
            new ConsultGenerationTrajectoryActivityInput(problemContext.Concepts, patientConcepts.Concepts, Array.Empty<ClinicalConcept>(), input.WorkflowPackage),
            AgentActivityRetryOptions);

        if (typicalTrajectory.Concepts.Count == 0)
        {
            await FailPreprocessingAsync(
                context,
                entityId,
                ConsultGenerationAnalysisStatuses.TypicalTrajectoryFailed,
                "No valid typical trajectory concepts were generated.",
                totalSectionCount,
                completedSectionCount,
                failedSectionCount,
                logger);
            return;
        }

        await context.Entities.CallEntityAsync(
            entityId,
            nameof(ConsultGenerationJobEntity.MarkAnalysisStage),
            ConsultGenerationAnalysisUpdate.Stage(
                ConsultGenerationAnalysisStatuses.TypicalTrajectoryCreated,
                4,
                null,
                null,
                typicalTrajectory.Concepts,
                null,
                typicalTrajectory.ValidationWarnings));

        var patientTrajectory = await context.CallActivityAsync<ConceptExtractionResult>(
            nameof(CreatePatientTrajectoryActivity),
            new ConsultGenerationTrajectoryActivityInput(problemContext.Concepts, patientConcepts.Concepts, typicalTrajectory.Concepts, input.WorkflowPackage),
            AgentActivityRetryOptions);

        if (patientTrajectory.Concepts.Count == 0)
        {
            await FailPreprocessingAsync(
                context,
                entityId,
                ConsultGenerationAnalysisStatuses.PatientTrajectoryFailed,
                "No valid patient trajectory concepts were generated.",
                totalSectionCount,
                completedSectionCount,
                failedSectionCount,
                logger);
            return;
        }

        await context.Entities.CallEntityAsync(
            entityId,
            nameof(ConsultGenerationJobEntity.MarkAnalysisStage),
            ConsultGenerationAnalysisUpdate.Stage(
                ConsultGenerationAnalysisStatuses.PatientTrajectoryCreated,
                5,
                null,
                null,
                null,
                patientTrajectory.Concepts,
                patientTrajectory.ValidationWarnings));

        await context.Entities.CallEntityAsync(
            entityId,
            nameof(ConsultGenerationJobEntity.MarkSectionGenerationStarted),
            ConsultGenerationAnalysisUpdate.Stage(ConsultGenerationAnalysisStatuses.SectionGenerationStarted, 6));

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
                patientTrajectory.Concepts,
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
                failedSectionCount
            });
        }

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
            failedSectionCount
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

    private static async Task FailPreprocessingAsync(
        TaskOrchestrationContext context,
        EntityInstanceId entityId,
        string analysisStatus,
        string analysisError,
        int totalSectionCount,
        int completedSectionCount,
        int failedSectionCount,
        ILogger logger)
    {
        logger.LogWarning(
            "Consult generation preprocessing failed. JobId={JobId}, Stage={Stage}, Reason={Reason}",
            context.InstanceId,
            analysisStatus,
            analysisError);

        await context.Entities.CallEntityAsync(
            entityId,
            nameof(ConsultGenerationJobEntity.MarkAnalysisFailed),
            ConsultGenerationAnalysisUpdate.Failure(analysisStatus, analysisError));

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

public sealed class ExtractPatientConceptsActivity
{
    private readonly AgentSectionGenerator _agent;
    private readonly IWorkflowPromptProvider _promptProvider;
    private readonly ILogger<ExtractPatientConceptsActivity> _logger;

    public ExtractPatientConceptsActivity(AgentSectionGenerator agent, IWorkflowPromptProvider promptProvider, ILogger<ExtractPatientConceptsActivity> logger)
    {
        _agent = agent;
        _promptProvider = promptProvider;
        _logger = logger;
    }

    [Function(nameof(ExtractPatientConceptsActivity))]
    public async Task<ConceptExtractionResult> RunAsync(
        [ActivityTrigger] ConsultGenerationConceptActivityInput input,
        CancellationToken cancellationToken)
    {
        var prompt = await _promptProvider.RenderAsync(
            input.WorkflowPackage,
            WorkflowPromptContract.ExtractPatientConcepts,
            new Dictionary<string, string> { [WorkflowPromptContract.ConsultDraft] = input.ConsultDraft },
            cancellationToken);

        return await ConsultGenerationPreprocessingRunner.RunConceptPromptAsync(_agent, _logger, ConsultGenerationAnalysisStatuses.ConceptsExtracted, "patient", prompt, cancellationToken);
    }
}

public sealed class IdentifyProblemActivity
{
    private readonly AgentSectionGenerator _agent;
    private readonly IWorkflowPromptProvider _promptProvider;
    private readonly ILogger<IdentifyProblemActivity> _logger;

    public IdentifyProblemActivity(AgentSectionGenerator agent, IWorkflowPromptProvider promptProvider, ILogger<IdentifyProblemActivity> logger)
    {
        _agent = agent;
        _promptProvider = promptProvider;
        _logger = logger;
    }

    [Function(nameof(IdentifyProblemActivity))]
    public async Task<ConceptExtractionResult> RunAsync(
        [ActivityTrigger] ConsultGenerationProblemActivityInput input,
        CancellationToken cancellationToken)
    {
        var prompt = await _promptProvider.RenderAsync(
            input.WorkflowPackage,
            WorkflowPromptContract.IdentifyProblem,
            new Dictionary<string, string>
            {
                [WorkflowPromptContract.PatientConcepts] = ConsultGenerationConceptFormatter.Format(input.PatientConcepts)
            },
            cancellationToken);

        return await ConsultGenerationPreprocessingRunner.RunConceptPromptAsync(_agent, _logger, ConsultGenerationAnalysisStatuses.ProblemIdentified, "problem", prompt, cancellationToken);
    }
}

public sealed class CreateTypicalTrajectoryActivity
{
    private readonly AgentSectionGenerator _agent;
    private readonly IWorkflowPromptProvider _promptProvider;
    private readonly ILogger<CreateTypicalTrajectoryActivity> _logger;

    public CreateTypicalTrajectoryActivity(AgentSectionGenerator agent, IWorkflowPromptProvider promptProvider, ILogger<CreateTypicalTrajectoryActivity> logger)
    {
        _agent = agent;
        _promptProvider = promptProvider;
        _logger = logger;
    }

    [Function(nameof(CreateTypicalTrajectoryActivity))]
    public async Task<ConceptExtractionResult> RunAsync(
        [ActivityTrigger] ConsultGenerationTrajectoryActivityInput input,
        CancellationToken cancellationToken)
    {
        var prompt = await _promptProvider.RenderAsync(
            input.WorkflowPackage,
            WorkflowPromptContract.CreateTypicalTrajectory,
            new Dictionary<string, string>
            {
                [WorkflowPromptContract.ProblemConcepts] = ConsultGenerationConceptFormatter.Format(input.ProblemContext)
            },
            cancellationToken);

        return await ConsultGenerationPreprocessingRunner.RunConceptPromptAsync(_agent, _logger, ConsultGenerationAnalysisStatuses.TypicalTrajectoryCreated, "typical-trajectory", prompt, cancellationToken);
    }
}

public sealed class CreatePatientTrajectoryActivity
{
    private readonly AgentSectionGenerator _agent;
    private readonly IWorkflowPromptProvider _promptProvider;
    private readonly ILogger<CreatePatientTrajectoryActivity> _logger;

    public CreatePatientTrajectoryActivity(AgentSectionGenerator agent, IWorkflowPromptProvider promptProvider, ILogger<CreatePatientTrajectoryActivity> logger)
    {
        _agent = agent;
        _promptProvider = promptProvider;
        _logger = logger;
    }

    [Function(nameof(CreatePatientTrajectoryActivity))]
    public async Task<ConceptExtractionResult> RunAsync(
        [ActivityTrigger] ConsultGenerationTrajectoryActivityInput input,
        CancellationToken cancellationToken)
    {
        var prompt = await _promptProvider.RenderAsync(
            input.WorkflowPackage,
            WorkflowPromptContract.CreatePatientTrajectory,
            new Dictionary<string, string>
            {
                [WorkflowPromptContract.ProblemConcepts] = ConsultGenerationConceptFormatter.Format(input.ProblemContext),
                [WorkflowPromptContract.PatientConcepts] = ConsultGenerationConceptFormatter.Format(input.PatientConcepts),
                [WorkflowPromptContract.TypicalTrajectoryConcepts] = ConsultGenerationConceptFormatter.Format(input.TypicalTrajectoryConcepts)
            },
            cancellationToken);

        return await ConsultGenerationPreprocessingRunner.RunConceptPromptAsync(_agent, _logger, ConsultGenerationAnalysisStatuses.PatientTrajectoryCreated, "patient-trajectory", prompt, cancellationToken);
    }
}

public sealed record ConsultGenerationConceptActivityInput(string ConsultDraft, string? WorkflowPackage = null);

public sealed record ConsultGenerationProblemActivityInput(IReadOnlyList<ClinicalConcept> PatientConcepts, string? WorkflowPackage = null);

public sealed record ConsultGenerationTrajectoryActivityInput(
    IReadOnlyList<ClinicalConcept> ProblemContext,
    IReadOnlyList<ClinicalConcept> PatientConcepts,
    IReadOnlyList<ClinicalConcept> TypicalTrajectoryConcepts,
    string? WorkflowPackage = null);

public sealed record ConceptExtractionResult(
    IReadOnlyList<ClinicalConcept> Concepts,
    IReadOnlyList<ConsultGenerationValidationWarning> ValidationWarnings);

public static class ConsultGenerationPreprocessingRunner
{
    public static async Task<ConceptExtractionResult> RunConceptPromptAsync(
        AgentSectionGenerator agent,
        ILogger logger,
        string warningStage,
        string source,
        string prompt,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var rawResponse = await agent.SendPromptAsync(warningStage, prompt, cancellationToken);
        var result = ConsultGenerationConceptParser.Parse(rawResponse, source, warningStage);

        logger.LogInformation(
            "SNOMED preprocessing completed. Stage={Stage}, ValidConceptCount={ValidConceptCount}, DroppedMalformedConceptCount={DroppedMalformedConceptCount}, ElapsedMs={ElapsedMs}",
            warningStage,
            result.Concepts.Count,
            result.ValidationWarnings.Sum(warning => warning.DroppedLineCount),
            stopwatch.ElapsedMilliseconds);

        Console.Error.WriteLine(
            $"[SNOMEDPreprocessing] Stage={warningStage}; ValidationWarnings={JsonSerializer.Serialize(result.ValidationWarnings)}; ElapsedMs={stopwatch.ElapsedMilliseconds}");

        return result;
    }
}

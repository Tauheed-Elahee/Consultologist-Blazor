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

public sealed class ConsultGenerationJobs
{
    private const string LastEventIdHeaderName = "Last-Event-ID";
    private const string MissingSseAttemptId = "missing";
    private const string InvalidSseAttemptId = "invalid";
    private const string SseExitReasonCompleted = "Completed";
    private const string SseExitReasonTerminalFailure = "TerminalFailure";
    private const string SseExitReasonTerminalInitialState = "TerminalInitialState";
    private const string SseExitReasonRequestAborted = "RequestAborted";
    private const string SseExitReasonServerTimeout = "ServerTimeout";
    private const string SseExitReasonServerError = "ServerError";
    private const string SseExitReasonChannelCompleted = "ChannelCompleted";

    private static readonly TimeSpan SsePollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SseHeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SseStreamTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SseInitialJobResponsePollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SseInitialJobResponseTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<ConsultGenerationJobs> _logger;
    private readonly IAccountAuthorizer _authorizer;
    private readonly IConsultGenerationJobEventStore _eventStore;

    public ConsultGenerationJobs(
        ILogger<ConsultGenerationJobs> logger,
        IAccountAuthorizer authorizer,
        IConsultGenerationJobEventStore eventStore)
    {
        _logger = logger;
        _authorizer = authorizer;
        _eventStore = eventStore;
    }

    [Function("StartConsultGenerationJob")]
    public async Task<HttpResponseData> StartAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", "options", Route = "ConsultGenerationJobs")] HttpRequestData req,
        [DurableClient] DurableTaskClient client)
    {
        var cancellationToken = req.FunctionContext.CancellationToken;

        _logger.LogInformation(
            "StartConsultGenerationJob entered. InvocationId={InvocationId}, Method={Method}, Url={Url}",
            req.FunctionContext.InvocationId,
            req.Method,
            req.Url);

        if (IsOptions(req))
        {
            _logger.LogInformation(
                "StartConsultGenerationJob returning OPTIONS response. InvocationId={InvocationId}",
                req.FunctionContext.InvocationId);

            return CreateEmptyResponse(req, HttpStatusCode.OK);
        }

        var account = await _authorizer.AuthorizeAsync(req, cancellationToken);

        if (account == null)
        {
            return AccountAuthorizer.CreateUnauthorizedResponse(req);
        }

        if (!AccountAuthorizer.IsActive(account))
        {
            return AccountAuthorizer.CreateForbiddenResponse(req);
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            _logger.LogInformation(
                "StartConsultGenerationJob reading request body. InvocationId={InvocationId}",
                req.FunctionContext.InvocationId);

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync(cancellationToken);

            _logger.LogInformation(
                "StartConsultGenerationJob request body read. InvocationId={InvocationId}, BodyLength={BodyLength}",
                req.FunctionContext.InvocationId,
                requestBody.Length);

            ConsultGenerationRequest? generationRequest = null;

            if (!string.IsNullOrWhiteSpace(requestBody))
            {
                try
                {
                    _logger.LogInformation(
                        "StartConsultGenerationJob deserializing request body. InvocationId={InvocationId}",
                        req.FunctionContext.InvocationId);

                    generationRequest = JsonSerializer.Deserialize<ConsultGenerationRequest>(requestBody, JsonOptions);
                }
                catch (JsonException ex)
                {
                    const string malformedJsonError = "Malformed JSON request body.";

                    _logger.LogWarning(
                        ex,
                        "Invalid ConsultGenerationJobs request: {ValidationError}",
                        malformedJsonError);

                    return await CreateJsonResponseAsync(req, HttpStatusCode.BadRequest, new { error = malformedJsonError }, cancellationToken);
                }
            }

            var validationError = ValidateRequest(generationRequest);

            if (validationError != null)
            {
                _logger.LogWarning("Invalid ConsultGenerationJobs request: {ValidationError}", validationError);
                return await CreateJsonResponseAsync(req, HttpStatusCode.BadRequest, new { error = validationError }, cancellationToken);
            }

            var request = generationRequest!;
            var jobId = Guid.NewGuid().ToString("N");
            var entityId = new EntityInstanceId(nameof(ConsultGenerationJobEntity), jobId);

            // Provenance: identify the artifacts and input that produce this consult.
            // See docs/customizable-workflow/provenance.md.
            var effectiveInputHash = ConsultGenerationProvenance.ComputeEffectiveInputHash(request);
            var agentVersion = Environment.GetEnvironmentVariable("AzureAI__AgentVersion");

            _logger.LogInformation(
                "StartConsultGenerationJob signaling job entity. InvocationId={InvocationId}, JobId={JobId}",
                req.FunctionContext.InvocationId,
                jobId);

            await client.Entities.SignalEntityAsync(
                entityId,
                nameof(ConsultGenerationJobEntity.Initialize),
                new ConsultGenerationJobInitialize(
                    jobId,
                    account.AppUserId,
                    request.Sections,
                    request.WorkflowPackage,
                    effectiveInputHash,
                    agentVersion));

            _logger.LogInformation(
                "StartConsultGenerationJob scheduling orchestration. InvocationId={InvocationId}, JobId={JobId}",
                req.FunctionContext.InvocationId,
                jobId);

            var instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
                nameof(ConsultGenerationOrchestrator),
                new ConsultGenerationOrchestrationInput(
                    request,
                    account.AppUserId,
                    request.WorkflowPackage,
                    effectiveInputHash,
                    agentVersion),
                new StartOrchestrationOptions { InstanceId = jobId },
                cancellationToken);

            var statusUrl = BuildStatusUrl(req, instanceId);

            _logger.LogInformation(
                "Consult generation job started. JobId={JobId}, SectionCount={SectionCount}, ElapsedMs={ElapsedMs}",
                instanceId,
                request.Sections.Count,
                stopwatch.ElapsedMilliseconds);

            return await CreateJsonResponseAsync(req, HttpStatusCode.Accepted, new ConsultGenerationJobStartResponse(instanceId, statusUrl), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error starting consult generation job. ExceptionType={ExceptionType}, Message={Message}, ElapsedMs={ElapsedMs}",
                ex.GetType().FullName,
                ex.Message,
                stopwatch.ElapsedMilliseconds);

            return await CreateJsonResponseAsync(req, HttpStatusCode.InternalServerError, new { error = $"Internal error: {ex.Message}" }, cancellationToken);
        }
    }

    [Function("GetConsultGenerationJob")]
    public async Task<HttpResponseData> GetAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "options", Route = "ConsultGenerationJobs/{jobId}")] HttpRequestData req,
        [DurableClient] DurableTaskClient client,
        string jobId)
    {
        var cancellationToken = req.FunctionContext.CancellationToken;

        _logger.LogInformation(
            "GetConsultGenerationJob entered. InvocationId={InvocationId}, Method={Method}, Url={Url}, JobId={JobId}",
            req.FunctionContext.InvocationId,
            req.Method,
            req.Url,
            jobId);

        if (IsOptions(req))
        {
            _logger.LogInformation(
                "GetConsultGenerationJob returning OPTIONS response. InvocationId={InvocationId}, JobId={JobId}",
                req.FunctionContext.InvocationId,
                jobId);

            return CreateEmptyResponse(req, HttpStatusCode.OK);
        }

        if (string.IsNullOrWhiteSpace(jobId))
        {
            return await CreateJsonResponseAsync(req, HttpStatusCode.BadRequest, new { error = "JobId is required." }, cancellationToken);
        }

        var account = await _authorizer.AuthorizeAsync(req, cancellationToken);

        if (account == null)
        {
            return AccountAuthorizer.CreateUnauthorizedResponse(req);
        }

        if (!AccountAuthorizer.IsActive(account))
        {
            return AccountAuthorizer.CreateForbiddenResponse(req);
        }

        var response = await GetJobResponseAsync(client, jobId, account.AppUserId, cancellationToken);

        if (response == null)
        {
            return await CreateJsonResponseAsync(req, HttpStatusCode.NotFound, new { error = "Consult generation job was not found." }, cancellationToken);
        }

        await TryMaterializeEventsForPollingAsync(response, cancellationToken);

        return await CreateJsonResponseAsync(req, HttpStatusCode.OK, response, cancellationToken);
    }

    [Function("GetConsultGenerationJobEvents")]
    public async Task<IActionResult> GetEventsAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "options", Route = "ConsultGenerationJobs/{jobId}/events")] HttpRequest req,
        [DurableClient] DurableTaskClient client,
        string jobId)
    {
        var cancellationToken = req.HttpContext.RequestAborted;
        var attemptId = GetSseAttemptId(req);

        _logger.LogInformation(
            "GetConsultGenerationJobEvents entered. Method={Method}, Path={Path}, JobId={JobId}, AttemptId={AttemptId}",
            req.Method,
            req.Path,
            jobId,
            attemptId);

        if (IsOptions(req))
        {
            _logger.LogInformation(
                "GetConsultGenerationJobEvents returning OPTIONS response. JobId={JobId}",
                jobId);

            FunctionCors.Apply(req, req.HttpContext.Response);
            return new OkResult();
        }

        if (string.IsNullOrWhiteSpace(jobId))
        {
            FunctionCors.Apply(req, req.HttpContext.Response);
            return new BadRequestObjectResult(new { error = "JobId is required." });
        }

        var account = await _authorizer.AuthorizeAsync(req, cancellationToken);

        if (account == null)
        {
            FunctionCors.Apply(req, req.HttpContext.Response);
            req.HttpContext.Response.Headers.WWWAuthenticate = "Bearer";
            return new UnauthorizedResult();
        }

        if (!AccountAuthorizer.IsActive(account))
        {
            FunctionCors.Apply(req, req.HttpContext.Response);
            return new ForbidResult();
        }

        if (!TryGetResumeAfterSequence(req, jobId, out var resumeAfterSequence, out var lastEventIdError))
        {
            FunctionCors.Apply(req, req.HttpContext.Response);
            return new BadRequestObjectResult(new { error = lastEventIdError });
        }

        var initialResponse = await WaitForInitialJobResponseAsync(
            client,
            jobId,
            account.AppUserId,
            cancellationToken);

        if (initialResponse == null)
        {
            FunctionCors.Apply(req, req.HttpContext.Response);
            return new NotFoundObjectResult(new { error = "Consult generation job was not found." });
        }

        var events = CreateConsultGenerationJobEventsAsync(
            client,
            jobId,
            account.AppUserId,
            attemptId,
            resumeAfterSequence,
            initialResponse,
            cancellationToken);

        return new CorsResultActionResult(TypedResults.ServerSentEvents(events));
    }

    private IAsyncEnumerable<SseItem<string>> CreateConsultGenerationJobEventsAsync(
        DurableTaskClient client,
        string jobId,
        string appUserId,
        string attemptId,
        long resumeAfterSequence,
        ConsultGenerationJobResponse initialResponse,
        CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<SseItem<string>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        _ = WriteConsultGenerationJobEventsAsync(
            client,
            jobId,
            appUserId,
            attemptId,
            resumeAfterSequence,
            initialResponse,
            channel.Writer,
            cancellationToken);

        return channel.Reader.ReadAllAsync(cancellationToken);
    }

    private async Task WriteConsultGenerationJobEventsAsync(
        DurableTaskClient client,
        string jobId,
        string appUserId,
        string attemptId,
        long resumeAfterSequence,
        ConsultGenerationJobResponse initialResponse,
        ChannelWriter<SseItem<string>> writer,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var initialEventCount = 0;
        var replayEventCount = 0;
        var liveEventCount = 0;
        var heartbeatCount = 0;
        var highestEmittedSequence = resumeAfterSequence;
        var latestEventId = (string?)null;
        var latestEventType = (string?)null;
        var terminalStatus = (string?)null;
        var latestStatus = initialResponse.Status;
        var exitReason = SseExitReasonChannelCompleted;
        var serverErrorType = (string?)null;

        try
        {
            _logger.LogInformation(
                "Consult generation SSE stream connected. JobId={JobId}, AppUserId={AppUserId}, AttemptId={AttemptId}, ResumeAfterSequence={ResumeAfterSequence}",
                jobId,
                appUserId,
                attemptId,
                resumeAfterSequence);

            if (resumeAfterSequence > 0)
            {
                var replayWriteResult = await WriteReplayedEventsAsync(
                    writer,
                    jobId,
                    appUserId,
                    resumeAfterSequence,
                    cancellationToken);
                highestEmittedSequence = replayWriteResult.HighestEmittedSequence;
                replayEventCount += replayWriteResult.EventCount;
                latestEventId = replayWriteResult.LatestEventId ?? latestEventId;
                latestEventType = replayWriteResult.LatestEventType ?? latestEventType;

                _logger.LogInformation(
                    "Consult generation SSE replay events sent. JobId={JobId}, AppUserId={AppUserId}, AttemptId={AttemptId}, ResumeAfterSequence={ResumeAfterSequence}, ReplayEventCount={ReplayEventCount}, HighestEmittedSequence={HighestEmittedSequence}",
                    jobId,
                    appUserId,
                    attemptId,
                    resumeAfterSequence,
                    replayEventCount,
                    highestEmittedSequence);
            }

            var initialWriteResult = await WriteMaterializedEventsAsync(
                writer,
                initialResponse,
                highestEmittedSequence,
                cancellationToken);
            highestEmittedSequence = initialWriteResult.HighestEmittedSequence;
            initialEventCount += initialWriteResult.EventCount;
            latestEventId = initialWriteResult.LatestEventId ?? latestEventId;
            latestEventType = initialWriteResult.LatestEventType ?? latestEventType;

            _logger.LogInformation(
                "Consult generation SSE initial events sent. JobId={JobId}, AppUserId={AppUserId}, AttemptId={AttemptId}, Status={Status}, TotalCount={TotalCount}, CompletedCount={CompletedCount}, FailedCount={FailedCount}, InitialEventCount={InitialEventCount}",
                jobId,
                appUserId,
                attemptId,
                initialResponse.Status,
                initialResponse.TotalSectionCount,
                initialResponse.CompletedSectionCount,
                initialResponse.FailedSectionCount,
                initialEventCount);

            if (IsTerminalJobStatus(initialResponse.Status))
            {
                terminalStatus = initialResponse.Status;
                exitReason = initialResponse.Status == ConsultGenerationJobStatuses.Failed
                    ? SseExitReasonTerminalFailure
                    : SseExitReasonTerminalInitialState;
                return;
            }

            var streamStartedAt = DateTimeOffset.UtcNow;
            var nextHeartbeatAt = streamStartedAt + SseHeartbeatInterval;

            while (!cancellationToken.IsCancellationRequested
                && DateTimeOffset.UtcNow - streamStartedAt < SseStreamTimeout)
            {
                await Task.Delay(SsePollInterval, cancellationToken);

                var latestResponse = await GetJobResponseAsync(client, jobId, appUserId, cancellationToken);

                if (latestResponse == null)
                {
                    throw new InvalidOperationException("Consult generation job was not found.");
                }

                latestStatus = latestResponse.Status;

                var liveWriteResult = await WriteMaterializedEventsAsync(
                    writer,
                    latestResponse,
                    highestEmittedSequence,
                    cancellationToken);
                highestEmittedSequence = liveWriteResult.HighestEmittedSequence;
                liveEventCount += liveWriteResult.EventCount;
                latestEventId = liveWriteResult.LatestEventId ?? latestEventId;
                latestEventType = liveWriteResult.LatestEventType ?? latestEventType;

                if (IsTerminalJobStatus(latestResponse.Status))
                {
                    terminalStatus = latestResponse.Status;
                    exitReason = latestResponse.Status == ConsultGenerationJobStatuses.Failed
                        ? SseExitReasonTerminalFailure
                        : SseExitReasonCompleted;
                    return;
                }

                if (DateTimeOffset.UtcNow >= nextHeartbeatAt)
                {
                    var heartbeat = CreateSseItem(
                        "heartbeat",
                        new ConsultGenerationJobHeartbeatEvent(jobId, latestResponse.Status));
                    await writer.WriteAsync(heartbeat, cancellationToken);
                    heartbeatCount++;
                    latestEventId = heartbeat.EventId ?? latestEventId;
                    latestEventType = heartbeat.EventType;

                    nextHeartbeatAt = DateTimeOffset.UtcNow + SseHeartbeatInterval;
                }
            }

            exitReason = SseExitReasonServerTimeout;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            exitReason = SseExitReasonRequestAborted;
        }
        catch (ChannelClosedException)
        {
            exitReason = SseExitReasonChannelCompleted;
        }
        catch (Exception ex)
        {
            exitReason = SseExitReasonServerError;
            serverErrorType = ex.GetType().FullName;
            writer.TryComplete(ex);
            return;
        }
        finally
        {
            var logLevel = exitReason is SseExitReasonTerminalFailure
                or SseExitReasonServerTimeout
                or SseExitReasonServerError
                ? LogLevel.Warning
                : LogLevel.Information;

            _logger.Log(
                logLevel,
                "Consult generation SSE stream exited. JobId={JobId}, AppUserId={AppUserId}, AttemptId={AttemptId}, ExitReason={ExitReason}, ElapsedMs={ElapsedMs}, ResumeAfterSequence={ResumeAfterSequence}, ReplayEventCount={ReplayEventCount}, InitialEventCount={InitialEventCount}, LiveEventCount={LiveEventCount}, HeartbeatCount={HeartbeatCount}, LatestEventId={LatestEventId}, LatestEventType={LatestEventType}, TerminalStatus={TerminalStatus}, LatestStatus={LatestStatus}, ServerErrorType={ServerErrorType}",
                jobId,
                appUserId,
                attemptId,
                exitReason,
                stopwatch.ElapsedMilliseconds,
                resumeAfterSequence,
                replayEventCount,
                initialEventCount,
                liveEventCount,
                heartbeatCount,
                latestEventId,
                latestEventType,
                terminalStatus,
                latestStatus,
                serverErrorType);

            writer.TryComplete();
        }
    }

    private static string BuildStatusUrl(HttpRequestData req, string jobId)
    {
        var authority = req.Url.GetLeftPart(UriPartial.Authority);
        return $"{authority}/api/ConsultGenerationJobs/{jobId}";
    }

    private static string? ValidateRequest(ConsultGenerationRequest? request)
    {
        if (request == null)
        {
            return "Request body is required.";
        }

        if (string.IsNullOrWhiteSpace(request.ConsultDraft))
        {
            return "ConsultDraft is required.";
        }

        if (request.Sections == null || request.Sections.Count == 0)
        {
            return "At least one section is required.";
        }

        foreach (var section in request.Sections)
        {
            if (string.IsNullOrWhiteSpace(section.Id)
                || string.IsNullOrWhiteSpace(section.Name))
            {
                return "Each section requires Id and Name.";
            }
        }

        if (request.Sections
            .GroupBy(section => section.Id, StringComparer.Ordinal)
            .Any(group => group.Count() > 1))
        {
            return "Duplicate section IDs are not allowed.";
        }

        return null;
    }

    private static async Task<ConsultGenerationJobResponse?> GetJobResponseAsync(
        DurableTaskClient client,
        string jobId,
        string appUserId,
        CancellationToken cancellationToken)
    {
        var entityBackedResponse = await GetEntityBackedJobResponseAsync(client, jobId, cancellationToken);
        var instance = await client.GetInstancesAsync(jobId, getInputsAndOutputs: false, cancellationToken);

        if (instance?.RuntimeStatus == OrchestrationRuntimeStatus.Failed)
        {
            instance = await client.GetInstancesAsync(jobId, getInputsAndOutputs: true, cancellationToken)
                ?? instance;
        }

        if (entityBackedResponse != null)
        {
            if (!string.Equals(entityBackedResponse.AppUserId, appUserId, StringComparison.Ordinal))
            {
                return null;
            }

            return MergeEntityAndRuntimeStatus(
                entityBackedResponse,
                instance);
        }

        if (instance == null)
        {
            return null;
        }

        var runtimeFailure = GetSanitizedRuntimeFailure(instance);

        return new ConsultGenerationJobResponse(
            jobId,
            appUserId,
            MapRuntimeStatus(instance.RuntimeStatus),
            0,
            0,
            0,
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            false,
            RuntimeFailureStage: runtimeFailure?.Stage,
            RuntimeFailureError: runtimeFailure?.Error);
    }

    private static async Task<ConsultGenerationJobResponse?> WaitForInitialJobResponseAsync(
        DurableTaskClient client,
        string jobId,
        string appUserId,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < SseInitialJobResponseTimeout)
        {
            var response = await GetJobResponseAsync(client, jobId, appUserId, cancellationToken);

            if (response != null)
            {
                return response;
            }

            await Task.Delay(SseInitialJobResponsePollInterval, cancellationToken);
        }

        return await GetJobResponseAsync(client, jobId, appUserId, cancellationToken);
    }

    private static async Task<ConsultGenerationJobResponse?> GetEntityBackedJobResponseAsync(
        DurableTaskClient client,
        string jobId,
        CancellationToken cancellationToken)
    {
        var entityId = new EntityInstanceId(nameof(ConsultGenerationJobEntity), jobId);
        var entity = await client.Entities.GetEntityAsync<ConsultGenerationJobState>(
            entityId,
            cancellation: cancellationToken);

        return entity?.State?.ToResponse();
    }

    private static ConsultGenerationJobResponse MergeEntityAndRuntimeStatus(
        ConsultGenerationJobResponse response,
        OrchestrationMetadata? instance)
    {
        if (instance == null
            || IsTerminalJobStatus(response.Status)
            || !IsTerminalRuntimeStatus(instance.RuntimeStatus))
        {
            return response;
        }

        var runtimeFailure = GetSanitizedRuntimeFailure(instance);

        return response with
        {
            Status = MapRuntimeStatus(instance.RuntimeStatus),
            RuntimeFailureStage = runtimeFailure?.Stage,
            RuntimeFailureError = runtimeFailure?.Error,
            History = AugmentHistoryForRuntimeFailure(response, runtimeFailure)
        };
    }

    private static IReadOnlyList<JobHistoryEvent>? AugmentHistoryForRuntimeFailure(
        ConsultGenerationJobResponse response,
        ConsultGenerationRuntimeFailure? runtimeFailure)
    {
        if (response.History == null || response.History.Count == 0)
        {
            return response.History;
        }

        var additional = new List<JobHistoryEvent>
        {
            new("failure", "Failed", runtimeFailure?.Error, DateTimeOffset.UtcNow)
        };

        var finishedIds = response.GeneratedSections.Keys
            .Concat(response.FailedSections.Keys)
            .ToHashSet();

        if (response.SectionProseProgress != null)
        {
            foreach (var (_, progress) in response.SectionProseProgress
                .Where(p => !finishedIds.Contains(p.Key))
                .OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var name = !string.IsNullOrWhiteSpace(progress.SectionName) ? progress.SectionName : progress.SectionId;
                additional.Add(new JobHistoryEvent("skipped", $"Section not reached: {name}", null, DateTimeOffset.UtcNow));
            }
        }

        return [.. response.History, .. additional];
    }

    private static bool IsTerminalJobStatus(string status)
    {
        return status is ConsultGenerationJobStatuses.Completed or ConsultGenerationJobStatuses.Failed;
    }

    private static bool IsTerminalRuntimeStatus(OrchestrationRuntimeStatus status)
    {
        if (status.ToString().Equals("Canceled", StringComparison.Ordinal))
        {
            return true;
        }

        return status is OrchestrationRuntimeStatus.Completed
            or OrchestrationRuntimeStatus.Failed
            or OrchestrationRuntimeStatus.Terminated;
    }

    private static string MapRuntimeStatus(OrchestrationRuntimeStatus runtimeStatus)
    {
        if (runtimeStatus.ToString().Equals("Canceled", StringComparison.Ordinal))
        {
            return ConsultGenerationJobStatuses.Failed;
        }

        return runtimeStatus switch
        {
            OrchestrationRuntimeStatus.Completed => ConsultGenerationJobStatuses.Completed,
            OrchestrationRuntimeStatus.Failed => ConsultGenerationJobStatuses.Failed,
            OrchestrationRuntimeStatus.Terminated => ConsultGenerationJobStatuses.Failed,
            OrchestrationRuntimeStatus.Pending => ConsultGenerationJobStatuses.Queued,
            _ => ConsultGenerationJobStatuses.Running
        };
    }

    private static bool IsOptions(HttpRequestData req)
    {
        return string.Equals(req.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOptions(HttpRequest req)
    {
        return HttpMethods.IsOptions(req.Method);
    }

    private static string GetSseAttemptId(HttpRequest req)
    {
        var rawAttemptId = req.Query["attemptId"].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(rawAttemptId))
        {
            return MissingSseAttemptId;
        }

        return Guid.TryParse(rawAttemptId.Trim(), out var attemptId)
            ? attemptId.ToString("D")
            : InvalidSseAttemptId;
    }

    private static bool TryGetResumeAfterSequence(
        HttpRequest req,
        string jobId,
        out long resumeAfterSequence,
        out string? error)
    {
        resumeAfterSequence = 0;
        error = null;

        if (!req.Headers.TryGetValue(LastEventIdHeaderName, out var headerValues))
        {
            return true;
        }

        var values = new List<string>();

        foreach (var value in headerValues)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(value.Trim());
            }
        }

        if (values.Count == 0)
        {
            return true;
        }

        if (values.Count != 1)
        {
            error = "Invalid Last-Event-ID header.";
            return false;
        }

        var lastEventId = values[0];
        var separatorIndex = lastEventId.IndexOf(':', StringComparison.Ordinal);

        if (separatorIndex <= 0
            || separatorIndex != lastEventId.LastIndexOf(':')
            || separatorIndex == lastEventId.Length - 1)
        {
            error = "Invalid Last-Event-ID header.";
            return false;
        }

        var eventJobId = lastEventId[..separatorIndex];
        var sequenceText = lastEventId[(separatorIndex + 1)..];

        if (!string.Equals(eventJobId, jobId, StringComparison.Ordinal))
        {
            error = "Last-Event-ID does not match the requested job.";
            return false;
        }

        if (sequenceText.Length != 12
            || sequenceText.Any(character => character is < '0' or > '9')
            || !long.TryParse(sequenceText, out resumeAfterSequence))
        {
            error = "Invalid Last-Event-ID header.";
            return false;
        }

        return true;
    }

    private static HttpResponseData CreateEmptyResponse(HttpRequestData req, HttpStatusCode statusCode)
    {
        var response = req.CreateResponse(statusCode);
        FunctionCors.Apply(req, response);
        return response;
    }

    private static async Task<HttpResponseData> CreateJsonResponseAsync<T>(
        HttpRequestData req,
        HttpStatusCode statusCode,
        T payload,
        CancellationToken cancellationToken)
    {
        var response = req.CreateResponse(statusCode);
        FunctionCors.Apply(req, response);
        await response.WriteAsJsonAsync(payload, cancellationToken);
        return response;
    }

    private async Task<SseMaterializedWriteResult> WriteReplayedEventsAsync(
        ChannelWriter<SseItem<string>> writer,
        string jobId,
        string appUserId,
        long resumeAfterSequence,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ConsultGenerationJobStoredEvent> storedEvents;

        try
        {
            storedEvents = await _eventStore.ReadAfterAsync(
                jobId,
                appUserId,
                resumeAfterSequence,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Consult generation SSE replay read failed. JobId={JobId}, ResumeAfterSequence={ResumeAfterSequence}",
                jobId,
                resumeAfterSequence);

            throw;
        }

        return await WriteStoredEventsAsync(
            writer,
            storedEvents,
            resumeAfterSequence,
            cancellationToken);
    }

    private async Task<SseMaterializedWriteResult> WriteMaterializedEventsAsync(
        ChannelWriter<SseItem<string>> writer,
        ConsultGenerationJobResponse response,
        long highestEmittedSequence,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ConsultGenerationJobStoredEvent> storedEvents;

        try
        {
            storedEvents = await _eventStore.AppendAsync(
                response.JobId,
                response.AppUserId,
                CreateSemanticEventCandidates(response),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Consult generation SSE event persistence failed. JobId={JobId}, Status={Status}",
                response.JobId,
                response.Status);

            throw;
        }

        return await WriteStoredEventsAsync(
            writer,
            storedEvents,
            highestEmittedSequence,
            cancellationToken);
    }

    private static async Task<SseMaterializedWriteResult> WriteStoredEventsAsync(
        ChannelWriter<SseItem<string>> writer,
        IReadOnlyList<ConsultGenerationJobStoredEvent> storedEvents,
        long highestEmittedSequence,
        CancellationToken cancellationToken)
    {
        var eventCount = 0;
        var latestEventId = (string?)null;
        var latestEventType = (string?)null;

        foreach (var storedEvent in storedEvents.Where(storedEvent => storedEvent.Sequence > highestEmittedSequence))
        {
            var item = CreateSseItem(storedEvent);
            await writer.WriteAsync(item, cancellationToken);
            highestEmittedSequence = Math.Max(highestEmittedSequence, storedEvent.Sequence);
            eventCount++;
            latestEventId = item.EventId;
            latestEventType = item.EventType;
        }

        return new SseMaterializedWriteResult(
            highestEmittedSequence,
            eventCount,
            latestEventId,
            latestEventType);
    }

    private async Task TryMaterializeEventsForPollingAsync(
        ConsultGenerationJobResponse response,
        CancellationToken cancellationToken)
    {
        try
        {
            await _eventStore.AppendAsync(
                response.JobId,
                response.AppUserId,
                CreateSemanticEventCandidates(response),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Consult generation polling event materialization failed. Returning job response without persisted SSE catch-up events. JobId={JobId}, Status={Status}",
                response.JobId,
                response.Status);
        }
    }

    private static SseItem<string> CreateSseItem(ConsultGenerationJobStoredEvent storedEvent)
    {
        return new SseItem<string>(storedEvent.PayloadJson, storedEvent.EventType)
        {
            EventId = storedEvent.SseId
        };
    }

    private static SseItem<string> CreateSseItem<T>(
        string eventName,
        T payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        return new SseItem<string>(json, eventName);
    }

    private static IReadOnlyList<ConsultGenerationJobEventCandidate> CreateSemanticEventCandidates(
        ConsultGenerationJobResponse response)
    {
        var candidates = new List<ConsultGenerationJobEventCandidate>();

        AddEventCandidate(candidates, "snapshot", "snapshot", response);
        AddAnalysisEventCandidates(candidates, response);
        AddSectionProseStepEventCandidates(candidates, response);

        foreach (var generatedSection in response.GeneratedSections.OrderBy(section => section.Key, StringComparer.Ordinal))
        {
            AddEventCandidate(
                candidates,
                "section-completed",
                $"section-completed:{generatedSection.Key}",
                new ConsultGenerationJobSectionCompletedEvent(response.JobId, generatedSection.Key, generatedSection.Value));
        }

        foreach (var failedSection in response.FailedSections.OrderBy(section => section.Key, StringComparer.Ordinal))
        {
            AddEventCandidate(
                candidates,
                "section-failed",
                $"section-failed:{failedSection.Key}",
                new ConsultGenerationJobSectionFailedEvent(response.JobId, failedSection.Key, failedSection.Value));
        }

        if (IsTerminalJobStatus(response.Status))
        {
            if (response.Status == ConsultGenerationJobStatuses.Failed)
            {
                AddTerminalFailureEventCandidate(candidates, response);
                return candidates;
            }

            AddEventCandidate(candidates, "done", "done", response);
        }

        return candidates;
    }

    private static void AddTerminalFailureEventCandidate(
        List<ConsultGenerationJobEventCandidate> candidates,
        ConsultGenerationJobResponse response)
    {
        if (IsAnalysisFailureStatus(response.AnalysisStatus ?? string.Empty))
        {
            return;
        }

        var stage = response.RuntimeFailureStage;
        var error = response.RuntimeFailureError;

        if (string.IsNullOrWhiteSpace(stage))
        {
            stage = response.FailedSections.Count > 0
                ? "section-generation-failed"
                : ConsultGenerationRuntimeFailure.StageName;
        }

        if (string.IsNullOrWhiteSpace(error))
        {
            error = response.FailedSections.Count > 0
                ? "Consult generation failed because no sections were generated."
                : "Consult generation failed while running the backend workflow. Backend workflow stopped before completion.";
        }

        AddEventCandidate(
            candidates,
            "error",
            $"error:{stage}",
            new ConsultGenerationJobStreamError(response.JobId, error, stage));
    }

    private static void AddAnalysisEventCandidates(
        List<ConsultGenerationJobEventCandidate> candidates,
        ConsultGenerationJobResponse response)
    {
        if (string.IsNullOrWhiteSpace(response.AnalysisStatus))
        {
            return;
        }

        if (IsAnalysisFailureStatus(response.AnalysisStatus))
        {
            AddEventCandidate(
                candidates,
                "error",
                $"error:{response.AnalysisStatus}",
                new ConsultGenerationJobStreamError(
                    response.JobId,
                    response.AnalysisError ?? "Consult preprocessing failed.",
                    response.AnalysisStatus));
            return;
        }

        var completedStageCount = Math.Clamp(
            response.CompletedStageCount ?? 0,
            0,
            ConsultGenerationAnalysisStatuses.TotalStageCount);

        for (var stageCount = 1; stageCount <= completedStageCount; stageCount++)
        {
            var stage = GetAnalysisStageByCompletedCount(stageCount);

            if (stage == null)
            {
                continue;
            }

            AddEventCandidate(
                candidates,
                stage,
                $"analysis:{stage}",
                new ConsultGenerationJobStageEvent(
                    response.JobId,
                    stage,
                    GetAnalysisStageMessage(stage),
                    stageCount,
                    response.TotalStageCount ?? ConsultGenerationAnalysisStatuses.TotalStageCount));
        }
    }

    private static void AddSectionProseStepEventCandidates(
        List<ConsultGenerationJobEventCandidate> candidates,
        ConsultGenerationJobResponse response)
    {
        if (response.SectionProseProgress == null)
        {
            return;
        }

        foreach (var progress in response.SectionProseProgress.Values.OrderBy(section => section.SectionId, StringComparer.Ordinal))
        {
            var completedStepCount = Math.Clamp(
                progress.CompletedProseStepCount,
                0,
                ConsultGenerationSectionProseSteps.TotalStepCount);

            for (var stepCount = 1; stepCount <= completedStepCount; stepCount++)
            {
                var step = GetSectionProseStepByCompletedCount(stepCount);

                if (step == null)
                {
                    continue;
                }

                AddEventCandidate(
                    candidates,
                    step,
                    $"section-prose:{progress.SectionId}:{step}",
                    new ConsultGenerationSectionProseStepEvent(
                        response.JobId,
                        progress.SectionId,
                        progress.SectionName,
                        step,
                        GetSectionProseStepMessage(step),
                        stepCount,
                        progress.TotalProseStepCount));
            }
        }
    }

    private static void AddEventCandidate<T>(
        List<ConsultGenerationJobEventCandidate> candidates,
        string eventType,
        string eventKey,
        T payload)
    {
        candidates.Add(new ConsultGenerationJobEventCandidate(
            eventType,
            eventKey,
            JsonSerializer.Serialize(payload, JsonOptions)));
    }

    private static ConsultGenerationRuntimeFailure? GetSanitizedRuntimeFailure(OrchestrationMetadata instance)
    {
        if (MapRuntimeStatus(instance.RuntimeStatus) != ConsultGenerationJobStatuses.Failed)
        {
            return null;
        }

        if (instance.FailureDetails == null)
        {
            return new ConsultGenerationRuntimeFailure(
                ConsultGenerationRuntimeFailure.StageName,
                "Consult generation failed while running the backend workflow. Backend workflow stopped before completion.");
        }

        return GetSanitizedRuntimeFailure(instance.FailureDetails);
    }

    private static ConsultGenerationRuntimeFailure GetSanitizedRuntimeFailure(TaskFailureDetails failureDetails)
    {
        var failureText = GetFailureText(failureDetails);
        var action = GetRuntimeFailureAction(failureText);
        var cause = GetRuntimeFailureCause(failureText);

        return new ConsultGenerationRuntimeFailure(
            ConsultGenerationRuntimeFailure.StageName,
            $"Consult generation failed while {action}. {cause}");
    }

    private static string GetFailureText(TaskFailureDetails failureDetails)
    {
        var parts = new List<string>();

        for (var current = failureDetails; current != null; current = current.InnerFailure)
        {
            if (!string.IsNullOrWhiteSpace(current.ErrorType))
            {
                parts.Add(current.ErrorType);
            }

            if (!string.IsNullOrWhiteSpace(current.ErrorMessage))
            {
                parts.Add(current.ErrorMessage);
            }
        }

        return string.Join(" ", parts);
    }

    private static string GetRuntimeFailureAction(string failureText)
    {
        if (failureText.Contains(nameof(ExtractPatientConceptsActivity), StringComparison.Ordinal))
        {
            return "extracting patient concepts";
        }

        if (failureText.Contains(nameof(IdentifyProblemActivity), StringComparison.Ordinal))
        {
            return "identifying the primary problem";
        }

        if (failureText.Contains(nameof(CreateTypicalTrajectoryActivity), StringComparison.Ordinal))
        {
            return "building the reference trajectory";
        }

        if (failureText.Contains(nameof(CreatePatientTrajectoryActivity), StringComparison.Ordinal))
        {
            return "building the patient trajectory";
        }

        if (failureText.Contains(ConsultGenerationActivityNames.GenerateStandardSectionDraft, StringComparison.Ordinal)
            || failureText.Contains(ConsultGenerationActivityNames.UpdateSectionWithPatientInformation, StringComparison.Ordinal)
            || failureText.Contains(ConsultGenerationActivityNames.ApplySectionInstructions, StringComparison.Ordinal))
        {
            return "generating a consult section";
        }

        return "running the backend workflow";
    }

    private static string GetRuntimeFailureCause(string failureText)
    {
        if (failureText.Contains("HTTP 408", StringComparison.OrdinalIgnoreCase)
            || failureText.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || failureText.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return "Upstream AI service timed out.";
        }

        return "A backend dependency failed.";
    }

    private static bool IsAnalysisFailureStatus(string status)
    {
        return status.EndsWith("-failed", StringComparison.Ordinal);
    }

    private static string GetAnalysisStageMessage(string stage)
    {
        return stage switch
        {
            ConsultGenerationAnalysisStatuses.AnalysisStarted => "Analysis started.",
            ConsultGenerationAnalysisStatuses.ConceptsExtracted => "Clinical concepts extracted.",
            ConsultGenerationAnalysisStatuses.ProblemIdentified => "Primary problem identified.",
            ConsultGenerationAnalysisStatuses.TypicalTrajectoryCreated => "Reference trajectory created.",
            ConsultGenerationAnalysisStatuses.PatientTrajectoryCreated => "Patient trajectory created.",
            ConsultGenerationAnalysisStatuses.SectionGenerationStarted => "Section generation started.",
            _ => "Consult generation stage updated."
        };
    }

    private static string? GetAnalysisStageByCompletedCount(int completedStageCount)
    {
        var index = completedStageCount - 1;
        var stages = ConsultGenerationAnalysisStatuses.OrderedStages;
        return index >= 0 && index < stages.Length ? stages[index] : null;
    }

    private static string? GetSectionProseStepByCompletedCount(int completedStepCount)
    {
        return completedStepCount switch
        {
            1 => ConsultGenerationSectionProseSteps.StandardDraftCreated,
            2 => ConsultGenerationSectionProseSteps.PatientDraftCreated,
            3 => ConsultGenerationSectionProseSteps.InstructionsApplied,
            _ => null
        };
    }

    private static string GetSectionProseStepMessage(string step)
    {
        return step switch
        {
            ConsultGenerationSectionProseSteps.StandardDraftCreated => "Standard section draft created.",
            ConsultGenerationSectionProseSteps.PatientDraftCreated => "Patient information applied to section draft.",
            ConsultGenerationSectionProseSteps.InstructionsApplied => "Section instructions applied.",
            _ => "Section prose step completed."
        };
    }

    private sealed class CorsResultActionResult : IActionResult
    {
        private readonly IResult _result;

        public CorsResultActionResult(IResult result)
        {
            _result = result;
        }

        public Task ExecuteResultAsync(ActionContext context)
        {
            FunctionCors.Apply(context.HttpContext.Request, context.HttpContext.Response);
            context.HttpContext.Response.Headers.CacheControl = "no-cache";
            return _result.ExecuteAsync(context.HttpContext);
        }
    }

    private sealed record SseMaterializedWriteResult(
        long HighestEmittedSequence,
        int EventCount,
        string? LatestEventId,
        string? LatestEventType);
}

public sealed record ConsultGenerationJobSectionCompletedEvent(
    string JobId,
    string SectionId,
    string Text);

public sealed record ConsultGenerationJobSectionFailedEvent(
    string JobId,
    string SectionId,
    string Error);

public sealed record ConsultGenerationJobHeartbeatEvent(
    string JobId,
    string Status);

public sealed record ConsultGenerationJobStreamError(
    string JobId,
    string Error,
    string? Stage = null);

public sealed record ConsultGenerationJobStageEvent(
    string JobId,
    string Stage,
    string Message,
    int CompletedStageCount,
    int TotalStageCount);

public sealed record ConsultGenerationSectionProseStepEvent(
    string JobId,
    string SectionId,
    string SectionName,
    string Step,
    string Message,
    int CompletedStepCount,
    int TotalStepCount);

public sealed record ConsultGenerationRuntimeFailure(
    string Stage,
    string Error)
{
    public const string StageName = "runtime-failed";
}

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
        await context.Entities.CallEntityAsync(
            entityId,
            nameof(ConsultGenerationJobEntity.Initialize),
            new ConsultGenerationJobInitialize(
                context.InstanceId,
                input.AppUserId,
                request.Sections,
                input.WorkflowPackage,
                input.EffectiveInputHash,
                input.AgentVersion));

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
                input.WorkflowPackage);

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
        string? workflowPackage)
    {
        var input = new ConsultGenerationActivityInput(consultDraft, patientTrajectoryConcepts, section, WorkflowPackage: workflowPackage);
        var stepName = ConsultGenerationSectionProseSteps.StandardDraftCreated;

        try
        {
            var standardDraft = await context.CallActivityAsync<string>(
                ConsultGenerationActivityNames.GenerateStandardSectionDraft,
                input,
                AgentActivityRetryOptions);

            await context.Entities.CallEntityAsync(
                entityId,
                nameof(ConsultGenerationJobEntity.MarkSectionProseStep),
                new ConsultGenerationSectionProseStepUpdate(
                    section.Id,
                    section.Name,
                    ConsultGenerationSectionProseSteps.StandardDraftCreated,
                    1));

            stepName = ConsultGenerationSectionProseSteps.PatientDraftCreated;
            var patientDraft = await context.CallActivityAsync<string>(
                ConsultGenerationActivityNames.UpdateSectionWithPatientInformation,
                input with { SectionDraft = standardDraft },
                AgentActivityRetryOptions);

            await context.Entities.CallEntityAsync(
                entityId,
                nameof(ConsultGenerationJobEntity.MarkSectionProseStep),
                new ConsultGenerationSectionProseStepUpdate(
                    section.Id,
                    section.Name,
                    ConsultGenerationSectionProseSteps.PatientDraftCreated,
                    2));

            stepName = ConsultGenerationSectionProseSteps.InstructionsApplied;
            var finalProse = await context.CallActivityAsync<string>(
                ConsultGenerationActivityNames.ApplySectionInstructions,
                input with { SectionDraft = patientDraft },
                AgentActivityRetryOptions);

            await context.Entities.CallEntityAsync(
                entityId,
                nameof(ConsultGenerationJobEntity.MarkSectionProseStep),
                new ConsultGenerationSectionProseStepUpdate(
                    section.Id,
                    section.Name,
                    ConsultGenerationSectionProseSteps.InstructionsApplied,
                    3));

            return new SectionGenerationResult(section.Id, section.Name, true, finalProse.Trim(), null);
        }
        catch (Exception ex)
        {
            return new SectionGenerationResult(section.Id, section.Name, false, null, $"{stepName} failed: {ex.Message}");
        }
    }
}

public sealed class GenerateConsultSectionActivity
{
    private readonly ILogger<GenerateConsultSectionActivity> _logger;
    private readonly AgentSectionGenerator _sectionGenerator;

    public GenerateConsultSectionActivity(
        ILogger<GenerateConsultSectionActivity> logger,
        AgentSectionGenerator sectionGenerator)
    {
        _logger = logger;
        _sectionGenerator = sectionGenerator;
    }

    [Function(ConsultGenerationActivityNames.GenerateStandardSectionDraft)]
    public async Task<string> GenerateStandardSectionDraftAsync(
        [ActivityTrigger] ConsultGenerationActivityInput input,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var section = input.Section;

        try
        {
            _logger.LogInformation(
                "Starting standard section draft generation. SectionId={SectionId}, SectionName={SectionName}",
                section.Id,
                section.Name);

            var prose = await _sectionGenerator.GenerateStandardSectionDraftAsync(
                input.PatientTrajectoryConcepts,
                section.Name,
                input.WorkflowPackage,
                cancellationToken);

            var trimmedProse = prose.Trim();

            _logger.LogInformation(
                "Standard section draft generation completed. SectionId={SectionId}, SectionName={SectionName}, ResponseLength={ResponseLength}, ElapsedMs={ElapsedMs}",
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
                "Standard section draft generation failed. SectionId={SectionId}, SectionName={SectionName}, ExceptionType={ExceptionType}, Message={Message}, ElapsedMs={ElapsedMs}",
                section.Id,
                section.Name,
                ex.GetType().FullName,
                ex.Message,
                stopwatch.ElapsedMilliseconds);

            throw;
        }
    }

    [Function(ConsultGenerationActivityNames.UpdateSectionWithPatientInformation)]
    public async Task<string> UpdateSectionWithPatientInformationAsync(
        [ActivityTrigger] ConsultGenerationActivityInput input,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var section = input.Section;

        try
        {
            _logger.LogInformation(
                "Starting patient section draft generation. SectionId={SectionId}, SectionName={SectionName}",
                section.Id,
                section.Name);

            var prose = await _sectionGenerator.UpdateSectionWithPatientInformationAsync(
                input.SectionDraft ?? string.Empty,
                input.ConsultDraft,
                section.Name,
                input.WorkflowPackage,
                cancellationToken);

            var trimmedProse = prose.Trim();

            _logger.LogInformation(
                "Patient section draft generation completed. SectionId={SectionId}, SectionName={SectionName}, ResponseLength={ResponseLength}, ElapsedMs={ElapsedMs}",
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
                "Patient section draft generation failed. SectionId={SectionId}, SectionName={SectionName}, ExceptionType={ExceptionType}, Message={Message}, ElapsedMs={ElapsedMs}",
                section.Id,
                section.Name,
                ex.GetType().FullName,
                ex.Message,
                stopwatch.ElapsedMilliseconds);

            throw;
        }
    }

    [Function(ConsultGenerationActivityNames.ApplySectionInstructions)]
    public async Task<string> ApplySectionInstructionsAsync(
        [ActivityTrigger] ConsultGenerationActivityInput input,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var section = input.Section;

        try
        {
            _logger.LogInformation(
                "Starting section instruction application. SectionId={SectionId}, SectionName={SectionName}",
                section.Id,
                section.Name);

            var prose = await _sectionGenerator.ApplySectionInstructionsAsync(
                input.SectionDraft ?? string.Empty,
                section.Name,
                section.Standard,
                input.WorkflowPackage,
                cancellationToken);

            var trimmedProse = prose.Trim();

            _logger.LogInformation(
                "Section instruction application completed. SectionId={SectionId}, SectionName={SectionName}, ResponseLength={ResponseLength}, ElapsedMs={ElapsedMs}",
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
                "Section instruction application failed. SectionId={SectionId}, SectionName={SectionName}, ExceptionType={ExceptionType}, Message={Message}, ElapsedMs={ElapsedMs}",
                section.Id,
                section.Name,
                ex.GetType().FullName,
                ex.Message,
                stopwatch.ElapsedMilliseconds);

            throw;
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
        var rendered = await _promptProvider.TryRenderAsync(
            input.WorkflowPackage,
            WorkflowPromptContract.ExtractPatientConcepts,
            new Dictionary<string, string> { [WorkflowPromptContract.ConsultDraft] = input.ConsultDraft },
            cancellationToken);

        var prompt = rendered ?? ConsultGenerationPreprocessingRunner.SnomedToolGuidance + "\n\n" + $"""
            Extract patient-specific clinical concepts from the draft consult note.

            Output only SNOMED concept bullets in these exact forms:
            - term (type) - id number
            - term [not SNOMED concept]
            - term (type) - id number [not active SNOMED concept]

            Include inactive SNOMED concepts when relevant. Include clinically important findings that are not SNOMED concepts using [not SNOMED concept].
            Do not include commentary, headings, JSON, or non-bullet lines.

            Draft consult note:
            {input.ConsultDraft}
            """;

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
        var rendered = await _promptProvider.TryRenderAsync(
            input.WorkflowPackage,
            WorkflowPromptContract.IdentifyProblem,
            new Dictionary<string, string>
            {
                [WorkflowPromptContract.PatientConcepts] = ConsultGenerationConceptFormatter.Format(input.PatientConcepts)
            },
            cancellationToken);

        var prompt = rendered ?? ConsultGenerationPreprocessingRunner.SnomedToolGuidance + "\n\n" + $"""
            Identify the primary disease or problem concept from the validated patient concepts.

            Output only one or more SNOMED concept bullets in these exact forms:
            - term (type) - id number
            - term [not SNOMED concept]
            - term (type) - id number [not active SNOMED concept]

            Prefer the disease/problem driving the oncology consult. Do not include commentary, headings, JSON, or non-bullet lines.

            Validated patient concepts:
            {ConsultGenerationConceptFormatter.Format(input.PatientConcepts)}
            """;

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
        var rendered = await _promptProvider.TryRenderAsync(
            input.WorkflowPackage,
            WorkflowPromptContract.CreateTypicalTrajectory,
            new Dictionary<string, string>
            {
                [WorkflowPromptContract.ProblemConcepts] = ConsultGenerationConceptFormatter.Format(input.ProblemContext)
            },
            cancellationToken);

        var prompt = rendered ?? ConsultGenerationPreprocessingRunner.SnomedToolGuidance + "\n\n" + $"""
            Build a typical clinical trajectory for the disease/problem concept.

            Output only SNOMED concept bullets in these exact forms:
            - term (type) - id number
            - term [not SNOMED concept]
            - term (type) - id number [not active SNOMED concept]

            Include a concise support phrase after the accepted bullet only when needed by appending " -- support: ...".
            Do not include commentary, headings, JSON, or non-bullet lines.

            Disease/problem concept:
            {ConsultGenerationConceptFormatter.Format(input.ProblemContext)}
            """;

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
        var rendered = await _promptProvider.TryRenderAsync(
            input.WorkflowPackage,
            WorkflowPromptContract.CreatePatientTrajectory,
            new Dictionary<string, string>
            {
                [WorkflowPromptContract.ProblemConcepts] = ConsultGenerationConceptFormatter.Format(input.ProblemContext),
                [WorkflowPromptContract.PatientConcepts] = ConsultGenerationConceptFormatter.Format(input.PatientConcepts),
                [WorkflowPromptContract.TypicalTrajectoryConcepts] = ConsultGenerationConceptFormatter.Format(input.TypicalTrajectoryConcepts)
            },
            cancellationToken);

        var prompt = rendered ?? ConsultGenerationPreprocessingRunner.SnomedToolGuidance + "\n\n" + $"""
            Reconcile a patient-specific trajectory from the validated patient concepts and typical trajectory.

            Output only SNOMED concept bullets in these exact forms:
            - term (type) - id number
            - term [not SNOMED concept]
            - term (type) - id number [not active SNOMED concept]

            Include only patient-specific trajectory details supported by validated patient concepts. Do not add typical trajectory details unless supported by patient concepts.
            Include a concise support phrase after the accepted bullet only when needed by appending " -- support: ...".
            Do not include commentary, headings, JSON, or non-bullet lines.

            Disease/problem concept:
            {ConsultGenerationConceptFormatter.Format(input.ProblemContext)}

            Validated patient concepts:
            {ConsultGenerationConceptFormatter.Format(input.PatientConcepts)}

            Typical trajectory concepts:
            {ConsultGenerationConceptFormatter.Format(input.TypicalTrajectoryConcepts)}
            """;

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
    // Snowstorm rejects search terms outside 3-250 characters; steer the agent away
    // from passing sentences as terms. See docs/SNOMED_TOOL_FAILURES.md. On the
    // package path this text lives in the package's snomed-tool-guidance prelude;
    // this constant is the compiled fallback.
    public const string SnomedToolGuidance =
        "When calling SNOMED terminology tools such as search_concepts, look up one short clinical term per call " +
        "(a few words, 3-250 characters) - never a sentence or passage. If a tool returns an error message, " +
        "adjust the call as the message directs and retry.";

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

public static class ConsultGenerationConceptParser
{
    private static readonly Regex ActiveConceptPattern = new(
        @"^- (?<term>.+?) \((?<type>[^)]+)\) - (?<id>\d+)(?<inactive> \[not active SNOMED concept\])?(?<support> -- support: .+)?$",
        RegexOptions.Compiled);

    private static readonly Regex NonSnomedConceptPattern = new(
        @"^- (?<term>.+?) \[not SNOMED concept\](?<support> -- support: .+)?$",
        RegexOptions.Compiled);

    public static ConceptExtractionResult Parse(string rawText, string source, string warningStage)
    {
        var concepts = new List<ClinicalConcept>();
        var droppedLines = new List<string>();

        foreach (var line in rawText.Replace("\r\n", "\n").Split('\n').Select(value => value.Trim()).Where(value => value.Length > 0))
        {
            // Bare lines and "*" bullets are intentionally rejected so agent output cannot drift into ambiguous prose.
            if (!line.StartsWith("- ", StringComparison.Ordinal))
            {
                droppedLines.Add(line);
                continue;
            }

            var activeMatch = ActiveConceptPattern.Match(line);
            if (activeMatch.Success)
            {
                concepts.Add(new ClinicalConcept(
                    activeMatch.Groups["term"].Value.Trim(),
                    activeMatch.Groups["type"].Value.Trim(),
                    activeMatch.Groups["id"].Value.Trim(),
                    true,
                    !activeMatch.Groups["inactive"].Success,
                    source,
                    ExtractSupport(activeMatch.Groups["support"].Value)));
                continue;
            }

            var nonSnomedMatch = NonSnomedConceptPattern.Match(line);
            if (nonSnomedMatch.Success)
            {
                concepts.Add(new ClinicalConcept(
                    nonSnomedMatch.Groups["term"].Value.Trim(),
                    "finding",
                    string.Empty,
                    false,
                    false,
                    source,
                    ExtractSupport(nonSnomedMatch.Groups["support"].Value)));
                continue;
            }

            droppedLines.Add(line);
        }

        foreach (var droppedLine in droppedLines)
        {
            Console.Error.WriteLine($"[SNOMEDParser] Dropped malformed raw line. Stage={warningStage}; Line={droppedLine}");
        }

        var warnings = droppedLines.Count == 0
            ? Array.Empty<ConsultGenerationValidationWarning>()
            : new[]
            {
                new ConsultGenerationValidationWarning(warningStage, droppedLines.Count, "Malformed SNOMED bullet")
            };

        Console.Error.WriteLine($"[SNOMEDParser] Stage={warningStage}; ValidConceptCount={concepts.Count}; DroppedMalformedConceptCount={droppedLines.Count}");
        return new ConceptExtractionResult(concepts, warnings);
    }

    private static string? ExtractSupport(string value)
    {
        const string marker = " -- support: ";
        return string.IsNullOrWhiteSpace(value) ? null : value.Replace(marker, string.Empty, StringComparison.Ordinal).Trim();
    }
}

public static class ConsultGenerationConceptFormatter
{
    public static string Format(IReadOnlyList<ClinicalConcept> concepts)
    {
        return concepts.Count == 0
            ? "(none)"
            : string.Join(Environment.NewLine, concepts.Select(FormatOne));
    }

    private static string FormatOne(ClinicalConcept concept)
    {
        if (!concept.IsSnomedConcept)
        {
            return $"- {concept.Term} [not SNOMED concept]";
        }

        var inactive = concept.IsActive ? string.Empty : " [not active SNOMED concept]";
        return $"- {concept.Term} ({concept.Type}) - {concept.Id}{inactive}";
    }
}

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
        section.TotalProseStepCount = ConsultGenerationSectionProseSteps.TotalStepCount;
        State = state;
    }

    public async Task FinalizeJob(ConsultGenerationJobFinalize input)
    {
        var state = EnsureState();
        state.Status = input.Status;
        state.CompletedAtUtc = DateTimeOffset.UtcNow;

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

public sealed record ConsultGenerationActivityInput(
    string ConsultDraft,
    IReadOnlyList<ClinicalConcept> PatientTrajectoryConcepts,
    ConsultGenerationSectionRequest Section,
    string? SectionDraft = null,
    string? WorkflowPackage = null);

public sealed record ConsultGenerationOrchestrationInput(
    ConsultGenerationRequest Request,
    string AppUserId,
    string? WorkflowPackage = null,
    string? EffectiveInputHash = null,
    string? AgentVersion = null);

public sealed record ConsultGenerationJobInitialize(
    string JobId,
    string AppUserId,
    IReadOnlyList<ConsultGenerationSectionRequest> Sections,
    string? WorkflowPackage = null,
    string? EffectiveInputHash = null,
    string? AgentVersion = null);

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
            AgentVersion: AgentVersion);
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

public static class ConsultGenerationJobStatuses
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

public static class ConsultGenerationActivityNames
{
    public const string GenerateStandardSectionDraft = "generate-standard-section-draft";
    public const string UpdateSectionWithPatientInformation = "update-section-with-patient-information";
    public const string ApplySectionInstructions = "apply-section-instructions";
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
    public const int TotalStepCount = 3;
    public const string StandardDraftCreated = "section-standard-draft-created";
    public const string PatientDraftCreated = "section-patient-draft-created";
    public const string InstructionsApplied = "section-instructions-applied";
}

public static class ConsultGenerationSectionStatuses
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

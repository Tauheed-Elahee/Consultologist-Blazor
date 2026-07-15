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
    private readonly IWorkflowPackageStore _packageStore;
    private readonly IWorkflowPackagePinResolver _pinResolver;
    private readonly OutputContractCatalog _catalog;

    public ConsultGenerationJobs(
        ILogger<ConsultGenerationJobs> logger,
        IAccountAuthorizer authorizer,
        IConsultGenerationJobEventStore eventStore,
        IWorkflowPackageStore packageStore,
        IWorkflowPackagePinResolver pinResolver,
        OutputContractCatalog catalog)
    {
        _logger = logger;
        _authorizer = authorizer;
        _eventStore = eventStore;
        _packageStore = packageStore;
        _pinResolver = pinResolver;
        _catalog = catalog;
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

            // A workflow package is mandatory: resolve the ref here (request → account
            // pin → default) to a concrete immutable version and snapshot it into the
            // job, so the whole run — and the provenance record — uses one version even
            // when the pin says "latest". Registry failure stops the job before it exists.
            if (!WorkflowPackageRef.TryParse(request.WorkflowPackage, out var packageRef))
            {
                if (!string.IsNullOrWhiteSpace(request.WorkflowPackage))
                {
                    _logger.LogWarning("Invalid ConsultGenerationJobs request: malformed workflow package ref '{PackageRef}'.", request.WorkflowPackage);
                    return await CreateJsonResponseAsync(req, HttpStatusCode.BadRequest, new { error = "WorkflowPackage is not a valid package reference." }, cancellationToken);
                }

                packageRef = await _pinResolver.ResolvePinAsync(account.AppUserId, cancellationToken);
            }

            WorkflowPackage package;
            try
            {
                package = await _packageStore.ResolveAsync(packageRef!, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Workflow package resolution failed at job start. Pin={Pin}", packageRef);
                return await CreateJsonResponseAsync(req, HttpStatusCode.ServiceUnavailable, new { error = "Workflow package registry is unavailable." }, cancellationToken);
            }

            if (package.Nodes is not { Count: > 0 } || package.ResultNodeId is null)
            {
                _logger.LogWarning("Workflow package {Package} (specVersion {SpecVersion}) has no executable nodes; jobs require specVersion 2 or newer.", package.Ref, package.Manifest.SpecVersion);
                return await CreateJsonResponseAsync(req, HttpStatusCode.UnprocessableEntity, new { error = $"Workflow package {package.Ref} (specVersion {package.Manifest.SpecVersion}) predates prompt templates; pin a specVersion 2 or newer package." }, cancellationToken);
            }

            // The v5 input model (server-resolved sections, draft-only hash) is the
            // next slice; guard-then-implement, as with v4 (#71).
            if (package.Manifest.SpecVersion >= 5)
            {
                _logger.LogWarning("Workflow package {Package} is specVersion {SpecVersion}; the v5 input model is not wired yet.", package.Ref, package.Manifest.SpecVersion);
                return await CreateJsonResponseAsync(req, HttpStatusCode.UnprocessableEntity, new { error = $"Workflow package {package.Ref} requires the specVersion-5 input model, which this engine does not accept yet." }, cancellationToken);
            }

            var resolvedPackageRef = package.Ref;
            // The per-section step list is the forEach chain, in manifest order — the
            // display/progress skeleton the section-prose-step events hang off.
            var sectionSteps = package.Nodes
                .Where(node => node.ForEach != null)
                .Select(node => new ConsultSectionStepDescriptor(node.Id, node.Label))
                .ToList();
            var nodes = package.Nodes.Select(node => DescribeNode(node, package.SchemaContracts)).ToList();

            // Provenance: identify the artifacts and input that produce this consult.
            // Every catalog contract's agent version is recorded, keyed by contract id;
            // the legacy scalar fields mirror the text/concept-list entries until the
            // next response SchemaVersion bump. See docs/customizable-workflow/provenance.md.
            var effectiveInputHash = ConsultGenerationProvenance.ComputeEffectiveInputHash(request);
            var agentVersions = _catalog.Entries.Values.ToDictionary(
                entry => entry.ContractId,
                entry => entry.AgentVersion,
                StringComparer.Ordinal);
            var agentVersion = agentVersions.GetValueOrDefault(OutputContracts.Text);
            var conceptAgentVersion = agentVersions.GetValueOrDefault(OutputContracts.ConceptList);

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
                    resolvedPackageRef,
                    effectiveInputHash,
                    agentVersion,
                    sectionSteps,
                    conceptAgentVersion,
                    nodes,
                    agentVersions));

            _logger.LogInformation(
                "StartConsultGenerationJob scheduling orchestration. InvocationId={InvocationId}, JobId={JobId}",
                req.FunctionContext.InvocationId,
                jobId);

            var instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
                nameof(ConsultGenerationOrchestrator),
                new ConsultGenerationOrchestrationInput(
                    request,
                    account.AppUserId,
                    resolvedPackageRef,
                    effectiveInputHash,
                    agentVersion,
                    sectionSteps,
                    conceptAgentVersion,
                    nodes,
                    agentVersions,
                    package.ResultNodeId),
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

    /// <summary>
    /// Snapshots one package node into the job's descriptor form: bindings flattened,
    /// the node's schema resolved to its catalog contract id, forEach carried through,
    /// and the legacy concept-source stamp applied for the four canonical analysis
    /// node ids.
    /// </summary>
    private static ConsultNodeDescriptor DescribeNode(
        WorkflowNodeSpec node,
        IReadOnlyDictionary<string, string>? schemaContracts)
    {
        return new ConsultNodeDescriptor(
            node.Id,
            node.Label,
            node.Prompt,
            node.Bindings?.ToDictionary(
                pair => pair.Key,
                pair => new ConsultNodeBindingDescriptor(pair.Value.From, pair.Value.As),
                StringComparer.Ordinal),
            OutputContract: node.Output is null
                ? null
                : schemaContracts?.GetValueOrDefault(node.Output.Schema)
                    ?? throw new InvalidOperationException(
                        $"Node '{node.Id}' declares schema '{node.Output.Schema}' with no resolved output contract."),
            FailIfEmpty: node.Output?.FailIfEmpty,
            ForEach: node.ForEach,
            ConceptSource: WorkflowNodeDefaults.WellKnownConceptSources.GetValueOrDefault(node.Id, node.Id));
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

    internal static IReadOnlyList<ConsultGenerationJobEventCandidate> CreateSemanticEventCandidates(
        ConsultGenerationJobResponse response)
    {
        var candidates = new List<ConsultGenerationJobEventCandidate>();

        AddEventCandidate(candidates, "snapshot", "snapshot", response);
        // Pre-DAG (SchemaVersion 2) snapshots regenerate no stage candidates; their
        // events were materialized while they ran and replay from the event store. The
        // node path's failure branch covers both eras via the '-failed' status suffix.
        AddNodeEventCandidates(candidates, response);
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

    private static void AddNodeEventCandidates(
        List<ConsultGenerationJobEventCandidate> candidates,
        ConsultGenerationJobResponse response)
    {
        // Failures ride the existing error-event path via the '-failed' status suffix.
        if (IsAnalysisFailureStatus(response.AnalysisStatus ?? string.Empty))
        {
            AddEventCandidate(
                candidates,
                "error",
                $"error:{response.AnalysisStatus}",
                new ConsultGenerationJobStreamError(
                    response.JobId,
                    response.AnalysisError ?? "Consult generation failed.",
                    response.AnalysisStatus));
        }

        if (response.Nodes == null || response.NodeOutputs == null)
        {
            return;
        }

        var totalNodeCount = response.Nodes.Count;
        var emitted = 0;

        foreach (var node in response.Nodes)
        {
            if (!response.NodeOutputs.TryGetValue(node.Id, out var output))
            {
                continue;
            }

            // A node emits its completion at the node level (a forEach node completes
            // once every item settles; per-item progress rides the section-prose-step
            // events). Skipped/failed nodes surface through the error event and job
            // history instead.
            if (output.Status != ConsultGenerationNodeStatuses.Completed)
            {
                continue;
            }

            emitted++;
            AddEventCandidate(
                candidates,
                ConsultGenerationNodeEvents.EventName,
                $"node:{node.Id}",
                new ConsultGenerationJobNodeCompletedEvent(
                    response.JobId,
                    node.Id,
                    node.Label,
                    $"{node.Label} completed.",
                    emitted,
                    totalNodeCount));
        }
    }

    private static void AddSectionProseStepEventCandidates(
        List<ConsultGenerationJobEventCandidate> candidates,
        ConsultGenerationJobResponse response)
    {
        // Pre-milestone-3 snapshots carry no step list; their prose events were
        // materialized while they ran and replay from the event store.
        if (response.SectionProseProgress == null || response.SectionSteps is not { Count: > 0 } steps)
        {
            return;
        }

        foreach (var progress in response.SectionProseProgress.Values.OrderBy(section => section.SectionId, StringComparer.Ordinal))
        {
            var completedStepCount = Math.Clamp(progress.CompletedProseStepCount, 0, steps.Count);

            for (var stepCount = 1; stepCount <= completedStepCount; stepCount++)
            {
                var step = steps[stepCount - 1];

                AddEventCandidate(
                    candidates,
                    ConsultGenerationSectionProseSteps.EventName,
                    $"section-prose:{progress.SectionId}:{step.Id}",
                    new ConsultGenerationSectionProseStepEvent(
                        response.JobId,
                        progress.SectionId,
                        progress.SectionName,
                        step.Id,
                        step.Label,
                        $"{step.Label} completed.",
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
        if (failureText.Contains(ConsultGenerationActivityNames.RunPromptNode, StringComparison.Ordinal))
        {
            return "running a workflow step";
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

public static class ConsultGenerationNodeEvents
{
    /// <summary>The single SSE event name for every DAG node; the payload carries the node id and label.</summary>
    public const string EventName = "node-completed";
}

public sealed record ConsultGenerationJobNodeCompletedEvent(
    string JobId,
    string NodeId,
    string Label,
    string Message,
    int CompletedNodeCount,
    int TotalNodeCount);

public sealed record ConsultGenerationSectionProseStepEvent(
    string JobId,
    string SectionId,
    string SectionName,
    string Step,
    string Label,
    string Message,
    int CompletedStepCount,
    int TotalStepCount);

public sealed record ConsultGenerationRuntimeFailure(
    string Stage,
    string Error)
{
    public const string StageName = "runtime-failed";
}

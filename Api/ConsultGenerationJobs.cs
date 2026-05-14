using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Api.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Entities;
using Microsoft.Extensions.Logging;

namespace Api;

public sealed class ConsultGenerationJobs
{
    private static readonly TimeSpan SsePollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SseHeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SseStreamTimeout = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<ConsultGenerationJobs> _logger;

    public ConsultGenerationJobs(ILogger<ConsultGenerationJobs> logger)
    {
        _logger = logger;
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

            _logger.LogInformation(
                "StartConsultGenerationJob signaling job entity. InvocationId={InvocationId}, JobId={JobId}",
                req.FunctionContext.InvocationId,
                jobId);

            await client.Entities.SignalEntityAsync(
                entityId,
                nameof(ConsultGenerationJobEntity.Initialize),
                new ConsultGenerationJobInitialize(jobId, request.Sections));

            _logger.LogInformation(
                "StartConsultGenerationJob scheduling orchestration. InvocationId={InvocationId}, JobId={JobId}",
                req.FunctionContext.InvocationId,
                jobId);

            var instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
                nameof(ConsultGenerationOrchestrator),
                request,
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

        var response = await GetJobResponseAsync(client, jobId, cancellationToken);

        return response == null
            ? await CreateJsonResponseAsync(req, HttpStatusCode.NotFound, new { error = "Consult generation job was not found." }, cancellationToken)
            : await CreateJsonResponseAsync(req, HttpStatusCode.OK, response, cancellationToken);
    }

    [Function("GetConsultGenerationJobEvents")]
    public async Task<HttpResponseData> GetEventsAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "options", Route = "ConsultGenerationJobs/{jobId}/events")] HttpRequestData req,
        [DurableClient] DurableTaskClient client,
        string jobId)
    {
        var cancellationToken = req.FunctionContext.CancellationToken;

        _logger.LogInformation(
            "GetConsultGenerationJobEvents entered. InvocationId={InvocationId}, Method={Method}, Url={Url}, JobId={JobId}",
            req.FunctionContext.InvocationId,
            req.Method,
            req.Url,
            jobId);

        if (IsOptions(req))
        {
            _logger.LogInformation(
                "GetConsultGenerationJobEvents returning OPTIONS response. InvocationId={InvocationId}, JobId={JobId}",
                req.FunctionContext.InvocationId,
                jobId);

            return CreateEmptyResponse(req, HttpStatusCode.OK);
        }

        if (string.IsNullOrWhiteSpace(jobId))
        {
            return await CreateJsonResponseAsync(req, HttpStatusCode.BadRequest, new { error = "JobId is required." }, cancellationToken);
        }

        var initialResponse = await GetJobResponseAsync(client, jobId, cancellationToken);

        if (initialResponse == null)
        {
            return await CreateJsonResponseAsync(req, HttpStatusCode.NotFound, new { error = "Consult generation job was not found." }, cancellationToken);
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        FunctionCors.Apply(req, response);
        response.Headers.Add("Content-Type", "text/event-stream; charset=utf-8");
        response.Headers.Add("Cache-Control", "no-cache");

        try
        {
            await WriteSseEventAsync(response, "snapshot", initialResponse, cancellationToken);

            var seenCompletedSections = initialResponse.GeneratedSections.Keys.ToHashSet(StringComparer.Ordinal);
            var seenFailedSections = initialResponse.FailedSections.Keys.ToHashSet(StringComparer.Ordinal);

            if (IsTerminalJobStatus(initialResponse.Status))
            {
                await WriteSseEventAsync(response, "done", initialResponse, cancellationToken);
                return response;
            }

            var streamStartedAt = DateTimeOffset.UtcNow;
            var nextHeartbeatAt = streamStartedAt + SseHeartbeatInterval;

            while (!cancellationToken.IsCancellationRequested
                && DateTimeOffset.UtcNow - streamStartedAt < SseStreamTimeout)
            {
                await Task.Delay(SsePollInterval, cancellationToken);

                var latestResponse = await GetJobResponseAsync(client, jobId, cancellationToken);

                if (latestResponse == null)
                {
                    await WriteSseEventAsync(response, "error", new ConsultGenerationJobStreamError(jobId, "Consult generation job was not found."), cancellationToken);
                    return response;
                }

                foreach (var generatedSection in latestResponse.GeneratedSections)
                {
                    if (seenCompletedSections.Add(generatedSection.Key))
                    {
                        await WriteSseEventAsync(
                            response,
                            "section-completed",
                            new ConsultGenerationJobSectionCompletedEvent(jobId, generatedSection.Key, generatedSection.Value),
                            cancellationToken);
                    }
                }

                foreach (var failedSection in latestResponse.FailedSections)
                {
                    if (seenFailedSections.Add(failedSection.Key))
                    {
                        await WriteSseEventAsync(
                            response,
                            "section-failed",
                            new ConsultGenerationJobSectionFailedEvent(jobId, failedSection.Key, failedSection.Value),
                            cancellationToken);
                    }
                }

                if (IsTerminalJobStatus(latestResponse.Status))
                {
                    await WriteSseEventAsync(response, "done", latestResponse, cancellationToken);
                    return response;
                }

                if (DateTimeOffset.UtcNow >= nextHeartbeatAt)
                {
                    await WriteSseEventAsync(
                        response,
                        "heartbeat",
                        new ConsultGenerationJobHeartbeatEvent(jobId, latestResponse.Status),
                        cancellationToken);

                    nextHeartbeatAt = DateTimeOffset.UtcNow + SseHeartbeatInterval;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "Consult generation SSE stream canceled. InvocationId={InvocationId}, JobId={JobId}",
                req.FunctionContext.InvocationId,
                jobId);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Consult generation SSE stream failed. InvocationId={InvocationId}, JobId={JobId}, ExceptionType={ExceptionType}, Message={Message}",
                req.FunctionContext.InvocationId,
                jobId,
                ex.GetType().FullName,
                ex.Message);

            try
            {
                await WriteSseEventAsync(response, "error", new ConsultGenerationJobStreamError(jobId, ex.Message), cancellationToken);
            }
            catch (Exception writeException)
            {
                _logger.LogInformation(
                    writeException,
                    "Unable to write consult generation SSE error event. InvocationId={InvocationId}, JobId={JobId}",
                    req.FunctionContext.InvocationId,
                    jobId);
            }
        }

        return response;
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
                || string.IsNullOrWhiteSpace(section.Name)
                || string.IsNullOrWhiteSpace(section.Standard))
            {
                return "Each section requires Id, Name, and Standard.";
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
        CancellationToken cancellationToken)
    {
        var entityId = new EntityInstanceId(nameof(ConsultGenerationJobEntity), jobId);
        var entity = await client.Entities.GetEntityAsync<ConsultGenerationJobState>(
            entityId,
            cancellation: cancellationToken);

        var instance = await client.GetInstancesAsync(jobId, getInputsAndOutputs: false, cancellationToken);

        if (entity?.State != null)
        {
            return MergeEntityAndRuntimeStatus(
                entity.State.ToResponse(),
                instance?.RuntimeStatus);
        }

        return instance == null
            ? null
            : new ConsultGenerationJobResponse(
                jobId,
                MapRuntimeStatus(instance.RuntimeStatus),
                0,
                0,
                0,
                new Dictionary<string, string>(),
                new Dictionary<string, string>(),
                false);
    }

    private static ConsultGenerationJobResponse MergeEntityAndRuntimeStatus(
        ConsultGenerationJobResponse response,
        OrchestrationRuntimeStatus? runtimeStatus)
    {
        if (runtimeStatus == null
            || IsTerminalJobStatus(response.Status)
            || !IsTerminalRuntimeStatus(runtimeStatus.Value))
        {
            return response;
        }

        return response with
        {
            Status = MapRuntimeStatus(runtimeStatus.Value)
        };
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

    private static async Task WriteSseEventAsync<T>(
        HttpResponseData response,
        string eventName,
        T payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var message = $"event: {eventName}\ndata: {json}\n\n";
        var bytes = Encoding.UTF8.GetBytes(message);
        await response.Body.WriteAsync(bytes, cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
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
    string Error);

public sealed class ConsultGenerationOrchestrator
{
    [Function(nameof(ConsultGenerationOrchestrator))]
    public async Task RunAsync([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var request = context.GetInput<ConsultGenerationRequest>()
            ?? throw new InvalidOperationException("Consult generation request input is required.");

        var entityId = new EntityInstanceId(nameof(ConsultGenerationJobEntity), context.InstanceId);
        await context.Entities.CallEntityAsync(
            entityId,
            nameof(ConsultGenerationJobEntity.Initialize),
            new ConsultGenerationJobInitialize(context.InstanceId, request.Sections));

        await context.Entities.CallEntityAsync(entityId, nameof(ConsultGenerationJobEntity.MarkRunning));

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

        var pendingTasks = new List<Task<SectionGenerationResult>>();
        var taskSections = new Dictionary<Task<SectionGenerationResult>, ConsultGenerationSectionRequest>();

        foreach (var section in request.Sections)
        {
            var activityInput = new ConsultGenerationActivityInput(request.ConsultDraft, section);
            var task = context.CallActivityAsync<SectionGenerationResult>(
                nameof(GenerateConsultSectionActivity),
                activityInput);

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

        await context.Entities.CallEntityAsync(
            entityId,
            nameof(ConsultGenerationJobEntity.FinalizeJob),
            new ConsultGenerationJobFinalize(finalStatus));

        context.SetCustomStatus(new
        {
            status = finalStatus,
            totalSectionCount,
            completedSectionCount,
            failedSectionCount
        });
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

    [Function(nameof(GenerateConsultSectionActivity))]
    public async Task<SectionGenerationResult> RunAsync(
        [ActivityTrigger] ConsultGenerationActivityInput input,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var section = input.Section;

        try
        {
            _logger.LogInformation(
                "Starting durable section generation. SectionId={SectionId}, SectionName={SectionName}",
                section.Id,
                section.Name);

            var prose = await _sectionGenerator.GenerateSectionAsync(
                input.ConsultDraft,
                section.Name,
                section.Standard,
                cancellationToken);

            var trimmedProse = prose.Trim();

            _logger.LogInformation(
                "Durable section generation completed. SectionId={SectionId}, SectionName={SectionName}, ResponseLength={ResponseLength}, ElapsedMs={ElapsedMs}",
                section.Id,
                section.Name,
                trimmedProse.Length,
                stopwatch.ElapsedMilliseconds);

            return new SectionGenerationResult(section.Id, section.Name, true, trimmedProse, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Durable section generation failed. SectionId={SectionId}, SectionName={SectionName}, ExceptionType={ExceptionType}, Message={Message}, ElapsedMs={ElapsedMs}",
                section.Id,
                section.Name,
                ex.GetType().FullName,
                ex.Message,
                stopwatch.ElapsedMilliseconds);

            return new SectionGenerationResult(section.Id, section.Name, false, null, ex.Message);
        }
    }
}

public sealed class ConsultGenerationJobEntity : TaskEntity<ConsultGenerationJobState>
{
    public void Initialize(ConsultGenerationJobInitialize input)
    {
        if (State == null || State.Sections.Count == 0)
        {
            State = ConsultGenerationJobState.Create(input.JobId, input.Sections);
            return;
        }

        State.JobId = string.IsNullOrWhiteSpace(State.JobId) ? input.JobId : State.JobId;

        if (State.CreatedAtUtc == default)
        {
            State.CreatedAtUtc = DateTimeOffset.UtcNow;
        }

        foreach (var section in input.Sections)
        {
            State.GetOrAddSection(section.Id, section.Name);
        }
    }

    public void MarkRunning()
    {
        var state = EnsureState();
        state.Status = ConsultGenerationJobStatuses.Running;
        state.StartedAtUtc ??= DateTimeOffset.UtcNow;

        foreach (var section in state.Sections.Values.Where(section => section.Status == ConsultGenerationSectionStatuses.Pending))
        {
            section.Status = ConsultGenerationSectionStatuses.Running;
        }

        State = state;
    }

    public void CompleteSection(SectionGenerationResult result)
    {
        var state = EnsureState();
        var section = state.GetOrAddSection(result.SectionId, result.SectionName);
        section.Status = ConsultGenerationSectionStatuses.Completed;
        section.GeneratedText = result.GeneratedText ?? string.Empty;
        section.Error = null;
        section.CompletedAtUtc = DateTimeOffset.UtcNow;
        State = state;
    }

    public void FailSection(SectionGenerationResult result)
    {
        var state = EnsureState();
        var section = state.GetOrAddSection(result.SectionId, result.SectionName);
        section.Status = ConsultGenerationSectionStatuses.Failed;
        section.GeneratedText = null;
        section.Error = string.IsNullOrWhiteSpace(result.Error) ? "Section generation failed." : result.Error;
        section.CompletedAtUtc = DateTimeOffset.UtcNow;
        State = state;
    }

    public void FinalizeJob(ConsultGenerationJobFinalize input)
    {
        var state = EnsureState();
        state.Status = input.Status;
        state.CompletedAtUtc = DateTimeOffset.UtcNow;
        State = state;
    }

    [Function(nameof(ConsultGenerationJobEntity))]
    public static Task RunEntityAsync([EntityTrigger] TaskEntityDispatcher dispatcher)
    {
        return dispatcher.DispatchAsync<ConsultGenerationJobEntity>();
    }

    private ConsultGenerationJobState EnsureState()
    {
        return State ?? ConsultGenerationJobState.Create(string.Empty, Array.Empty<ConsultGenerationSectionRequest>());
    }
}

public sealed record ConsultGenerationActivityInput(
    string ConsultDraft,
    ConsultGenerationSectionRequest Section);

public sealed record ConsultGenerationJobInitialize(
    string JobId,
    IReadOnlyList<ConsultGenerationSectionRequest> Sections);

public sealed record ConsultGenerationJobFinalize(string Status);

public sealed class ConsultGenerationJobState
{
    public string JobId { get; set; } = string.Empty;
    public string Status { get; set; } = ConsultGenerationJobStatuses.Queued;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public Dictionary<string, ConsultGenerationSectionState> Sections { get; set; } = new();

    public static ConsultGenerationJobState Create(
        string jobId,
        IReadOnlyList<ConsultGenerationSectionRequest> sections)
    {
        return new ConsultGenerationJobState
        {
            JobId = jobId,
            Status = ConsultGenerationJobStatuses.Queued,
            CreatedAtUtc = DateTimeOffset.UtcNow,
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

        return new ConsultGenerationJobResponse(
            JobId,
            Status,
            Sections.Count,
            completedSections.Count,
            failedSections.Count,
            completedSections,
            failedSections,
            completedSections.Count > 0);
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
}

public static class ConsultGenerationJobStatuses
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

public static class ConsultGenerationSectionStatuses
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

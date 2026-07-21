using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using System.Runtime.CompilerServices;

namespace Consultologist.Web.Services.AI;

public interface IAIEndpointService
{
    Task<ConsultGenerationJobStartResponse> StartConsultGenerationJobAsync(
        string consultDraft,
        string? workflowPackage = null);

    Task<ConsultGenerationJobResponse> GetConsultGenerationJobAsync(string jobId);

    string GetConsultGenerationJobEventsUrl(string jobId, string attemptId, string? lastEventId = null);

    IAsyncEnumerable<ConsultGenerationJobSseEvent> StreamConsultGenerationJobEventsAsync(
        string jobId,
        string attemptId,
        CancellationToken cancellationToken,
        string? lastEventId = null);
}

public class AIEndpointService : IAIEndpointService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly IAccessTokenProvider _accessTokenProvider;
    private readonly NavigationManager _navigation;
    private readonly ILogger<AIEndpointService> _logger;

    public AIEndpointService(
        HttpClient httpClient,
        IConfiguration configuration,
        IAccessTokenProvider accessTokenProvider,
        NavigationManager navigation,
        ILogger<AIEndpointService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _accessTokenProvider = accessTokenProvider;
        _navigation = navigation;
        _logger = logger;
    }

    public async Task<ConsultGenerationJobStartResponse> StartConsultGenerationJobAsync(
        string consultDraft,
        string? workflowPackage = null)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var functionUrl = _configuration["AzureFunction:ConsultGenerationJobsUrl"];

            if (string.IsNullOrEmpty(functionUrl))
            {
                _logger.LogError("AzureFunction:ConsultGenerationJobsUrl is not configured");
                throw new InvalidOperationException("Azure Function consult generation jobs URL is not configured");
            }

            var request = new ConsultGenerationRequest(consultDraft, workflowPackage);

            _logger.LogInformation(
                "Starting consult generation job at {Url}. ConsultDraftLength={ConsultDraftLength}",
                functionUrl,
                consultDraft.Length);

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, functionUrl)
            {
                Content = JsonContent.Create(request)
            };
            await AddAuthorizationAsync(httpRequest);

            var response = await _httpClient.SendAsync(httpRequest);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError(
                    "Consult generation job start failed with status {StatusCode}: {Error}",
                    response.StatusCode,
                    errorContent);

                throw new HttpRequestException($"Azure Function call failed: {response.StatusCode}");
            }

            var result = await response.Content.ReadFromJsonAsync<ConsultGenerationJobStartResponse>();

            if (result == null)
            {
                _logger.LogError("Failed to deserialize consult generation job start response");
                throw new InvalidOperationException("Failed to deserialize response");
            }

            _logger.LogInformation(
                "Consult generation job started. JobId={JobId}, ElapsedMs={ElapsedMs}",
                result.JobId,
                stopwatch.ElapsedMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error starting consult generation job. ExceptionType={ExceptionType}, Message={Message}, ElapsedMs={ElapsedMs}",
                ex.GetType().FullName,
                ex.Message,
                stopwatch.ElapsedMilliseconds);

            throw;
        }
    }

    public async Task<ConsultGenerationJobResponse> GetConsultGenerationJobAsync(string jobId)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var functionUrl = _configuration["AzureFunction:ConsultGenerationJobsUrl"];

            if (string.IsNullOrEmpty(functionUrl))
            {
                _logger.LogError("AzureFunction:ConsultGenerationJobsUrl is not configured");
                throw new InvalidOperationException("Azure Function consult generation jobs URL is not configured");
            }

            var statusUrl = $"{functionUrl.TrimEnd('/')}/{Uri.EscapeDataString(jobId)}";

            _logger.LogDebug("Polling consult generation job at {Url}. JobId={JobId}", statusUrl, jobId);

            using var request = new HttpRequestMessage(HttpMethod.Get, statusUrl);
            await AddAuthorizationAsync(request);

            var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError(
                    "Consult generation job poll failed with status {StatusCode}: {Error}",
                    response.StatusCode,
                    errorContent);

                throw new HttpRequestException($"Azure Function call failed: {response.StatusCode}");
            }

            var result = await response.Content.ReadFromJsonAsync<ConsultGenerationJobResponse>();

            if (result == null)
            {
                _logger.LogError("Failed to deserialize consult generation job response");
                throw new InvalidOperationException("Failed to deserialize response");
            }

            _logger.LogDebug(
                "Consult generation job polled. JobId={JobId}, Status={Status}, Completed={CompletedCount}, Failed={FailedCount}, ElapsedMs={ElapsedMs}",
                result.JobId,
                result.Status,
                result.CompletedSectionCount,
                result.FailedSectionCount,
                stopwatch.ElapsedMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error polling consult generation job. JobId={JobId}, ExceptionType={ExceptionType}, Message={Message}, ElapsedMs={ElapsedMs}",
                jobId,
                ex.GetType().FullName,
                ex.Message,
                stopwatch.ElapsedMilliseconds);

            throw;
        }
    }

    public string GetConsultGenerationJobEventsUrl(string jobId, string attemptId, string? lastEventId = null)
    {
        var functionUrl = _configuration["AzureFunction:ConsultGenerationJobsUrl"];

        if (string.IsNullOrEmpty(functionUrl))
        {
            _logger.LogError("AzureFunction:ConsultGenerationJobsUrl is not configured");
            throw new InvalidOperationException("Azure Function consult generation jobs URL is not configured");
        }

        return $"{functionUrl.TrimEnd('/')}/{Uri.EscapeDataString(jobId)}/events?attemptId={Uri.EscapeDataString(attemptId)}";
    }

    public async IAsyncEnumerable<ConsultGenerationJobSseEvent> StreamConsultGenerationJobEventsAsync(
        string jobId,
        string attemptId,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        string? lastEventId = null)
    {
        var eventsUrl = GetConsultGenerationJobEventsUrl(jobId, attemptId, lastEventId);

        using var request = new HttpRequestMessage(HttpMethod.Get, eventsUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrWhiteSpace(lastEventId))
        {
            request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);
        }

        await AddAuthorizationAsync(request);
        request.SetBrowserResponseStreamingEnabled(true);

        _logger.LogInformation(
            "Opening consult generation SSE stream at {Url}. JobId={JobId}, AttemptId={AttemptId}, ResumeCursorPresent={ResumeCursorPresent}",
            eventsUrl,
            jobId,
            attemptId,
            !string.IsNullOrWhiteSpace(lastEventId));

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "Consult generation SSE stream failed with status {StatusCode}: {Error}",
                response.StatusCode,
                errorContent);

            throw new HttpRequestException($"Azure Function SSE stream failed: {response.StatusCode}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var parser = SseParser.Create(stream);

        await foreach (var item in parser.EnumerateAsync(cancellationToken))
        {
            yield return new ConsultGenerationJobSseEvent(item.EventType, item.Data, item.EventId);
        }
    }

    private async Task AddAuthorizationAsync(HttpRequestMessage request)
    {
        var apiScope = _configuration["AzureFunction:ApiScope"];

        if (string.IsNullOrWhiteSpace(apiScope))
        {
            throw new InvalidOperationException("AzureFunction:ApiScope is not configured.");
        }

        var tokenResult = await _accessTokenProvider.RequestAccessToken(new AccessTokenRequestOptions
        {
            Scopes = new[] { apiScope }
        });

        if (!tokenResult.TryGetToken(out var token))
        {
            throw new AccessTokenNotAvailableException(_navigation, tokenResult, new[] { apiScope });
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
    }
}

public record ConsultGenerationRequest(string ConsultDraft, string? WorkflowPackage = null);
public record ConsultGenerationJobStartResponse(string JobId, string StatusUrl);
public record ConsultGenerationJobSseEvent(string EventName, string Json, string? EventId = null);
public record ConsultGenerationJobResponse(
    string JobId,
    string? AppUserId,
    string Status,
    int TotalSectionCount,
    int CompletedSectionCount,
    int FailedSectionCount,
    Dictionary<string, string> GeneratedSections,
    Dictionary<string, string> FailedSections,
    bool Success,
    int? SchemaVersion = null,
    string? AnalysisStatus = null,
    string? AnalysisError = null,
    int? CompletedStageCount = null,
    int? TotalStageCount = null,
    IReadOnlyDictionary<string, ConsultGenerationSectionProseProgress>? SectionProseProgress = null,
    string? RuntimeFailureStage = null,
    string? RuntimeFailureError = null,
    IReadOnlyList<ConsultGenerationJobHistoryEvent>? History = null,
    IReadOnlyList<ConsultGenerationNodeDescriptor>? Nodes = null,
    DateTimeOffset? CreatedAtUtc = null,
    DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? CompletedAtUtc = null,
    string? WorkflowPackage = null,
    string? EffectiveInputHash = null,
    int? EffectiveInputHashVersion = null,
    IReadOnlyDictionary<string, string>? AgentVersions = null,
    string? CatalogRef = null,
    string? WorkflowOutputHash = null,
    int? WorkflowOutputHashVersion = null,
    IReadOnlyList<ConsultSectionStepDescriptor>? SectionSteps = null,
    IReadOnlyDictionary<string, ConsultGenerationNodeStatus>? NodeOutputs = null,
    // v6: the result aggregator's rendered output — the deliverable itself
    // (Completed jobs only; workflowOutputHash v2 is its digest).
    string? AssembledDocument = null);

/// <summary>
/// One node of the job's workflow DAG (v5: one kind, ForEach as multiplicity).
/// The provenance panel joins OutputContract with AgentVersions per row.
/// </summary>
public record ConsultGenerationNodeDescriptor(
    string Id,
    string Label,
    string? PromptId = null,
    string? OutputContract = null,
    string? ForEach = null);

public record ConsultSectionStepDescriptor(string Id, string Label);

/// <summary>One chain entry: keyed "nodeId" (node level) or "nodeId:itemId" (per item).</summary>
public record ConsultGenerationNodeStatus(
    string NodeId,
    string Label,
    string Status,
    string? InputHash,
    string? OutputHash,
    DateTimeOffset? CompletedAtUtc,
    string? Error);

public record ConsultGenerationJobHistoryEvent(string Kind, string Label, string? Detail, DateTimeOffset OccurredAt);

public record ConsultGenerationSectionProseProgress(
    string SectionId,
    string SectionName,
    string? ProseStepStatus,
    int CompletedProseStepCount,
    int TotalProseStepCount);

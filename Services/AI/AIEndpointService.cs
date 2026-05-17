using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using System.Runtime.CompilerServices;

namespace BlazorWasm.Services.AI;

public interface IAIEndpointService
{
    Task<string?> InvokeAgentAsync(string consultDraft, string sectionName, string sectionStandard);

    Task<ConsultGenerationResponse> GenerateConsultAsync(
        string consultDraft,
        IReadOnlyList<ConsultGenerationSectionRequest> sections);

    Task<ConsultGenerationJobStartResponse> StartConsultGenerationJobAsync(
        string consultDraft,
        IReadOnlyList<ConsultGenerationSectionRequest> sections);

    Task<ConsultGenerationJobResponse> GetConsultGenerationJobAsync(string jobId);

    string GetConsultGenerationJobEventsUrl(string jobId);

    IAsyncEnumerable<ConsultGenerationJobSseEvent> StreamConsultGenerationJobEventsAsync(
        string jobId,
        CancellationToken cancellationToken);
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

    public async Task<string?> InvokeAgentAsync(string consultDraft, string sectionName, string sectionStandard)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var functionUrl = _configuration["AzureFunction:AgentProxyUrl"];
            var timeoutSeconds = _configuration.GetValue<int?>("AzureFunction:TimeoutSeconds") ?? 240;

            if (string.IsNullOrEmpty(functionUrl))
            {
                _logger.LogError("AzureFunction:AgentProxyUrl is not configured");
                throw new InvalidOperationException("Azure Function URL is not configured");
            }

            var request = new AgentSectionRequest(consultDraft, sectionName, sectionStandard);

            _logger.LogInformation(
                "Calling Azure Function at {Url}. SectionName={SectionName}, TimeoutSeconds={TimeoutSeconds}, ConsultDraftLength={ConsultDraftLength}, SectionStandardLength={SectionStandardLength}",
                functionUrl,
                sectionName,
                timeoutSeconds,
                consultDraft.Length,
                sectionStandard.Length);

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, functionUrl)
            {
                Content = JsonContent.Create(request)
            };
            await AddAuthorizationAsync(httpRequest);

            var response = await _httpClient.SendAsync(httpRequest);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Azure Function call failed with status {StatusCode}: {Error}",
                    response.StatusCode, errorContent);
                throw new HttpRequestException($"Azure Function call failed: {response.StatusCode}");
            }

            var result = await response.Content.ReadFromJsonAsync<AgentResponse>();

            if (result == null)
            {
                _logger.LogError("Failed to deserialize Azure Function response");
                throw new InvalidOperationException("Failed to deserialize response");
            }

            if (!result.Success)
            {
                _logger.LogError("Azure Function returned error: {Error}", result.Error);
                throw new InvalidOperationException(result.Error ?? "Unknown error from Azure Function");
            }

            _logger.LogInformation(
                "AI agent completed. SectionName={SectionName}, ResponseLength={ResponseLength}, ElapsedMs={ElapsedMs}",
                sectionName,
                result.Response?.Length ?? 0,
                stopwatch.ElapsedMilliseconds);

            return result.Response;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogError(
                ex,
                "AI agent request timed out or was canceled. SectionName={SectionName}, ConfiguredTimeoutSeconds={ConfiguredTimeoutSeconds}, ElapsedMs={ElapsedMs}",
                sectionName,
                _configuration.GetValue<int?>("AzureFunction:TimeoutSeconds") ?? 240,
                stopwatch.ElapsedMilliseconds);

            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error invoking AI agent. SectionName={SectionName}, ExceptionType={ExceptionType}, Message={Message}, ElapsedMs={ElapsedMs}",
                sectionName,
                ex.GetType().FullName,
                ex.Message,
                stopwatch.ElapsedMilliseconds);

            throw;
        }
    }

    public async Task<ConsultGenerationResponse> GenerateConsultAsync(
        string consultDraft,
        IReadOnlyList<ConsultGenerationSectionRequest> sections)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var functionUrl = _configuration["AzureFunction:ConsultGenerationUrl"];
            var timeoutSeconds = _configuration.GetValue<int?>("AzureFunction:TimeoutSeconds") ?? 240;

            if (string.IsNullOrEmpty(functionUrl))
            {
                _logger.LogError("AzureFunction:ConsultGenerationUrl is not configured");
                throw new InvalidOperationException("Azure Function consult generation URL is not configured");
            }

            var request = new ConsultGenerationRequest(consultDraft, sections);

            _logger.LogInformation(
                "Calling consult generation Azure Function at {Url}. SectionCount={SectionCount}, TimeoutSeconds={TimeoutSeconds}, ConsultDraftLength={ConsultDraftLength}",
                functionUrl,
                sections.Count,
                timeoutSeconds,
                consultDraft.Length);

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, functionUrl)
            {
                Content = JsonContent.Create(request)
            };
            await AddAuthorizationAsync(httpRequest);

            var response = await _httpClient.SendAsync(httpRequest);
            var result = await response.Content.ReadFromJsonAsync<ConsultGenerationResponse>();

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = result == null
                    ? await response.Content.ReadAsStringAsync()
                    : string.Join("; ", result.FailedSections.Select(pair => $"{pair.Key}: {pair.Value}"));

                _logger.LogError(
                    "Consult generation Azure Function call failed with status {StatusCode}: {Error}",
                    response.StatusCode,
                    errorContent);

                throw new HttpRequestException($"Azure Function call failed: {response.StatusCode}");
            }

            if (result == null)
            {
                _logger.LogError("Failed to deserialize consult generation Azure Function response");
                throw new InvalidOperationException("Failed to deserialize response");
            }

            _logger.LogInformation(
                "Consult generation completed. GeneratedCount={GeneratedCount}, FailedCount={FailedCount}, ElapsedMs={ElapsedMs}",
                result.GeneratedSections.Count,
                result.FailedSections.Count,
                stopwatch.ElapsedMilliseconds);

            return result;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogError(
                ex,
                "Consult generation request timed out or was canceled. SectionCount={SectionCount}, ConfiguredTimeoutSeconds={ConfiguredTimeoutSeconds}, ElapsedMs={ElapsedMs}",
                sections.Count,
                _configuration.GetValue<int?>("AzureFunction:TimeoutSeconds") ?? 240,
                stopwatch.ElapsedMilliseconds);

            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error generating consult. SectionCount={SectionCount}, ExceptionType={ExceptionType}, Message={Message}, ElapsedMs={ElapsedMs}",
                sections.Count,
                ex.GetType().FullName,
                ex.Message,
                stopwatch.ElapsedMilliseconds);

            throw;
        }
    }

    public async Task<ConsultGenerationJobStartResponse> StartConsultGenerationJobAsync(
        string consultDraft,
        IReadOnlyList<ConsultGenerationSectionRequest> sections)
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

            var request = new ConsultGenerationRequest(consultDraft, sections);

            _logger.LogInformation(
                "Starting consult generation job at {Url}. SectionCount={SectionCount}, ConsultDraftLength={ConsultDraftLength}",
                functionUrl,
                sections.Count,
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
                "Error starting consult generation job. SectionCount={SectionCount}, ExceptionType={ExceptionType}, Message={Message}, ElapsedMs={ElapsedMs}",
                sections.Count,
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

    public string GetConsultGenerationJobEventsUrl(string jobId)
    {
        var functionUrl = _configuration["AzureFunction:ConsultGenerationJobsUrl"];

        if (string.IsNullOrEmpty(functionUrl))
        {
            _logger.LogError("AzureFunction:ConsultGenerationJobsUrl is not configured");
            throw new InvalidOperationException("Azure Function consult generation jobs URL is not configured");
        }

        return $"{functionUrl.TrimEnd('/')}/{Uri.EscapeDataString(jobId)}/events";
    }

    public async IAsyncEnumerable<ConsultGenerationJobSseEvent> StreamConsultGenerationJobEventsAsync(
        string jobId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var eventsUrl = GetConsultGenerationJobEventsUrl(jobId);

        using var request = new HttpRequestMessage(HttpMethod.Get, eventsUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        await AddAuthorizationAsync(request);
        request.SetBrowserResponseStreamingEnabled(true);

        _logger.LogInformation(
            "Opening consult generation SSE stream at {Url}. JobId={JobId}",
            eventsUrl,
            jobId);

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
            yield return new ConsultGenerationJobSseEvent(item.EventType, item.Data);
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

public record AgentSectionRequest(string ConsultDraft, string SectionName, string SectionStandard);
public record AgentResponse(string? Response, string? Error, bool Success);
public record ConsultGenerationRequest(string ConsultDraft, IReadOnlyList<ConsultGenerationSectionRequest> Sections);
public record ConsultGenerationSectionRequest(string Id, string Name, string Standard);
public record ConsultGenerationResponse(Dictionary<string, string> GeneratedSections, Dictionary<string, string> FailedSections, bool Success);
public record ConsultGenerationJobStartResponse(string JobId, string StatusUrl);
public record ConsultGenerationJobSseEvent(string EventName, string Json);
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
    IReadOnlyDictionary<string, ConsultGenerationSectionProseProgress>? SectionProseProgress = null);

public record ConsultGenerationSectionProseProgress(
    string SectionId,
    string SectionName,
    string? ProseStepStatus,
    int CompletedProseStepCount,
    int TotalProseStepCount);

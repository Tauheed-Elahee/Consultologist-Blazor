using System.Diagnostics;
using System.Net.Http.Json;

namespace BlazorWasm.Services.AI;

public interface IAIEndpointService
{
    Task<string?> InvokeAgentAsync(string consultDraft, string sectionName, string sectionStandard);

    Task<ConsultGenerationResponse> GenerateConsultAsync(
        string consultDraft,
        IReadOnlyList<ConsultGenerationSectionRequest> sections);
}

public class AIEndpointService : IAIEndpointService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AIEndpointService> _logger;

    public AIEndpointService(HttpClient httpClient, IConfiguration configuration, ILogger<AIEndpointService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
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

            var response = await _httpClient.PostAsJsonAsync(functionUrl, request);

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

            var response = await _httpClient.PostAsJsonAsync(functionUrl, request);
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
}

public record AgentSectionRequest(string ConsultDraft, string SectionName, string SectionStandard);
public record AgentResponse(string? Response, string? Error, bool Success);
public record ConsultGenerationRequest(string ConsultDraft, IReadOnlyList<ConsultGenerationSectionRequest> Sections);
public record ConsultGenerationSectionRequest(string Id, string Name, string Standard);
public record ConsultGenerationResponse(Dictionary<string, string> GeneratedSections, Dictionary<string, string> FailedSections, bool Success);

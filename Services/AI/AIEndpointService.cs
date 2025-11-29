using System.Net.Http.Json;

namespace BlazorWasm.Services.AI;

public interface IAIEndpointService
{
    Task<string?> InvokeAgentAsync(string consultDraft, string jsonSchema);
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

    public async Task<string?> InvokeAgentAsync(string consultDraft, string jsonSchema)
    {
        try
        {
            var functionUrl = _configuration["AzureFunction:AgentProxyUrl"];

            if (string.IsNullOrEmpty(functionUrl))
            {
                _logger.LogError("AzureFunction:AgentProxyUrl is not configured");
                throw new InvalidOperationException("Azure Function URL is not configured");
            }

            var request = new AgentRequest(consultDraft, jsonSchema);

            _logger.LogInformation("Calling Azure Function at {Url}", functionUrl);

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

            return result.Response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error invoking AI agent");
            throw;
        }
    }
}

public record AgentRequest(string ConsultDraft, string JsonSchema);
public record AgentResponse(string? Response, string? Error, bool Success);

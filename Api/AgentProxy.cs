using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Api.Models;

namespace Api;

// Attempt to force a run on GitHub Actions
public class AgentProxy
{
    private readonly ILogger<AgentProxy> _logger;
    private readonly HttpClient _httpClient;
    private readonly TokenCredential _credential;

    public AgentProxy(ILogger<AgentProxy> logger, IHttpClientFactory httpClientFactory, TokenCredential credential)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient();
        _credential = credential;
    }

    // TODO: Change AuthorizationLevel.Anonymous to AuthorizationLevel.Function for production
    // NOTE: AuthorizationLevel.Anonymous is for development purposes only
    [Function("AgentProxy")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "post", "options")] HttpRequest req)
    {
        // Handle CORS preflight
        if (req.Method == "OPTIONS")
        {
            req.HttpContext.Response.StatusCode = 200;
            return new OkResult();
        }

        try
        {
            // Read and parse request body
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();

            _logger.LogInformation("Received request body: {Body}", requestBody);

            var agentRequest = JsonSerializer.Deserialize<AgentRequest>(requestBody, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (agentRequest == null || string.IsNullOrEmpty(agentRequest.ConsultDraft) || string.IsNullOrEmpty(agentRequest.JsonSchema))
            {
                _logger.LogWarning("Invalid request: agentRequest={AgentRequest}, ConsultDraft={ConsultDraft}, JsonSchema={JsonSchema}",
                    agentRequest, agentRequest?.ConsultDraft, agentRequest?.JsonSchema);
                return new BadRequestObjectResult(new AgentResponse(null, "Invalid request: ConsultDraft and JsonSchema are required", false));
            }

            // Get configuration from environment variables
            var endpoint = Environment.GetEnvironmentVariable("AzureAI__Endpoint");
            var agentId = Environment.GetEnvironmentVariable("AzureAI__AgentId");
            var apiVersion = Environment.GetEnvironmentVariable("AzureAI__ApiVersion") ?? "2025-05-01";

            if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(agentId))
            {
                return new ObjectResult(new AgentResponse(null, "Azure AI configuration missing", false)) { StatusCode = 500 };
            }

            // Get Azure AD token for Azure AI Foundry
            var tokenRequestContext = new TokenRequestContext(new[] { "https://ai.azure.com/.default" });

            AccessToken token;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                token = await _credential.GetTokenAsync(tokenRequestContext, cts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogError("Token acquisition timed out");
                return new ObjectResult(new AgentResponse(null, "Authentication timeout", false)) { StatusCode = 500 };
            }
            catch (AuthenticationFailedException ex)
            {
                _logger.LogError(ex, "Authentication failed");
                return new ObjectResult(new AgentResponse(null, "Authentication failed", false)) { StatusCode = 500 };
            }

            // Configure HTTP client with bearer token
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            // Step 1: Create thread
            var threadUrl = $"{endpoint}/threads?api-version={apiVersion}";
            var threadResponse = await _httpClient.PostAsync(threadUrl, new StringContent("{}", Encoding.UTF8, "application/json"));

            if (!threadResponse.IsSuccessStatusCode)
            {
                var errorContent = await threadResponse.Content.ReadAsStringAsync();
                _logger.LogError($"Failed to create thread: {errorContent}");
                return new ObjectResult(new AgentResponse(null, $"Failed to create thread: {threadResponse.StatusCode}", false)) { StatusCode = 500 };
            }

            var threadData = await threadResponse.Content.ReadAsStringAsync();
            var threadJson = JsonNode.Parse(threadData);
            var threadId = threadJson?["id"]?.GetValue<string>();

            if (string.IsNullOrEmpty(threadId))
            {
                return new ObjectResult(new AgentResponse(null, "Failed to get thread ID", false)) { StatusCode = 500 };
            }

            // Step 2: Add message to thread
            var userMessage = $"ConsultDraft:\n{agentRequest.ConsultDraft}\n\nJSON Schema:\n{agentRequest.JsonSchema}";
            var messagePayload = new
            {
                role = "user",
                content = userMessage
            };

            var messageUrl = $"{endpoint}/threads/{threadId}/messages?api-version={apiVersion}";
            var messageResponse = await _httpClient.PostAsync(messageUrl,
                new StringContent(JsonSerializer.Serialize(messagePayload), Encoding.UTF8, "application/json"));

            if (!messageResponse.IsSuccessStatusCode)
            {
                var errorContent = await messageResponse.Content.ReadAsStringAsync();
                _logger.LogError($"Failed to add message: {errorContent}");
                return new ObjectResult(new AgentResponse(null, $"Failed to add message: {messageResponse.StatusCode}", false)) { StatusCode = 500 };
            }

            // Step 3: Create run
            var runPayload = new
            {
                assistant_id = agentId
            };

            var runUrl = $"{endpoint}/threads/{threadId}/runs?api-version={apiVersion}";
            var runResponse = await _httpClient.PostAsync(runUrl,
                new StringContent(JsonSerializer.Serialize(runPayload), Encoding.UTF8, "application/json"));

            if (!runResponse.IsSuccessStatusCode)
            {
                var errorContent = await runResponse.Content.ReadAsStringAsync();
                _logger.LogError($"Failed to create run: {errorContent}");
                return new ObjectResult(new AgentResponse(null, $"Failed to create run: {runResponse.StatusCode}", false)) { StatusCode = 500 };
            }

            var runData = await runResponse.Content.ReadAsStringAsync();
            var runJson = JsonNode.Parse(runData);
            var runId = runJson?["id"]?.GetValue<string>();

            if (string.IsNullOrEmpty(runId))
            {
                return new ObjectResult(new AgentResponse(null, "Failed to get run ID", false)) { StatusCode = 500 };
            }

            // Step 4: Poll run status
            var statusUrl = $"{endpoint}/threads/{threadId}/runs/{runId}?api-version={apiVersion}";
            string? status = null;
            int maxAttempts = 60; // 60 attempts * 1 second = 1 minute timeout
            int attempts = 0;

            while (attempts < maxAttempts)
            {
                var statusResponse = await _httpClient.GetAsync(statusUrl);

                if (!statusResponse.IsSuccessStatusCode)
                {
                    var errorContent = await statusResponse.Content.ReadAsStringAsync();
                    _logger.LogError($"Failed to check run status: {errorContent}");
                    return new ObjectResult(new AgentResponse(null, $"Failed to check run status: {statusResponse.StatusCode}", false)) { StatusCode = 500 };
                }

                var statusData = await statusResponse.Content.ReadAsStringAsync();
                var statusJson = JsonNode.Parse(statusData);
                status = statusJson?["status"]?.GetValue<string>();

                if (status == "completed")
                {
                    break;
                }
                else if (status == "failed" || status == "cancelled" || status == "expired")
                {
                    return new ObjectResult(new AgentResponse(null, $"Run ended with status: {status}", false)) { StatusCode = 500 };
                }

                await Task.Delay(1000);
                attempts++;
            }

            if (status != "completed")
            {
                return new ObjectResult(new AgentResponse(null, "Run timed out", false)) { StatusCode = 500 };
            }

            // Step 5: Get messages
            var messagesUrl = $"{endpoint}/threads/{threadId}/messages?api-version={apiVersion}";
            var messagesResponse = await _httpClient.GetAsync(messagesUrl);

            if (!messagesResponse.IsSuccessStatusCode)
            {
                var errorContent = await messagesResponse.Content.ReadAsStringAsync();
                _logger.LogError($"Failed to get messages: {errorContent}");
                return new ObjectResult(new AgentResponse(null, $"Failed to get messages: {messagesResponse.StatusCode}", false)) { StatusCode = 500 };
            }

            var messagesData = await messagesResponse.Content.ReadAsStringAsync();
            var messagesJson = JsonNode.Parse(messagesData);
            var dataArray = messagesJson?["data"]?.AsArray();

            if (dataArray == null || dataArray.Count == 0)
            {
                return new ObjectResult(new AgentResponse(null, "No messages received", false)) { StatusCode = 500 };
            }

            // Get the first assistant message (most recent)
            foreach (var message in dataArray)
            {
                var role = message?["role"]?.GetValue<string>();
                if (role == "assistant")
                {
                    var content = message?["content"]?.AsArray()?[0]?["text"]?["value"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(content))
                    {
                        return new OkObjectResult(new AgentResponse(content, null, true));
                    }
                }
            }

            return new ObjectResult(new AgentResponse(null, "No assistant response found", false)) { StatusCode = 500 };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in AgentProxy function");
            return new ObjectResult(new AgentResponse(null, $"Internal error: {ex.Message}", false)) { StatusCode = 500 };
        }
    }
}

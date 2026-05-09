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
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "post", "options")] HttpRequest req)
    {
        // Add CORS headers
        var origin = req.Headers["Origin"].ToString();
        var allowedOrigins = new[]
        {
            "https://app.consultologist.ai",
            "https://gentle-desert-09697700f.3.azurestaticapps.net",
            "http://localhost:3000",
            "http://localhost:5000",
            "http://localhost:5173",
            "http://localhost:5174",
            "http://localhost:7071"
        };

        if (allowedOrigins.Contains(origin))
        {
            req.HttpContext.Response.Headers.Append("Access-Control-Allow-Origin", origin);
            req.HttpContext.Response.Headers.Append("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            req.HttpContext.Response.Headers.Append("Access-Control-Allow-Headers", "Content-Type, Authorization");
        }

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

            var agentRequest = JsonSerializer.Deserialize<AgentSectionRequest>(requestBody, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (agentRequest == null
                || string.IsNullOrWhiteSpace(agentRequest.ConsultDraft)
                || string.IsNullOrWhiteSpace(agentRequest.SectionName)
                || string.IsNullOrWhiteSpace(agentRequest.SectionStandard))
            {
                _logger.LogWarning("Invalid request: agentRequest={AgentRequest}, ConsultDraft={ConsultDraft}, SectionName={SectionName}, SectionStandard={SectionStandard}",
                    agentRequest, agentRequest?.ConsultDraft, agentRequest?.SectionName, agentRequest?.SectionStandard);
                return new BadRequestObjectResult(new AgentResponse(null, "Invalid request: ConsultDraft, SectionName, and SectionStandard are required", false));
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
            var userMessage = $"""
                You are writing one section of an oncology consult note.

                Source of truth:
                Use only the clinical facts contained in the draft consult note below.

                Section standard:
                The following standard shows the desired organization, tone, level of detail, and clinical writing style.
                It is not patient data. Do not copy diagnoses, staging, receptor values, dates, pathology details, treatments, scores, symptoms, or outcomes from the standard unless they are also present in the draft consult note.

                Missing information rule:
                Do not invent missing pathology, dates, staging, receptor status, genomic scores, medications, allergies, physical exam findings, or treatment decisions.
                If a detail is not present in the draft, omit it unless the section would be misleading without it, in which case write "not documented."

                Output rule:
                Return only the final prose for the requested section. Do not include a heading, JSON, markdown, bullets, or commentary.

                Requested section:
                {agentRequest.SectionName}

                Section standard:
                {agentRequest.SectionStandard}

                Draft consult note:
                {agentRequest.ConsultDraft}
                """;
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
                assistant_id = agentId,
                temperature = 0
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

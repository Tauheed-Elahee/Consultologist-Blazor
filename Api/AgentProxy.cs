using System.Net.Http.Headers;
using System.Net;
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
            var agentName = Environment.GetEnvironmentVariable("AzureAI__AgentName");
            var agentVersion = Environment.GetEnvironmentVariable("AzureAI__AgentVersion");
            var apiVersion = Environment.GetEnvironmentVariable("AzureAI__ApiVersion") ?? "v1";

            if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(agentName) || string.IsNullOrEmpty(agentVersion))
            {
                return new ObjectResult(new AgentResponse(null, "Azure AI configuration missing: AzureAI__Endpoint, AzureAI__AgentName, and AzureAI__AgentVersion are required", false)) { StatusCode = 500 };
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

            var responsesUrl = $"{endpoint.TrimEnd('/')}/openai/v1/responses?api-version={apiVersion}";
            var foundryResponse = await _httpClient.PostAsync(responsesUrl,
                new StringContent(CreateResponsePayload(userMessage, agentName, agentVersion, includeAgentVersion: true), Encoding.UTF8, "application/json"));

            var responseData = await foundryResponse.Content.ReadAsStringAsync();
            if (foundryResponse.StatusCode == HttpStatusCode.BadRequest)
            {
                _logger.LogWarning("Foundry responses API rejected agent version payload. Retrying without agent_reference.version. Original response: {Body}", responseData);

                foundryResponse = await _httpClient.PostAsync(responsesUrl,
                    new StringContent(CreateResponsePayload(userMessage, agentName, agentVersion, includeAgentVersion: false), Encoding.UTF8, "application/json"));
                responseData = await foundryResponse.Content.ReadAsStringAsync();
            }

            if (!foundryResponse.IsSuccessStatusCode)
            {
                _logger.LogError("Foundry responses API failed with status {StatusCode}: {Body}", foundryResponse.StatusCode, responseData);
                return new ObjectResult(new AgentResponse(null, $"Azure AI request failed: {foundryResponse.StatusCode}", false)) { StatusCode = 500 };
            }

            var assistantText = ExtractResponseText(responseData);
            if (!string.IsNullOrWhiteSpace(assistantText))
            {
                return new OkObjectResult(new AgentResponse(assistantText, null, true));
            }

            return new ObjectResult(new AgentResponse(null, "No assistant response found", false)) { StatusCode = 500 };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in AgentProxy function");
            return new ObjectResult(new AgentResponse(null, $"Internal error: {ex.Message}", false)) { StatusCode = 500 };
        }
    }

    private static string CreateResponsePayload(string input, string agentName, string agentVersion, bool includeAgentVersion)
    {
        var agentReference = new JsonObject
        {
            ["name"] = agentName,
            ["type"] = "agent_reference"
        };

        if (includeAgentVersion)
        {
            agentReference["version"] = agentVersion;
        }

        var payload = new JsonObject
        {
            ["input"] = input,
            ["store"] = false,
            ["agent_reference"] = agentReference
        };

        return payload.ToJsonString();
    }

    private static string? ExtractResponseText(string responseData)
    {
        var responseJson = JsonNode.Parse(responseData);
        var outputText = responseJson?["output_text"]?.GetValue<string>();

        if (!string.IsNullOrWhiteSpace(outputText))
        {
            return outputText;
        }

        var output = responseJson?["output"]?.AsArray();
        if (output == null)
        {
            return null;
        }

        foreach (var outputItem in output)
        {
            var type = outputItem?["type"]?.GetValue<string>();
            if (type != "message" && type != "output_message")
            {
                continue;
            }

            var content = outputItem?["content"]?.AsArray();
            if (content == null)
            {
                continue;
            }

            foreach (var contentItem in content)
            {
                var text = contentItem?["text"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return null;
    }
}

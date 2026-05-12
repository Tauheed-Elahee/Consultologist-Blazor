using System.Diagnostics;
using System.Text.Json;
using Azure.Core;
using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenAI.Responses;
using System.ClientModel;
using Api.Models;

namespace Api;

public class AgentProxy
{
    private readonly ILogger<AgentProxy> _logger;
    private readonly TokenCredential _credential;

    public AgentProxy(ILogger<AgentProxy> logger, TokenCredential credential)
    {
        _logger = logger;
        _credential = credential;
    }

    // TODO: Change AuthorizationLevel.Anonymous to AuthorizationLevel.Function for production
    // NOTE: AuthorizationLevel.Anonymous is for development purposes only
    [Function("AgentProxy")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "post", "options")] HttpRequest req)
    {
        var requestStopwatch = Stopwatch.StartNew();
        var stage = "start";

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
            stage = "read-request-body";

            // Read and parse request body
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();

            _logger.LogInformation("Received request body: {Body}", requestBody);

            stage = "deserialize-request";

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

            _logger.LogInformation(
                "AgentProxy request validated. SectionName={SectionName}, ConsultDraftLength={ConsultDraftLength}, SectionStandardLength={SectionStandardLength}, TraceIdentifier={TraceIdentifier}",
                agentRequest.SectionName,
                agentRequest.ConsultDraft.Length,
                agentRequest.SectionStandard.Length,
                req.HttpContext.TraceIdentifier);

            stage = "read-configuration";

            // Get configuration from environment variables
            var endpoint = Environment.GetEnvironmentVariable("AzureAI__Endpoint");
            var agentName = Environment.GetEnvironmentVariable("AzureAI__AgentName");
            var agentVersion = Environment.GetEnvironmentVariable("AzureAI__AgentVersion");
            var azureClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");

            if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(agentName) || string.IsNullOrEmpty(agentVersion))
            {
                _logger.LogError(
                    "Azure AI configuration missing. HasEndpoint={HasEndpoint}, HasAgentName={HasAgentName}, HasAgentVersion={HasAgentVersion}",
                    !string.IsNullOrEmpty(endpoint),
                    !string.IsNullOrEmpty(agentName),
                    !string.IsNullOrEmpty(agentVersion));

                return new ObjectResult(new AgentResponse(null, "Azure AI configuration missing: AzureAI__Endpoint, AzureAI__AgentName, and AzureAI__AgentVersion are required", false)) { StatusCode = 500 };
            }

            var endpointUri = new Uri(endpoint);

            _logger.LogInformation(
                "Azure AI configuration loaded. EndpointHost={EndpointHost}, AgentName={AgentName}, AgentVersion={AgentVersion}, HasAzureClientId={HasAzureClientId}",
                endpointUri.Host,
                agentName,
                agentVersion,
                !string.IsNullOrWhiteSpace(azureClientId));

            stage = "build-user-message";

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

            ResponseResult sdkResponse;
            try
            {
                stage = "create-project-client";
                var projectClient = new AIProjectClient(endpointUri, _credential);

                stage = "send-foundry-request-with-version";
                _logger.LogInformation(
                    "Sending Foundry agent request. SectionName={SectionName}, AgentName={AgentName}, AgentVersion={AgentVersion}, MessageLength={MessageLength}",
                    agentRequest.SectionName,
                    agentName,
                    agentVersion,
                    userMessage.Length);

                var sdkStopwatch = Stopwatch.StartNew();
                sdkResponse = await CreateAgentResponseAsync(
                    projectClient.ProjectOpenAIClient,
                    new AgentReference(agentName, agentVersion),
                    userMessage,
                    req.HttpContext.RequestAborted);

                _logger.LogInformation(
                    "Foundry agent request completed with version. SectionName={SectionName}, ElapsedMs={ElapsedMs}",
                    agentRequest.SectionName,
                    sdkStopwatch.ElapsedMilliseconds);
            }
            catch (ClientResultException ex) when (ex.Status == 400)
            {
                _logger.LogWarning(ex, "Foundry SDK rejected agent version payload. Retrying without agent_reference.version.");

                try
                {
                    stage = "create-project-client-retry-name-only";
                    var projectClient = new AIProjectClient(endpointUri, _credential);

                    stage = "send-foundry-request-name-only";
                    _logger.LogInformation(
                        "Retrying Foundry agent request without version. SectionName={SectionName}, AgentName={AgentName}, MessageLength={MessageLength}",
                        agentRequest.SectionName,
                        agentName,
                        userMessage.Length);

                    var retryStopwatch = Stopwatch.StartNew();
                    sdkResponse = await CreateAgentResponseAsync(
                        projectClient.ProjectOpenAIClient,
                        new AgentReference(agentName),
                        userMessage,
                        req.HttpContext.RequestAborted);

                    _logger.LogInformation(
                        "Foundry agent request completed without version. SectionName={SectionName}, ElapsedMs={ElapsedMs}",
                        agentRequest.SectionName,
                        retryStopwatch.ElapsedMilliseconds);
                }
                catch (ClientResultException retryEx)
                {
                    _logger.LogError(retryEx, "Foundry SDK request failed after name-only retry with status {StatusCode}", retryEx.Status);
                    return new ObjectResult(new AgentResponse(null, $"Azure AI request failed: {retryEx.Status}", false)) { StatusCode = 500 };
                }
            }
            catch (OperationCanceledException ex) when (!req.HttpContext.RequestAborted.IsCancellationRequested)
            {
                _logger.LogError(
                    ex,
                    "Azure AI SDK request timed out. Stage={Stage}, ExceptionType={ExceptionType}, Message={Message}, ElapsedMs={ElapsedMs}",
                    stage,
                    ex.GetType().FullName,
                    ex.Message,
                    requestStopwatch.ElapsedMilliseconds);

                return new ObjectResult(new AgentResponse(null, "Azure AI request timeout", false)) { StatusCode = 500 };
            }
            catch (Exception ex) when (ex.GetType().FullName == "Azure.Identity.AuthenticationFailedException")
            {
                _logger.LogError(
                    ex,
                    "Authentication failed. Stage={Stage}, ExceptionType={ExceptionType}, Message={Message}, ElapsedMs={ElapsedMs}",
                    stage,
                    ex.GetType().FullName,
                    ex.Message,
                    requestStopwatch.ElapsedMilliseconds);

                return new ObjectResult(new AgentResponse(null, "Authentication failed", false)) { StatusCode = 500 };
            }
            catch (ClientResultException ex)
            {
                _logger.LogError(
                    ex,
                    "Foundry SDK request failed. Stage={Stage}, StatusCode={StatusCode}, ExceptionType={ExceptionType}, Message={Message}, ElapsedMs={ElapsedMs}",
                    stage,
                    ex.Status,
                    ex.GetType().FullName,
                    ex.Message,
                    requestStopwatch.ElapsedMilliseconds);

                return new ObjectResult(new AgentResponse(null, $"Azure AI request failed: {ex.Status}", false)) { StatusCode = 500 };
            }

            stage = "extract-assistant-text";
            var assistantText = sdkResponse.GetOutputText();
            if (!string.IsNullOrWhiteSpace(assistantText))
            {
                _logger.LogInformation(
                    "AgentProxy completed successfully. SectionName={SectionName}, ResponseLength={ResponseLength}, ElapsedMs={ElapsedMs}",
                    agentRequest.SectionName,
                    assistantText.Length,
                    requestStopwatch.ElapsedMilliseconds);

                return new OkObjectResult(new AgentResponse(assistantText, null, true));
            }

            _logger.LogError(
                "Foundry response did not contain assistant text. SectionName={SectionName}, ElapsedMs={ElapsedMs}",
                agentRequest.SectionName,
                requestStopwatch.ElapsedMilliseconds);

            return new ObjectResult(new AgentResponse(null, "No assistant response found", false)) { StatusCode = 500 };
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error in AgentProxy function. Stage={Stage}, ExceptionType={ExceptionType}, Message={Message}, ElapsedMs={ElapsedMs}",
                stage,
                ex.GetType().FullName,
                ex.Message,
                requestStopwatch.ElapsedMilliseconds);

            return new ObjectResult(new AgentResponse(null, $"Internal error: {ex.Message}", false)) { StatusCode = 500 };
        }
    }

    private static async Task<ResponseResult> CreateAgentResponseAsync(
        ProjectOpenAIClient projectOpenAIClient,
        AgentReference agentReference,
        string userMessage,
        CancellationToken cancellationToken)
    {
        var responsesClient = projectOpenAIClient.GetProjectResponsesClientForAgent(agentReference);
        var options = new CreateResponseOptions
        {
            StoredOutputEnabled = false
        };

        options.InputItems.Add(ResponseItem.CreateUserMessageItem(userMessage));

        var response = await responsesClient.CreateResponseAsync(options, cancellationToken);
        return response.Value;
    }
}

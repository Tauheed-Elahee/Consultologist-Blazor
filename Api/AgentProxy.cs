using System.Diagnostics;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Api.Models;

namespace Api;

public class AgentProxy
{
    private readonly ILogger<AgentProxy> _logger;
    private readonly AgentSectionGenerator _sectionGenerator;

    public AgentProxy(ILogger<AgentProxy> logger, AgentSectionGenerator sectionGenerator)
    {
        _logger = logger;
        _sectionGenerator = sectionGenerator;
    }

    // TODO: Change AuthorizationLevel.Anonymous to AuthorizationLevel.Function for production
    // NOTE: AuthorizationLevel.Anonymous is for development purposes only
    [Function("AgentProxy")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "post", "options")] HttpRequest req)
    {
        var requestStopwatch = Stopwatch.StartNew();
        var stage = "start";

        FunctionCors.Apply(req);

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

            try
            {
                stage = "generate-section";
                var sdkStopwatch = Stopwatch.StartNew();
                var assistantText = await _sectionGenerator.GenerateSectionAsync(
                    agentRequest.ConsultDraft,
                    agentRequest.SectionName,
                    agentRequest.SectionStandard,
                    req.HttpContext.RequestAborted);

                _logger.LogInformation(
                    "AgentProxy completed successfully. SectionName={SectionName}, ResponseLength={ResponseLength}, ElapsedMs={ElapsedMs}",
                    agentRequest.SectionName,
                    assistantText.Length,
                    sdkStopwatch.ElapsedMilliseconds);

                return new OkObjectResult(new AgentResponse(assistantText, null, true));
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
            catch (System.ClientModel.ClientResultException ex)
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
            catch (InvalidOperationException ex)
            {
                _logger.LogError(
                    ex,
                    "Agent section generation failed. Stage={Stage}, ExceptionType={ExceptionType}, Message={Message}, ElapsedMs={ElapsedMs}",
                    stage,
                    ex.GetType().FullName,
                    ex.Message,
                    requestStopwatch.ElapsedMilliseconds);

                return new ObjectResult(new AgentResponse(null, ex.Message, false)) { StatusCode = 500 };
            }
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
}

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
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
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", "options")] HttpRequestData req)
    {
        var requestStopwatch = Stopwatch.StartNew();
        var stage = "start";
        var cancellationToken = req.FunctionContext.CancellationToken;

        if (IsOptions(req))
        {
            return CreateEmptyResponse(req, HttpStatusCode.OK);
        }

        try
        {
            stage = "read-request-body";

            // Read and parse request body
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync(cancellationToken);

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
                return await CreateJsonResponseAsync(req, HttpStatusCode.BadRequest, new AgentResponse(null, "Invalid request: ConsultDraft, SectionName, and SectionStandard are required", false), cancellationToken);
            }

            _logger.LogInformation(
                "AgentProxy request validated. SectionName={SectionName}, ConsultDraftLength={ConsultDraftLength}, SectionStandardLength={SectionStandardLength}, TraceIdentifier={TraceIdentifier}",
                agentRequest.SectionName,
                agentRequest.ConsultDraft.Length,
                agentRequest.SectionStandard.Length,
                req.FunctionContext.InvocationId);

            try
            {
                stage = "generate-section";
                var sdkStopwatch = Stopwatch.StartNew();
                var assistantText = await _sectionGenerator.GenerateSectionAsync(
                    agentRequest.ConsultDraft,
                    agentRequest.SectionName,
                    agentRequest.SectionStandard,
                    cancellationToken);

                _logger.LogInformation(
                    "AgentProxy completed successfully. SectionName={SectionName}, ResponseLength={ResponseLength}, ElapsedMs={ElapsedMs}",
                    agentRequest.SectionName,
                    assistantText.Length,
                    sdkStopwatch.ElapsedMilliseconds);

                return await CreateJsonResponseAsync(req, HttpStatusCode.OK, new AgentResponse(assistantText, null, true), cancellationToken);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(
                    ex,
                    "Azure AI SDK request timed out. Stage={Stage}, ExceptionType={ExceptionType}, Message={Message}, ElapsedMs={ElapsedMs}",
                    stage,
                    ex.GetType().FullName,
                    ex.Message,
                    requestStopwatch.ElapsedMilliseconds);

                return await CreateJsonResponseAsync(req, HttpStatusCode.InternalServerError, new AgentResponse(null, "Azure AI request timeout", false), cancellationToken);
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

                return await CreateJsonResponseAsync(req, HttpStatusCode.InternalServerError, new AgentResponse(null, "Authentication failed", false), cancellationToken);
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

                return await CreateJsonResponseAsync(req, HttpStatusCode.InternalServerError, new AgentResponse(null, $"Azure AI request failed: {ex.Status}", false), cancellationToken);
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

                return await CreateJsonResponseAsync(req, HttpStatusCode.InternalServerError, new AgentResponse(null, ex.Message, false), cancellationToken);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "Malformed AgentProxy request JSON. Stage={Stage}, ElapsedMs={ElapsedMs}",
                stage,
                requestStopwatch.ElapsedMilliseconds);

            return await CreateJsonResponseAsync(req, HttpStatusCode.BadRequest, new AgentResponse(null, "Malformed JSON request body.", false), cancellationToken);
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

            return await CreateJsonResponseAsync(req, HttpStatusCode.InternalServerError, new AgentResponse(null, $"Internal error: {ex.Message}", false), cancellationToken);
        }
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
}

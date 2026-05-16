using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Api.Auth;
using Api.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Api;

public sealed class ConsultGeneration
{
    private readonly ILogger<ConsultGeneration> _logger;
    private readonly AgentSectionGenerator _sectionGenerator;
    private readonly IAccountAuthorizer _authorizer;

    public ConsultGeneration(
        ILogger<ConsultGeneration> logger,
        AgentSectionGenerator sectionGenerator,
        IAccountAuthorizer authorizer)
    {
        _logger = logger;
        _sectionGenerator = sectionGenerator;
        _authorizer = authorizer;

        Console.Error.WriteLine($"[Api.StartupDiagnostics] ConsultGeneration constructed. Utc={DateTimeOffset.UtcNow:O}");
        _logger.LogInformation("ConsultGeneration constructed.");
    }

    [Function("ConsultGeneration")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", "options")] HttpRequestData req)
    {
        var requestStopwatch = Stopwatch.StartNew();
        var cancellationToken = req.FunctionContext.CancellationToken;

        _logger.LogInformation(
            "ConsultGeneration entered. InvocationId={InvocationId}, Method={Method}, Url={Url}",
            req.FunctionContext.InvocationId,
            req.Method,
            req.Url);

        if (IsOptions(req))
        {
            _logger.LogInformation(
                "ConsultGeneration returning OPTIONS response. InvocationId={InvocationId}",
                req.FunctionContext.InvocationId);

            return CreateEmptyResponse(req, HttpStatusCode.OK);
        }

        var account = await _authorizer.AuthorizeAsync(req, cancellationToken);

        if (account == null)
        {
            return AccountAuthorizer.CreateUnauthorizedResponse(req);
        }

        if (!AccountAuthorizer.IsActive(account))
        {
            return AccountAuthorizer.CreateForbiddenResponse(req);
        }

        try
        {
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync(cancellationToken);
            ConsultGenerationRequest? generationRequest = null;

            if (!string.IsNullOrWhiteSpace(requestBody))
            {
                try
                {
                    generationRequest = JsonSerializer.Deserialize<ConsultGenerationRequest>(requestBody, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                }
                catch (JsonException ex)
                {
                    const string malformedJsonError = "Malformed JSON request body.";

                    _logger.LogWarning(
                        ex,
                        "Invalid ConsultGeneration request: {ValidationError}",
                        malformedJsonError);

                    return await CreateJsonResponseAsync(req, HttpStatusCode.BadRequest, new ConsultGenerationResponse(new(), new() { ["request"] = malformedJsonError }, false), cancellationToken);
                }
            }

            var validationError = ValidateRequest(generationRequest);
            if (validationError != null)
            {
                _logger.LogWarning("Invalid ConsultGeneration request: {ValidationError}", validationError);
                return await CreateJsonResponseAsync(req, HttpStatusCode.BadRequest, new ConsultGenerationResponse(new(), new() { ["request"] = validationError }, false), cancellationToken);
            }

            var request = generationRequest!;
            var generatedSections = new ConcurrentDictionary<string, string>();
            var failedSections = new ConcurrentDictionary<string, string>();

            _logger.LogInformation(
                "ConsultGeneration request validated. SectionCount={SectionCount}, ConsultDraftLength={ConsultDraftLength}, TraceIdentifier={TraceIdentifier}",
                request.Sections.Count,
                request.ConsultDraft.Length,
                req.FunctionContext.InvocationId);

            var tasks = request.Sections.Select(section => GenerateSectionAsync(
                request.ConsultDraft,
                section,
                generatedSections,
                failedSections,
                cancellationToken));

            await Task.WhenAll(tasks);

            var generated = generatedSections.ToDictionary(pair => pair.Key, pair => pair.Value);
            var failed = failedSections.ToDictionary(pair => pair.Key, pair => pair.Value);
            var response = new ConsultGenerationResponse(generated, failed, generated.Count > 0);

            _logger.LogInformation(
                "ConsultGeneration completed. GeneratedCount={GeneratedCount}, FailedCount={FailedCount}, ElapsedMs={ElapsedMs}",
                generated.Count,
                failed.Count,
                requestStopwatch.ElapsedMilliseconds);

            if (generated.Count == 0)
            {
                return await CreateJsonResponseAsync(req, HttpStatusCode.InternalServerError, response, cancellationToken);
            }

            return await CreateJsonResponseAsync(req, HttpStatusCode.OK, response, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error in ConsultGeneration function. ExceptionType={ExceptionType}, Message={Message}, ElapsedMs={ElapsedMs}",
                ex.GetType().FullName,
                ex.Message,
                requestStopwatch.ElapsedMilliseconds);

            return await CreateJsonResponseAsync(req, HttpStatusCode.InternalServerError, new ConsultGenerationResponse(new(), new() { ["request"] = $"Internal error: {ex.Message}" }, false), cancellationToken);
        }
    }

    private async Task GenerateSectionAsync(
        string consultDraft,
        ConsultGenerationSectionRequest section,
        ConcurrentDictionary<string, string> generatedSections,
        ConcurrentDictionary<string, string> failedSections,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            _logger.LogInformation("Starting section generation. SectionId={SectionId}, SectionName={SectionName}", section.Id, section.Name);

            var prose = await _sectionGenerator.GenerateSectionAsync(
                consultDraft,
                section.Name,
                section.Standard,
                cancellationToken);

            var trimmedProse = prose.Trim();
            generatedSections[section.Id] = trimmedProse;

            _logger.LogInformation(
                "Section generation completed. SectionId={SectionId}, SectionName={SectionName}, ResponseLength={ResponseLength}, ElapsedMs={ElapsedMs}",
                section.Id,
                section.Name,
                trimmedProse.Length,
                stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            failedSections[section.Id] = ex.Message;

            _logger.LogError(
                ex,
                "Section generation failed. SectionId={SectionId}, SectionName={SectionName}, ExceptionType={ExceptionType}, Message={Message}, ElapsedMs={ElapsedMs}",
                section.Id,
                section.Name,
                ex.GetType().FullName,
                ex.Message,
                stopwatch.ElapsedMilliseconds);
        }
    }

    private static string? ValidateRequest(ConsultGenerationRequest? request)
    {
        if (request == null)
        {
            return "Request body is required.";
        }

        if (string.IsNullOrWhiteSpace(request.ConsultDraft))
        {
            return "ConsultDraft is required.";
        }

        if (request.Sections == null || request.Sections.Count == 0)
        {
            return "At least one section is required.";
        }

        foreach (var section in request.Sections)
        {
            if (string.IsNullOrWhiteSpace(section.Id)
                || string.IsNullOrWhiteSpace(section.Name))
            {
                return "Each section requires Id and Name.";
            }
        }

        if (request.Sections
            .GroupBy(section => section.Id, StringComparer.Ordinal)
            .Any(group => group.Count() > 1))
        {
            return "Duplicate section IDs are not allowed.";
        }

        return null;
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

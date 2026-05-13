using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Api;

public sealed class ConsultGeneration
{
    private readonly ILogger<ConsultGeneration> _logger;
    private readonly AgentSectionGenerator _sectionGenerator;

    public ConsultGeneration(ILogger<ConsultGeneration> logger, AgentSectionGenerator sectionGenerator)
    {
        _logger = logger;
        _sectionGenerator = sectionGenerator;
    }

    [Function("ConsultGeneration")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "post", "options")] HttpRequest req)
    {
        var requestStopwatch = Stopwatch.StartNew();

        FunctionCors.Apply(req);

        if (req.Method == "OPTIONS")
        {
            req.HttpContext.Response.StatusCode = 200;
            return new OkResult();
        }

        try
        {
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var generationRequest = JsonSerializer.Deserialize<ConsultGenerationRequest>(requestBody, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            var validationError = ValidateRequest(generationRequest);
            if (validationError != null)
            {
                _logger.LogWarning("Invalid ConsultGeneration request: {ValidationError}", validationError);
                return new BadRequestObjectResult(new ConsultGenerationResponse(new(), new() { ["request"] = validationError }, false));
            }

            var request = generationRequest!;
            var generatedSections = new ConcurrentDictionary<string, string>();
            var failedSections = new ConcurrentDictionary<string, string>();

            _logger.LogInformation(
                "ConsultGeneration request validated. SectionCount={SectionCount}, ConsultDraftLength={ConsultDraftLength}, TraceIdentifier={TraceIdentifier}",
                request.Sections.Count,
                request.ConsultDraft.Length,
                req.HttpContext.TraceIdentifier);

            var tasks = request.Sections.Select(section => GenerateSectionAsync(
                request.ConsultDraft,
                section,
                generatedSections,
                failedSections,
                req.HttpContext.RequestAborted));

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
                return new ObjectResult(response) { StatusCode = 500 };
            }

            return new OkObjectResult(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error in ConsultGeneration function. ExceptionType={ExceptionType}, Message={Message}, ElapsedMs={ElapsedMs}",
                ex.GetType().FullName,
                ex.Message,
                requestStopwatch.ElapsedMilliseconds);

            return new ObjectResult(new ConsultGenerationResponse(new(), new() { ["request"] = $"Internal error: {ex.Message}" }, false)) { StatusCode = 500 };
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
                || string.IsNullOrWhiteSpace(section.Name)
                || string.IsNullOrWhiteSpace(section.Standard))
            {
                return "Each section requires Id, Name, and Standard.";
            }
        }

        return null;
    }
}

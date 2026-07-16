using System.Net;
using System.Text.Json;
using Consultologist.Api.Auth;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Consultologist.Api.Workflow;

public sealed class WorkflowPackages
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IWorkflowPackageStore _packageStore;
    private readonly IWorkflowPackagePinResolver _pinResolver;
    private readonly WorkflowPackagePublisher _publisher;
    private readonly IAccountAuthorizer _authorizer;
    private readonly ILogger<WorkflowPackages> _logger;

    public WorkflowPackages(
        IWorkflowPackageStore packageStore,
        IWorkflowPackagePinResolver pinResolver,
        WorkflowPackagePublisher publisher,
        IAccountAuthorizer authorizer,
        ILogger<WorkflowPackages> logger)
    {
        _packageStore = packageStore;
        _pinResolver = pinResolver;
        _publisher = publisher;
        _authorizer = authorizer;
        _logger = logger;
    }

    [Function("WorkflowPackageCurrent")]
    public async Task<HttpResponseData> GetCurrentAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "options", Route = "WorkflowPackages/Current")] HttpRequestData req)
    {
        var cancellationToken = req.FunctionContext.CancellationToken;

        if (string.Equals(req.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            var optionsResponse = req.CreateResponse(HttpStatusCode.OK);
            FunctionCors.Apply(req, optionsResponse);
            return optionsResponse;
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

        var packageRef = await _pinResolver.ResolvePinAsync(account.AppUserId, cancellationToken);

        WorkflowPackage package;
        try
        {
            package = await _packageStore.ResolveAsync(packageRef, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Workflow package resolution failed. Pin={Pin}", packageRef);
            var errorResponse = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            FunctionCors.Apply(req, errorResponse);
            await errorResponse.WriteAsJsonAsync(new { error = "Workflow package registry is unavailable." }, cancellationToken);
            return errorResponse;
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        FunctionCors.Apply(req, response);
        await response.WriteAsJsonAsync(
            new WorkflowPackageResponse(
                package.Manifest.Name,
                package.Manifest.Version,
                package.Manifest.SpecVersion,
                WorkflowPackageSections.Resolve(package)
                    .Select(section => new WorkflowPackageSectionResponse(section.Id, section.Name, section.Content))
                    .ToList()),
            cancellationToken);

        return response;
    }

    [Function("WorkflowPackageContent")]
    public async Task<HttpResponseData> GetCurrentContentAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "options", Route = "WorkflowPackages/Current/Content")] HttpRequestData req)
    {
        var cancellationToken = req.FunctionContext.CancellationToken;

        if (string.Equals(req.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            var optionsResponse = req.CreateResponse(HttpStatusCode.OK);
            FunctionCors.Apply(req, optionsResponse);
            return optionsResponse;
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

        var packageRef = await _pinResolver.ResolvePinAsync(account.AppUserId, cancellationToken);

        WorkflowPackage package;
        try
        {
            package = await _packageStore.ResolveAsync(packageRef, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Workflow package content resolution failed. Pin={Pin}", packageRef);
            var errorResponse = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            FunctionCors.Apply(req, errorResponse);
            await errorResponse.WriteAsJsonAsync(new { error = "Workflow package registry is unavailable." }, cancellationToken);
            return errorResponse;
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        FunctionCors.Apply(req, response);
        await response.WriteAsJsonAsync(
            new WorkflowPackageContentResponse(
                package.Manifest.Name,
                package.Manifest.Version,
                package.Manifest.SpecVersion,
                package.Manifest,
                package.SourceFiles ?? new Dictionary<string, string>(StringComparer.Ordinal)),
            cancellationToken);

        return response;
    }

    [Function("WorkflowPackagePublish")]
    public async Task<HttpResponseData> PublishAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", "options", Route = "WorkflowPackages/Publish")] HttpRequestData req)
    {
        var cancellationToken = req.FunctionContext.CancellationToken;

        if (string.Equals(req.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            var optionsResponse = req.CreateResponse(HttpStatusCode.OK);
            FunctionCors.Apply(req, optionsResponse);
            return optionsResponse;
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

        WorkflowPackagePublishRequest? publishRequest = null;
        var requestBody = await req.ReadAsStringAsync() ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(requestBody))
        {
            try
            {
                publishRequest = JsonSerializer.Deserialize<WorkflowPackagePublishRequest>(requestBody, JsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Invalid WorkflowPackagePublish request: malformed JSON body.");
                return await CreateJsonResponseAsync(req, HttpStatusCode.BadRequest, new { errors = new[] { "Malformed JSON request body." } }, cancellationToken);
            }
        }

        if (publishRequest is null)
        {
            return await CreateJsonResponseAsync(req, HttpStatusCode.BadRequest, new { errors = new[] { "A publish request body is required." } }, cancellationToken);
        }

        WorkflowPackagePublishResult result;
        try
        {
            result = await _publisher.PublishAsync(account.AppUserId, publishRequest, cancellationToken);
        }
        catch (Azure.RequestFailedException ex)
        {
            _logger.LogError(ex, "Workflow package publish failed against the registry. AppUserId={AppUserId}", account.AppUserId);
            return await CreateJsonResponseAsync(req, HttpStatusCode.ServiceUnavailable, new { errors = new[] { "Workflow package registry is unavailable." } }, cancellationToken);
        }

        if (result.Forbidden)
        {
            return await CreateJsonResponseAsync(req, HttpStatusCode.Forbidden, new { errors = result.Errors }, cancellationToken);
        }

        if (!result.Succeeded)
        {
            return await CreateJsonResponseAsync(req, HttpStatusCode.BadRequest, new { errors = result.Errors }, cancellationToken);
        }

        return await CreateJsonResponseAsync(req, HttpStatusCode.OK, result.Response!, cancellationToken);
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

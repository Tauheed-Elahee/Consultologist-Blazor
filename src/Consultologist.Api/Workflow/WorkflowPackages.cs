using System.Net;
using Consultologist.Api.Auth;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Consultologist.Api.Workflow;

public sealed class WorkflowPackages
{
    private readonly IWorkflowPackageStore _packageStore;
    private readonly IWorkflowPackagePinResolver _pinResolver;
    private readonly IAccountAuthorizer _authorizer;
    private readonly ILogger<WorkflowPackages> _logger;

    public WorkflowPackages(
        IWorkflowPackageStore packageStore,
        IWorkflowPackagePinResolver pinResolver,
        IAccountAuthorizer authorizer,
        ILogger<WorkflowPackages> logger)
    {
        _packageStore = packageStore;
        _pinResolver = pinResolver;
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
}

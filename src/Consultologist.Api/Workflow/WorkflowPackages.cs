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
    private readonly WorkflowPackageLineageResolver _lineage;
    private readonly IAccountAuthorizer _authorizer;
    private readonly ILogger<WorkflowPackages> _logger;

    public WorkflowPackages(
        IWorkflowPackageStore packageStore,
        IWorkflowPackagePinResolver pinResolver,
        WorkflowPackagePublisher publisher,
        WorkflowPackageLineageResolver lineage,
        IAccountAuthorizer authorizer,
        ILogger<WorkflowPackages> logger)
    {
        _packageStore = packageStore;
        _pinResolver = pinResolver;
        _publisher = publisher;
        _lineage = lineage;
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

    [Function("WorkflowPackageDiagram")]
    public async Task<HttpResponseData> GetDiagramAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "options", Route = "WorkflowPackages/Current/Diagram")] HttpRequestData req)
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
            _logger.LogError(ex, "Workflow package diagram resolution failed. Pin={Pin}", packageRef);
            var errorResponse = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            FunctionCors.Apply(req, errorResponse);
            await errorResponse.WriteAsJsonAsync(new { error = "Workflow package registry is unavailable." }, cancellationToken);
            return errorResponse;
        }

        // The same generator that produces the checked-in dag.mmd (pinned by
        // WorkflowDagDiagramTests) — a read-only projection of the manifest.
        return await CreateJsonResponseAsync(req, HttpStatusCode.OK,
            new { diagram = WorkflowDagDiagram.Generate(package.Manifest) }, cancellationToken);
    }

    [Function("WorkflowPackageLineage")]
    public async Task<HttpResponseData> GetLineageAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "options", Route = "WorkflowPackages/Lineage")] HttpRequestData req)
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

        var rawRef = System.Web.HttpUtility.ParseQueryString(req.Url.Query)["ref"];

        if (!WorkflowPackageRef.TryParse(rawRef, out var packageRef) || packageRef!.IsLatest)
        {
            return await CreateJsonResponseAsync(req, HttpStatusCode.BadRequest,
                new { error = "ref must be a concrete package reference (name@vYYYY.MM.N)." }, cancellationToken);
        }

        if (!WorkflowPackageNaming.CanAccess(packageRef.Name, account.AppUserId))
        {
            return await CreateJsonResponseAsync(req, HttpStatusCode.Forbidden,
                new { error = "Workflow package is not accessible from this account." }, cancellationToken);
        }

        IReadOnlyList<string> chain;
        try
        {
            chain = await _lineage.GetLineageAsync(packageRef, cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("was not found", StringComparison.Ordinal))
        {
            return await CreateJsonResponseAsync(req, HttpStatusCode.NotFound,
                new { error = ex.Message }, cancellationToken);
        }

        // The acct-* rule on every hop — unreachable by construction (publish
        // stamping validates sources), enforced anyway.
        if (chain.Any(hop => WorkflowPackageRef.TryParse(hop, out var hopRef)
            && !WorkflowPackageNaming.CanAccess(hopRef!.Name, account.AppUserId)))
        {
            return await CreateJsonResponseAsync(req, HttpStatusCode.Forbidden,
                new { error = "Workflow package lineage crosses another account's package." }, cancellationToken);
        }

        return await CreateJsonResponseAsync(req, HttpStatusCode.OK, new WorkflowPackageLineageResponse(chain), cancellationToken);
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

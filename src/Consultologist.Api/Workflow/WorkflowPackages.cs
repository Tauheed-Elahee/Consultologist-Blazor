using System.Net;
using Consultologist.Api.Auth;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Consultologist.Api.Workflow;

public sealed class WorkflowPackages
{
    private const string PackagePinSettingKey = "consult.workflowPackage";
    private const string DefaultPinFallback = "general@latest";

    private readonly IWorkflowPackageStore _packageStore;
    private readonly IAccountSettingsStore _settingsStore;
    private readonly IAccountAuthorizer _authorizer;
    private readonly ILogger<WorkflowPackages> _logger;

    public WorkflowPackages(
        IWorkflowPackageStore packageStore,
        IAccountSettingsStore settingsStore,
        IAccountAuthorizer authorizer,
        ILogger<WorkflowPackages> logger)
    {
        _packageStore = packageStore;
        _settingsStore = settingsStore;
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

        var packageRef = await ResolvePinAsync(account.AppUserId, cancellationToken);

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
                package.StandardsMarkdown),
            cancellationToken);

        return response;
    }

    private async Task<WorkflowPackageRef> ResolvePinAsync(string appUserId, CancellationToken cancellationToken)
    {
        var accountPin = await _settingsStore.GetAsync(appUserId, PackagePinSettingKey, cancellationToken);
        if (WorkflowPackageRef.TryParse(accountPin?.Value, out var accountRef))
        {
            return accountRef!;
        }

        if (!string.IsNullOrWhiteSpace(accountPin?.Value))
        {
            _logger.LogWarning(
                "Ignoring malformed workflow package pin on account. AppUserId={AppUserId}, Pin={Pin}",
                appUserId,
                accountPin.Value);
        }

        var defaultPin = Environment.GetEnvironmentVariable("WorkflowPackages__Default");
        if (WorkflowPackageRef.TryParse(defaultPin, out var defaultRef))
        {
            return defaultRef!;
        }

        WorkflowPackageRef.TryParse(DefaultPinFallback, out var fallbackRef);
        return fallbackRef!;
    }
}

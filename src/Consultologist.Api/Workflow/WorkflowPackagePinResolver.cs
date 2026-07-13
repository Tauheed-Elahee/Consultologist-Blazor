using Consultologist.Api.Auth;
using Microsoft.Extensions.Logging;

namespace Consultologist.Api.Workflow;

public interface IWorkflowPackagePinResolver
{
    Task<WorkflowPackageRef> ResolvePinAsync(string appUserId, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves the workflow package pin for an account: account setting →
/// WorkflowPackages__Default app setting → general@latest. Shared by the
/// WorkflowPackages/Current endpoint and the consult job start.
/// </summary>
public sealed class WorkflowPackagePinResolver : IWorkflowPackagePinResolver
{
    private const string PackagePinSettingKey = "consult.workflowPackage";
    private const string DefaultPinFallback = "general@latest";

    private readonly IAccountSettingsStore _settingsStore;
    private readonly ILogger<WorkflowPackagePinResolver> _logger;

    public WorkflowPackagePinResolver(IAccountSettingsStore settingsStore, ILogger<WorkflowPackagePinResolver> logger)
    {
        _settingsStore = settingsStore;
        _logger = logger;
    }

    public async Task<WorkflowPackageRef> ResolvePinAsync(string appUserId, CancellationToken cancellationToken)
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

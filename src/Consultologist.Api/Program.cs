extern alias AzureIdentity;

using Consultologist.Api;
using Consultologist.Api.Agents;
using Consultologist.Api.Auth;
using Consultologist.Api.Jobs;
using Consultologist.Api.Probes;
using Consultologist.Api.Workflow;
using Azure.Core;
using AzureIdentity::Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Logging.AddConsole();

builder.Services.AddApplicationInsightsTelemetryWorkerService();
builder.Services.ConfigureFunctionsApplicationInsights();

builder.Services.AddSingleton<TokenCredential>(sp =>
{
    var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Consultologist.Api.StartupDiagnostics");
    var azureClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
    var isRunningInAzure = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID"));

    Console.Error.WriteLine(
        $"[Api.StartupDiagnostics] Creating TokenCredential. Utc={DateTimeOffset.UtcNow:O}; IsRunningInAzure={isRunningInAzure}; HasAzureClientId={!string.IsNullOrWhiteSpace(azureClientId)}");

    logger.LogInformation(
        "Creating TokenCredential. IsRunningInAzure={IsRunningInAzure}, HasAzureClientId={HasAzureClientId}",
        isRunningInAzure,
        !string.IsNullOrWhiteSpace(azureClientId));

    var credentialOptions = new DefaultAzureCredentialOptions
    {
        ExcludeInteractiveBrowserCredential = true,
        ExcludeVisualStudioCredential = true,
        ExcludeVisualStudioCodeCredential = true,
        ExcludeAzurePowerShellCredential = true,
        Retry = { NetworkTimeout = TimeSpan.FromSeconds(10) }
    };

    if (!string.IsNullOrWhiteSpace(azureClientId))
    {
        credentialOptions.ManagedIdentityClientId = azureClientId;
    }

    var credential = new DefaultAzureCredential(credentialOptions);

    Console.Error.WriteLine($"[Api.StartupDiagnostics] TokenCredential created. Utc={DateTimeOffset.UtcNow:O}");
    logger.LogInformation("TokenCredential created.");

    return credential;
});

builder.Services.AddSingleton(_ =>
{
    // Azure: the catalog is content, loaded from the public registry at the
    // pinned version (publish + pin bump + restart, no redeploy). Local dev:
    // the bundled agents/ directory. Both fail loud — no coherent catalog, no
    // jobs (#93).
    var publicUri = Environment.GetEnvironmentVariable("WorkflowPackages__PublicBlobServiceUri");
    var pin = Environment.GetEnvironmentVariable("OutputContracts__Pin") ?? $"{OutputContractCatalog.RegistryName}@latest";

    return string.IsNullOrWhiteSpace(publicUri)
        ? OutputContractCatalog.Load()
        : OutputContractCatalog.LoadFromRegistryAsync(new Uri(publicUri), pin).GetAwaiter().GetResult();
});
builder.Services.AddScoped<AgentSectionGenerator>();
builder.Services.AddSingleton<IBearerTokenValidator, BearerTokenValidator>();
builder.Services.AddSingleton<IAccountStore, AccountStore>();
builder.Services.AddSingleton<IAccountSettingsStore, AccountSettingsStore>();
builder.Services.AddSingleton<IConsultGenerationJobEventStore, TableConsultGenerationJobEventStore>();
builder.Services.AddSingleton<IConsultGenerationJobIndexStore, TableConsultGenerationJobIndexStore>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<WorkflowPackageBlobContainerFactory>();
builder.Services.AddSingleton<IWorkflowPackageStore, WorkflowPackageStore>();
builder.Services.AddSingleton<IWorkflowPackageRegistryWriter, WorkflowPackageRegistryWriter>();
builder.Services.AddSingleton<WorkflowPackagePublisher>();
builder.Services.AddSingleton<IWorkflowPackagePinResolver, WorkflowPackagePinResolver>();
builder.Services.AddSingleton<PublicRegistryReader>();
builder.Services.AddSingleton<PublicChain>();
builder.Services.AddScoped<WorkflowPackages>();
builder.Services.AddHttpClient();
builder.Services.AddHostedService<AgentAttestationService>();
builder.Services.AddTransient<ConsultGenerationJobEntity>();
builder.Services.AddScoped<IAccountAuthorizer, AccountAuthorizer>();
builder.Services.AddScoped<Account>();
builder.Services.AddScoped<Diagnostics>();
builder.Services.AddScoped<ConsultGenerationJobs>();
builder.Services.AddScoped<RunPromptNodeActivity>();

builder.Build().Run();

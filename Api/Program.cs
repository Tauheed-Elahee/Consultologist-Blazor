extern alias AzureIdentity;

using Api;
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
    var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Api.StartupDiagnostics");
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

builder.Services.AddScoped<AgentSectionGenerator>();
builder.Services.AddScoped<AgentProxy>();
builder.Services.AddScoped<ConsultGeneration>();

builder.Build().Run();

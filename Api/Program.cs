extern alias AzureIdentity;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AzureIdentity::Azure.Identity;
using Azure.Core;
using Api;

// Deployment: 2025-12-01 22:40 UTC - Fixed TokenCredential DI registration
var builder = FunctionsApplication.CreateBuilder(args);

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

// Register TokenCredential explicitly for DI
builder.Services.AddSingleton<TokenCredential>(sp =>
{
    var azureClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
    var isRunningInAzure = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID"));

    if (isRunningInAzure && string.IsNullOrWhiteSpace(azureClientId))
    {
        throw new InvalidOperationException(
            "AZURE_CLIENT_ID must be set when running in Azure so DefaultAzureCredential uses the attached user-assigned managed identity.");
    }

    var credentialOptions = new DefaultAzureCredentialOptions
    {
        // Exclude interactive credentials for server environments
        ExcludeInteractiveBrowserCredential = true,
        ExcludeVisualStudioCredential = true,
        ExcludeVisualStudioCodeCredential = true,
        ExcludeAzurePowerShellCredential = true,

        // Critical: Set timeout to prevent deployment hanging
        Retry = { NetworkTimeout = TimeSpan.FromSeconds(10) }
    };

    if (!string.IsNullOrWhiteSpace(azureClientId))
    {
        credentialOptions.ManagedIdentityClientId = azureClientId;
    }

    return new DefaultAzureCredential(credentialOptions);
});

builder.Services.AddScoped<AgentSectionGenerator>();

// Register Functions in DI container
builder.Services.AddScoped<AgentProxy>();
builder.Services.AddScoped<ConsultGeneration>();

builder.Build().Run();

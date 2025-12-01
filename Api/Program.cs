using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Azure.Identity;
using Azure.Core;
using Api;

// Deployment: 2025-12-01 22:40 UTC - Fixed TokenCredential DI registration
var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

builder.Services.AddHttpClient();

// Register TokenCredential explicitly for DI
builder.Services.AddSingleton<TokenCredential>(sp =>
{
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

    return new DefaultAzureCredential(credentialOptions);
});

// Register AgentProxy in DI container
builder.Services.AddScoped<AgentProxy>();

builder.Build().Run();

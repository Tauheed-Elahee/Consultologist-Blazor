using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Azure.Identity;
using Api;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

builder.Services.AddHttpClient();

// Configure Azure clients with lazy DefaultAzureCredential initialization
builder.Services.AddAzureClients(clientBuilder =>
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

    // Credential will be lazy-loaded on first use, not during startup
    clientBuilder.UseCredential(new DefaultAzureCredential(credentialOptions));
});

// Register AgentProxy in DI container
builder.Services.AddScoped<AgentProxy>();

builder.Build().Run();

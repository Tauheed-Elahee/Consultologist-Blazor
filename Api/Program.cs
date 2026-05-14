extern alias AzureIdentity;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AzureIdentity::Azure.Identity;
using Azure.Core;
using Api;

// Deployment: 2025-12-01 22:40 UTC - Fixed TokenCredential DI registration
Console.Error.WriteLine($"[Api.StartupDiagnostics] Program starting. Utc={DateTimeOffset.UtcNow:O}");

var builder = FunctionsApplication.CreateBuilder(args);

builder.Logging.AddConsole();

builder.Use(next => async context =>
{
    var logger = context.InstanceServices
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("Api.InvocationDiagnostics");

    logger.LogInformation(
        "Worker invocation starting. FunctionName={FunctionName}, InvocationId={InvocationId}",
        context.FunctionDefinition.Name,
        context.InvocationId);

    try
    {
        await next(context);

        logger.LogInformation(
            "Worker invocation completed. FunctionName={FunctionName}, InvocationId={InvocationId}",
            context.FunctionDefinition.Name,
            context.InvocationId);
    }
    catch (Exception ex)
    {
        logger.LogError(
            ex,
            "Worker invocation failed before a function response was returned. FunctionName={FunctionName}, InvocationId={InvocationId}, ExceptionType={ExceptionType}, Message={Message}",
            context.FunctionDefinition.Name,
            context.InvocationId,
            ex.GetType().FullName,
            ex.Message);

        throw;
    }
});

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

// Register TokenCredential explicitly for DI
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

    var credential = new DefaultAzureCredential(credentialOptions);

    Console.Error.WriteLine($"[Api.StartupDiagnostics] TokenCredential created. Utc={DateTimeOffset.UtcNow:O}");
    logger.LogInformation("TokenCredential created.");

    return credential;
});

builder.Services.AddScoped<AgentSectionGenerator>();

// Register Functions in DI container
builder.Services.AddScoped<AgentProxy>();
builder.Services.AddScoped<ConsultGeneration>();

Console.Error.WriteLine($"[Api.StartupDiagnostics] Program configured. Building host. Utc={DateTimeOffset.UtcNow:O}");

builder.Build().Run();

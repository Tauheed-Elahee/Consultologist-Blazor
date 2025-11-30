# Fix Azure Static Web Apps Deployment Failure

## Problem
Azure Static Web Apps deployment fails with "Failed to deploy the Azure Functions" during polling phase after successful build and artifact upload.

## Root Cause
The primary issue is **DefaultAzureCredential initialization blocking function worker startup**:

1. `DefaultAzureCredential()` is instantiated in the `AgentProxy` constructor (line 24)
2. During deployment, this triggers a credential chain that makes network calls
3. Managed Identity may not be fully initialized during deployment polling
4. Without timeout configuration, the function worker initialization hangs
5. Deployment polling times out (~60 seconds) before the function can start

Secondary issues:
- Missing DI registration for `AgentProxy` class
- CORS configuration conflict between `host.json` and manual headers in code

## Solution: Lazy Credential Initialization with Dependency Injection

Defer credential initialization until first actual use (not during startup) using Azure SDK's dependency injection pattern.

## Implementation Steps

### 1. Update Api/Api.csproj

Add the Microsoft.Extensions.Azure package:

```xml
<PackageReference Include="Microsoft.Extensions.Azure" Version="1.7.6" />
```

### 2. Refactor Api/Program.cs

Replace entire content with:

```csharp
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
        ExcludeSharedTokenCacheCredential = true,
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
```

### 3. Update Api/AgentProxy.cs

**Change 1: Update the field and constructor (lines 17-25)**

```csharp
private readonly TokenCredential _credential; // Change from DefaultAzureCredential

public AgentProxy(
    ILogger<AgentProxy> logger, 
    IHttpClientFactory httpClientFactory,
    TokenCredential credential) // Inject from DI
{
    _logger = logger;
    _httpClient = httpClientFactory.CreateClient();
    _credential = credential; // Just assign, no initialization
}
```

**Change 2: Remove manual CORS headers (lines 30-34)**

Remove these lines:
```csharp
req.HttpContext.Response.Headers["Access-Control-Allow-Origin"] = "*";
req.HttpContext.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
req.HttpContext.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization";
```

**Change 3: Add timeout to token acquisition (around line 66)**

Replace:
```csharp
var tokenRequestContext = new TokenRequestContext(new[] { "https://ai.azure.com/.default" });
var token = await _credential.GetTokenAsync(tokenRequestContext);
```

With:
```csharp
var tokenRequestContext = new TokenRequestContext(new[] { "https://ai.azure.com/.default" });

AccessToken token;
try
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    token = await _credential.GetTokenAsync(tokenRequestContext, cts.Token);
}
catch (OperationCanceledException)
{
    _logger.LogError("Token acquisition timed out");
    return new ObjectResult(new AgentResponse(null, "Authentication timeout", false)) { StatusCode = 500 };
}
catch (Azure.Identity.AuthenticationFailedException ex)
{
    _logger.LogError(ex, "Authentication failed");
    return new ObjectResult(new AgentResponse(null, "Authentication failed", false)) { StatusCode = 500 };
}
```

## Critical Files to Modify

1. `Api/Api.csproj` - Add Microsoft.Extensions.Azure package
2. `Api/Program.cs` - Configure DI with lazy credential initialization
3. `Api/AgentProxy.cs` - Inject TokenCredential, remove CORS headers, add timeouts

## Expected Outcomes

**After fix:**
- ✅ Deployment completes successfully
- ✅ Function worker starts quickly (<5 seconds)
- ✅ Credential initialized only on first request
- ✅ Proper timeout protection prevents hanging

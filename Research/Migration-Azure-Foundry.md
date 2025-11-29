# Azure AI Foundry Integration - Authentication Options

## ✅ RESOLVED - 2025-11-28

**Issue**: Invalid `assistant_id` error when creating runs with Azure AI Foundry agent.

**Root Cause**: Using Azure AI Foundry's internal agent ID format (`consultologist-canada-east-ai:7`) instead of the OpenAI-compatible assistant ID required by the REST API.

**Solution**: Query the `/assistants` endpoint to get the correct `asst_` prefixed ID.

**Correct Assistant ID**: `asst_tx4wK44h3Q4fLrMv3vZAhDy3`

**See**: [Findings-AI-Endpoint.md](./Findings-AI-Endpoint.md) for complete investigation and resolution details.

---

## Current Status

**Problem**: Getting 401 Unauthorized when attempting to call Azure AI Foundry Agent API endpoints using API key authentication.

**Root Cause**: Azure AI Foundry Agents/Projects management API endpoints (`/threads`, `/messages`, `/runs`) **require Azure AD (Entra ID) authentication**, not API keys. This is by design for security and auditability.

## API Key Limitations

From Microsoft documentation:
- API keys work for some Azure AI Foundry endpoints (OpenAI-compatible endpoints, runtime operations)
- The Agents/Projects management APIs **explicitly reject** key-based authentication
- Error message: "Key-based authentication is not supported for this route"
- Microsoft requires Azure AD auth + RBAC for agent management operations

### Supported Header Formats (where applicable)
- `api-key: {your-key}` - For Azure OpenAI-style endpoints
- `Ocp-Apim-Subscription-Key: {your-key}` - For multi-service resource keys

## Authentication Options

### Option 1: Azure AD (Entra ID) Token Authentication ✅ Recommended

**Approach**: Leverage existing MSAL authentication in the Blazor app.

**Pros**:
- Uses existing authentication infrastructure
- No new services needed
- User-based permissions (good for multi-user scenarios)
- Follows Microsoft's security best practices

**Cons**:
- Requires Azure Portal configuration (add permissions)
- Each user needs access to the AI resource
- Finding the correct scope can be tricky
- More complex initial setup

**Implementation Steps**:
1. Azure Portal: Add Azure AI Foundry resource to App Registration's API permissions
2. Find correct scope for Azure AI Foundry (e.g., `https://cognitiveservices.azure.com/.default`)
3. Update `Program.cs` to request Azure AI Foundry scope
4. Modify `CreateAIRequestAsync` to:
   - Get access token from MSAL
   - Use `Authorization: Bearer {token}` instead of `api-key` header
5. May need separate HttpClient with different authorization handler

**Estimated Complexity**: Medium
**Estimated Time**: 1-2 hours (including Azure config and testing)

---

### Option 2: Azure Function Proxy 🔒 Most Secure for Production

**Approach**: Create Azure Function as middleware between Blazor app and Azure AI Foundry.

**Architecture**:
```
Blazor App → Azure Function (with Managed Identity) → Azure AI Foundry
```

**Pros**:
- API keys/credentials never exposed to browser
- Function uses Managed Identity (most secure)
- Centralized authentication and authorization
- Can add rate limiting, logging, validation
- Better for production scenarios

**Cons**:
- Requires creating new Azure Function
- Additional infrastructure to maintain
- More moving parts
- Slight latency overhead

**Implementation Steps**:
1. Create Azure Function App in Azure Portal
2. Enable Managed Identity on the Function
3. Grant Function's identity access to Azure AI Foundry resource
4. Implement HTTP-triggered function that:
   - Accepts request from Blazor app
   - Calls Azure AI Foundry using Managed Identity
   - Returns response to Blazor app
5. Update Blazor app to call Azure Function instead of direct API

**Estimated Complexity**: Medium-High
**Estimated Time**: 2-4 hours (including Azure setup, function code, deployment)

---

### Option 3: OpenAI-Compatible Endpoint (If Available)

**Approach**: Check if Azure AI Foundry resource has OpenAI-compatible endpoints that accept API keys.

**Pros**:
- Simpler authentication (API key works)
- No code changes to auth logic
- Direct connection from browser

**Cons**:
- Not all Azure AI Foundry resources expose this
- May not have full agent functionality
- API key exposed in browser (security concern)
- Different URL structure

**How to Check**:
1. Azure Portal → Your AI Foundry resource → Keys and Endpoint
2. Look for "Endpoint" or "Target URI"
3. Check if there's an OpenAI-compatible endpoint URL
4. Test if API key works with that endpoint

**Estimated Complexity**: Low (if available)
**Estimated Time**: 30 minutes (investigation and testing)

---

## Current Implementation

**Endpoint**: `https://consultologist-canada-east-resou.services.ai.azure.com/api/projects/consultologist-canada-east`
**Agent ID**: `consultologist-canada-east-ai:7`
**API Version**: `2025-05-01`

**Workflow Implemented**:
1. Create thread: `POST /threads`
2. Add message: `POST /threads/{threadId}/messages`
3. Create run: `POST /threads/{threadId}/runs` with `assistant_id`
4. Poll status: `GET /threads/{threadId}/runs/{runId}`
5. Get messages: `GET /threads/{threadId}/messages`

---

## Recommendation

**For Development/Testing**: Start with **Option 1** (Azure AD tokens)
- Fastest to implement using existing MSAL setup
- Good learning experience for Azure AD integration
- Can test full functionality quickly

**For Production**: Migrate to **Option 2** (Azure Function proxy)
- Most secure (credentials never in browser)
- Better architecture for production apps
- Easier to add monitoring, rate limiting, etc.

---

## References

- [How to use API Key in Azure AI Foundry](https://learn.microsoft.com/en-us/answers/questions/5587848/how-to-use-api-key-in-azure-ai-foundry)
- [Azure AI Agent Key Based Authentication](https://learn.microsoft.com/en-us/answers/questions/5534429/azure-ai-agent-key-based-authentication)
- [Azure AI Foundry Agent Service REST API](https://learn.microsoft.com/en-us/rest/api/aifoundry/aiagents/)
- [Securely expose Azure AI Foundry Projects via API Management](https://muafzal.medium.com/securely-expose-azure-ai-foundry-projects-via-api-management-0fadcf43d3e8)

---

## Selected Approach: Option 2 - Azure Function Proxy ✅

**Decision**: Implementing Azure Function as secure middleware for production deployment.

---

## Implementation Plan: Azure Function Proxy

**Architecture**: Monorepo with Azure Static Web Apps Managed Functions

The Azure Function will be created in the same repository as the Blazor app and deployed as a **managed function** within Azure Static Web Apps. This provides automatic integration, simplified deployment, and no CORS configuration needed.

**Repository Structure:**
```
Consultologist-Blazor/
├── Pages/               # Blazor pages
├── Services/           # Services including AIEndpointService
├── wwwroot/            # Static files & config
├── Api/                # Azure Function (managed)
│   ├── AgentProxy.cs
│   ├── Models/
│   ├── host.json
│   └── local.settings.json
├── BlazorWasm.csproj
└── staticwebapp.config.json
```

### Phase 1: Create Azure Function Project

#### 1.1 Create New Azure Function Project

```bash
# Navigate to repository root
cd /home/thegreat/Projects/GitHub/Consultologist-Blazor

# Create API directory for Azure Function (managed function location)
mkdir -p Api
cd Api

# Create new Azure Functions project (isolated worker)
func init --worker-runtime dotnet-isolated --target-framework net8.0

# Add HTTP trigger function
func new --name AgentProxy --template "HTTP trigger" --authlevel "function"
```

**Note**: The `Api` folder name is important - Azure Static Web Apps automatically recognizes this as the managed function location.

#### 1.2 Add Required NuGet Packages

```bash
cd Api
dotnet add package Azure.Identity
dotnet add package Microsoft.Extensions.Configuration
dotnet add package System.Text.Json
```

**Packages needed**:
- `Azure.Identity` - For Managed Identity authentication
- `Microsoft.Extensions.Configuration` - For reading app settings
- `System.Text.Json` - For JSON processing

---

### Phase 2: Implement Azure Function Code

#### 2.1 Create Request/Response Models

**File**: `Api/Models/AgentRequest.cs`

```csharp
namespace Api.Models;

public record AgentRequest(string ConsultDraft, string JsonSchema);
```

**File**: `Api/Models/AgentResponse.cs`

```csharp
namespace Api.Models;

public record AgentResponse(string? Response, string? Error, bool Success);
```

#### 2.2 Implement Agent Proxy Function

**File**: `Api/AgentProxy.cs`

Move the multi-step Azure AI Foundry workflow logic here:
1. Create thread
2. Add message with consultDraft + jsonSchema
3. Create run with agent ID
4. Poll run status (1 second intervals)
5. Retrieve messages and extract response

**Authentication**: Use `DefaultAzureCredential()` instead of API key

```csharp
using Azure.Identity;
// Get token for Azure AI Foundry
var credential = new DefaultAzureCredential();
var token = await credential.GetTokenAsync(
    new TokenRequestContext(new[] { "https://cognitiveservices.azure.com/.default" }));

// Use Bearer token instead of api-key
request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
```

#### 2.3 Configuration

**File**: `Api/local.settings.json` (local development)

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "AzureAI__Endpoint": "https://consultologist-canada-east-resou.services.ai.azure.com/api/projects/consultologist-canada-east",
    "AzureAI__AgentId": "consultologist-canada-east-ai:7",
    "AzureAI__ApiVersion": "2025-05-01"
  }
}
```

**Note**: No API key needed - using Managed Identity

---

### Phase 3: Azure Static Web Apps Configuration

**Note**: Since the Function is deployed as a managed function within Azure Static Web Apps, there's **no separate Function App to create**. The Static Web App automatically provisions and manages the Function.

#### 3.1 Enable System-Assigned Managed Identity on Static Web App

1. **Azure Portal** → Your Static Web App → **Identity**
2. System assigned → Status **On**
3. Save (Azure creates identity for the Static Web App and its managed functions)

#### 3.2 Grant Static Web App Access to AI Foundry

1. Azure AI Foundry resource → **Access Control (IAM)**
2. Add role assignment
3. Role: **Cognitive Services User** or **Cognitive Services OpenAI User**
4. Assign access to: **Managed Identity**
5. Select: Your Static Web App's managed identity

#### 3.3 Configure Static Web App Settings

Static Web App → **Configuration** → Application Settings:

Add these settings for the managed function:

```
AzureAI__Endpoint = https://consultologist-canada-east-resou.services.ai.azure.com/api/projects/consultologist-canada-east
AzureAI__AgentId = consultologist-canada-east-ai:7
AzureAI__ApiVersion = 2025-05-01
```

**Important**: Azure Static Web Apps managed functions automatically inherit these application settings.

---

### Phase 4: Deploy to Azure Static Web Apps

**Automatic Deployment via GitHub Actions**

Azure Static Web Apps automatically deploys both the Blazor app and the managed function when you push to your GitHub repository.

#### 4.1 Ensure GitHub Actions Workflow Exists

Check if `.github/workflows/azure-static-web-apps-*.yml` exists. If not, create one:

**File**: `.github/workflows/azure-static-web-apps-deploy.yml`

The workflow should include both app and API locations:

```yaml
app_location: "/" # Blazor app root
api_location: "Api" # Managed function location
output_location: "wwwroot"
```

#### 4.2 Push to GitHub

```bash
git add Api/
git commit -m "Add Azure Function as managed function"
git push origin main
```

GitHub Actions will automatically:
1. Build the Blazor WebAssembly app
2. Build the Azure Function
3. Deploy both to Azure Static Web Apps

#### 4.3 Access Function URL

After deployment, the managed function is available at:

```
https://your-static-web-app.azurestaticapps.net/api/AgentProxy
```

**No function key needed** - functions in Static Web Apps can use different authentication levels, but internal calls from the Blazor app work seamlessly.

---

### Phase 5: Update Blazor App

#### 5.1 Add Function URL to Configuration

**File**: `wwwroot/appsettings.Development.json`

```json
{
  "AzureFunction": {
    "AgentProxyUrl": "http://localhost:7071/api/AgentProxy"
  }
}
```

**File**: `wwwroot/appsettings.json`

```json
{
  "AzureFunction": {
    "AgentProxyUrl": "/api/AgentProxy"
  }
}
```

**Note**: 
- **Local development**: Use `http://localhost:7071/api/AgentProxy` (Azure Functions local runtime)
- **Production**: Use `/api/AgentProxy` (relative URL - same domain as Static Web App, no CORS issues)

#### 5.2 Create AIEndpointService (Optional)

**File**: `Services/AI/AIEndpointService.cs`

```csharp
public interface IAIEndpointService
{
    Task<string?> InvokeAgentAsync(string consultDraft, string jsonSchema);
}

public class AIEndpointService : IAIEndpointService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public AIEndpointService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public async Task<string?> InvokeAgentAsync(string consultDraft, string jsonSchema)
    {
        var functionUrl = _configuration["AzureFunction:AgentProxyUrl"];
        var request = new { ConsultDraft = consultDraft, JsonSchema = jsonSchema };
        
        var response = await _httpClient.PostAsJsonAsync(functionUrl, request);
        response.EnsureSuccessStatusCode();
        
        var result = await response.Content.ReadFromJsonAsync<AgentResponse>();
        return result?.Response;
    }
}
```

#### 5.3 Register Service in Program.cs

```csharp
builder.Services.AddScoped<IAIEndpointService, AIEndpointService>();
```

#### 5.4 Simplify Consults.razor

Replace `CreateAIRequestAsync` with simple service call:

```csharp
@inject IAIEndpointService AIService

private async Task CreateAIRequestAsync()
{
    aiRequestError = null;
    aiResponse = null;

    if (!string.IsNullOrEmpty(consultDraft) && !string.IsNullOrEmpty(renderedSchemaContent))
    {
        try
        {
            isAiRequestLoading = true;
            aiResponse = await AIService.InvokeAgentAsync(consultDraft, renderedSchemaContent);
        }
        catch (Exception ex)
        {
            aiRequestError = $"Error calling agent: {ex.Message}";
        }
        finally
        {
            isAiRequestLoading = false;
        }
    }
    else
    {
        aiRequestError = "Require both Consult Draft and loaded Schema";
    }
}
```

---

### Phase 6: Production Deployment

#### 6.1 Azure Static Web App Configuration

Set environment variable in Static Web App:

```
AzureFunction__AgentProxyUrl = https://consultologist-agent-proxy.azurewebsites.net/api/AgentProxy?code=FUNCTION_KEY
```

#### 6.2 Security Considerations

1. **CORS**: Configure Function App to only accept requests from your Static Web App domain
2. **Function Authorization**: Use `function` level (requires key in URL)
3. **Rate Limiting**: Consider adding throttling in Function code
4. **Monitoring**: Enable Application Insights for both Function and Static Web App

---

## Testing Checklist

- [ ] Function deploys successfully
- [ ] Managed Identity is enabled on Function
- [ ] Function has access to AI Foundry resource
- [ ] Function can authenticate with Azure AI Foundry
- [ ] Blazor app can call Function endpoint
- [ ] End-to-end workflow completes successfully
- [ ] Error handling works correctly
- [ ] CORS is properly configured

---

## Cleanup of Old Code

After successful deployment:

1. Remove API key from appsettings files
2. Remove Azure AI configuration from Blazor app (moved to Function)
3. Remove multi-step workflow code from Consults.razor
4. Delete unused helper methods

---

## Rollback Plan

If Option 2 doesn't work:
1. Keep existing Consults.razor code (don't delete yet)
2. Can quickly switch back to Option 1 (Azure AD tokens)
3. Or temporarily use direct API calls for testing

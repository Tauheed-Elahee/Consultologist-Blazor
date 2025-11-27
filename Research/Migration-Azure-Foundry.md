# Azure AI Foundry Integration - Authentication Options

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

## Next Steps

**Decision Required**: Choose authentication approach (Option 1, 2, or 3)

Once decided, implementation plan can be created for the chosen option.

# On-Behalf-Of (OBO) Flow Authentication

## Overview

On-Behalf-Of (OBO) Flow is an OAuth 2.0 authentication pattern that allows a middle-tier service to use a user's access token to request access to a downstream API. The service acts "on behalf of" the authenticated user.

**Important for This Project:** ⚠️ **OBO Flow is NOT feasible in Azure Static Web Apps** because Static Web Apps does not pass user access tokens to Azure Functions.

This document explains what OBO flow is, why it doesn't work in our architecture, and what alternatives are available.

---

## How On-Behalf-Of Flow Works (In Theory)

### Standard OBO Flow Diagram

```
┌──────────┐
│ User     │
│ Browser  │
└────┬─────┘
     │ 1. User authenticates, gets token
     ▼
┌──────────────────┐
│ Client App       │ Has user's access token (JWT)
│ (e.g., Blazor)   │ Scope: api://my-api/.default
└────┬─────────────┘
     │ 2. Calls API with user token in Authorization header
     ▼
┌──────────────────┐
│ Middle-Tier API  │ Receives user token
│ (Azure Function) │
└────┬─────────────┘
     │ 3. Exchanges user token for Graph token
     │    using OnBehalfOfCredential
     ▼
┌──────────────────┐
│ Azure AD         │ Validates user token
│ Token Endpoint   │ Issues new token for Graph API
└────┬─────────────┘
     │ 4. Returns Graph API token (still represents user)
     ▼
┌──────────────────┐
│ Middle-Tier API  │ Has Graph token representing user
└────┬─────────────┘
     │ 5. Calls Graph API with user's identity
     ▼
┌──────────────────┐
│ Microsoft Graph  │ User can only access their own data
│ GET /me          │
└──────────────────┘
```

### Code Example (If It Were Possible)

```csharp
using Azure.Identity;
using Microsoft.Graph;

public class GraphService
{
    public async Task<User?> GetUserProfileAsync(HttpRequestData req)
    {
        // Extract user's token from request
        var authHeader = req.Headers["Authorization"].FirstOrDefault();
        var userToken = authHeader?.Replace("Bearer ", "");
        
        if (string.IsNullOrEmpty(userToken))
            throw new UnauthorizedException("No token provided");
        
        // Create OBO credential
        var credential = new OnBehalfOfCredential(
            tenantId: _tenantId,
            clientId: _clientId,
            clientSecret: _clientSecret,
            userAssertion: userToken  // ← User's access token
        );
        
        // Call Graph API as the user
        var graphClient = new GraphServiceClient(credential);
        
        // This returns the current user's profile
        return await graphClient.Me.GetAsync();
    }
}
```

---

## Why OBO Flow Doesn't Work in Azure Static Web Apps

### The Missing Piece: User Access Token

**Azure Static Web Apps authentication provides:**
✅ User ID (object ID in Azure AD)  
✅ User email/UPN  
✅ User roles  
✅ Claims (often empty)

**Azure Static Web Apps does NOT provide:**
❌ The actual Azure AD access token (JWT)  
❌ ID token  
❌ Refresh token  

### What the x-ms-client-principal Header Contains

```json
{
  "identityProvider": "aad",
  "userId": "5e930dc383dd40068c0fb368269b38e4",
  "userDetails": "user@example.com",
  "userRoles": ["authenticated"],
  "claims": null
}
```

**No token = No OBO flow possible**

### Why Static Web Apps Doesn't Expose Tokens

**Security by Design:**
1. **Reduces attack surface** - Tokens never leave Azure infrastructure
2. **Prevents token theft** - No tokens in browser or function code
3. **Simplified architecture** - Developers don't handle sensitive tokens
4. **Managed authentication** - Azure handles all token lifecycle

### Azure App Service vs Static Web Apps

| Feature | Azure App Service | Azure Static Web Apps |
|---------|------------------|----------------------|
| **Token Store** | ✅ Available via headers | ❌ Not available |
| **X-MS-TOKEN-AAD-ACCESS-TOKEN** | ✅ Provided | ❌ Not provided |
| **x-ms-client-principal** | ✅ Provided | ✅ Provided |
| **/.auth/me endpoint** | ✅ Returns tokens | ❌ No tokens |

**Conclusion:** Static Web Apps is more restrictive for security reasons.

---

## Alternatives to OBO Flow

### Option 1: Client Credentials Flow (Current Implementation)

**How it works:**
- Azure Function authenticates as itself (not as user)
- Uses user ID from `x-ms-client-principal` to query Graph API
- Requires application permissions and admin consent

**See:** `Research/application-auth.md` for full details

**Pros:**
✅ Works with Static Web Apps  
✅ Simple implementation  
✅ Reliable and fast

**Cons:**
❌ Requires admin consent  
❌ Cannot access user-specific resources (emails, files)

---

### Option 2: MSAL in Blazor WebAssembly (No Admin Consent)

This approach **completely bypasses** the need for Azure Functions and OBO flow by authenticating directly in the browser.

#### Architecture

```
┌──────────────┐
│ User Browser │
│ (Blazor)     │
└──────┬───────┘
       │ 1. User clicks login
       ▼
┌──────────────────┐
│ MSAL.js Library  │ Runs in browser
│ in Blazor        │
└──────┬───────────┘
       │ 2. Redirects to Azure AD
       ▼
┌──────────────────┐
│ Azure AD         │ User authenticates
│ Login Page       │
└──────┬───────────┘
       │ 3. Returns access token to browser
       ▼
┌──────────────────┐
│ Blazor App       │ Token stored in browser (sessionStorage)
│ (Has token)      │
└──────┬───────────┘
       │ 4. Calls Graph API directly with token
       ▼
┌──────────────────┐
│ Microsoft Graph  │ ✅ Delegated permissions (no admin consent for User.Read)
│ GET /me          │
└──────────────────┘
```

#### Implementation Steps

**1. Install NuGet Package**
```bash
dotnet add package Microsoft.Authentication.WebAssembly.Msal
```

**2. Configure in Program.cs**
```csharp
using Microsoft.Authentication.WebAssembly.Msal;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Add MSAL authentication
builder.Services.AddMsalAuthentication(options =>
{
    builder.Configuration.Bind("AzureAd", options.ProviderOptions.Authentication);
    options.ProviderOptions.DefaultAccessTokenScopes.Add("User.Read");
});

// Add HttpClient for Graph API
builder.Services.AddScoped(sp =>
{
    var httpClient = new HttpClient
    {
        BaseAddress = new Uri("https://graph.microsoft.com/v1.0/")
    };
    return httpClient;
});

await builder.Build().RunAsync();
```

**3. Update appsettings.json**
```json
{
  "AzureAd": {
    "Authority": "https://login.microsoftonline.com/YOUR_TENANT_ID",
    "ClientId": "YOUR_CLIENT_ID",
    "ValidateAuthority": true
  }
}
```

**4. Create UserProfileService**
```csharp
public class UserProfileService
{
    private readonly HttpClient _httpClient;
    private readonly IAccessTokenProvider _tokenProvider;
    
    public UserProfileService(HttpClient httpClient, IAccessTokenProvider tokenProvider)
    {
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
    }
    
    public async Task<UserProfile?> GetUserProfileAsync()
    {
        // Request access token for Graph API
        var tokenResult = await _tokenProvider.RequestAccessToken(
            new AccessTokenRequestOptions
            {
                Scopes = new[] { "User.Read" }
            });
        
        if (!tokenResult.TryGetToken(out var token))
        {
            throw new Exception("Failed to acquire token");
        }
        
        // Add token to request
        _httpClient.DefaultRequestHeaders.Authorization = 
            new AuthenticationHeaderValue("Bearer", token.Value);
        
        // Call Graph API directly
        return await _httpClient.GetFromJsonAsync<UserProfile>("me");
    }
}
```

**5. Update App.razor**
```razor
<CascadingAuthenticationState>
    <Router AppAssembly="@typeof(App).Assembly">
        <Found Context="routeData">
            <AuthorizeRouteView RouteData="@routeData" DefaultLayout="@typeof(MainLayout)">
                <NotAuthorized>
                    <RedirectToLogin />
                </NotAuthorized>
            </AuthorizeRouteView>
        </Found>
    </Router>
</CascadingAuthenticationState>
```

**6. Configure Azure AD App Registration**

**Authentication Platform:**
1. Azure Portal → App Registrations → Your App
2. **Authentication** → **Add a platform** → **Single-page application**
3. Add Redirect URI: `https://yourapp.azurestaticapps.net/authentication/login-callback`
4. Enable **Access tokens** and **ID tokens**

**API Permissions:**
1. **API permissions** → **Add a permission**
2. **Microsoft Graph** → **Delegated permissions**
3. Select **User.Read** ✅ (no admin consent needed)
4. Click **Add permissions**

**Note:** Do NOT click "Grant admin consent" - it's not required for `User.Read`!

#### Pros and Cons of MSAL Approach

**Pros:**
✅ **No admin consent required** for `User.Read`  
✅ True user context (can access user's personal data)  
✅ Works in organizations that restrict admin consent  
✅ User can consent for themselves  
✅ Can access Microsoft Graph directly from browser  

**Cons:**
❌ More complex frontend code  
❌ Must replace Static Web Apps auth entirely  
❌ Tokens visible in browser (security consideration)  
❌ Cannot use Static Web Apps managed functions authentication  
❌ Requires more configuration  
❌ Different authentication UX  

---

## When to Use Each Approach

### Use Client Credentials (Application Auth) When:

✅ You can get admin consent  
✅ You only need to read user profiles (directory data)  
✅ You want simplest implementation  
✅ You're okay with server-side operations  
✅ You don't need to access user-owned resources

**Best for:** User profile display, directory queries, service-to-service auth

---

### Use MSAL (Delegated Permissions) When:

✅ Admin consent is impossible or difficult  
✅ You need to access user-owned data (emails, calendar, files)  
✅ You want true user context in Graph API calls  
✅ Your organization allows user consent  
✅ You're comfortable with more complex frontend code

**Best for:** Accessing user's personal data, avoiding admin consent

---

### When Would True OBO Work?

OBO flow works in these scenarios:

✅ **Azure App Service** (not Static Web Apps) - provides token store  
✅ **API Management** with token forwarding  
✅ **Custom authentication** where you control token flow  
✅ **Desktop/mobile apps** calling your own API  

**Not in:** Azure Static Web Apps with managed functions

---

## Permissions Comparison

### Delegated Permissions (MSAL - User Context)

| Permission | Description | Admin Consent |
|------------|-------------|---------------|
| `User.Read` | Read signed-in user's profile | ❌ No |
| `User.ReadBasic.All` | Read all users' basic profiles | ✅ Yes |
| `User.Read.All` | Read all users' full profiles | ✅ Yes |
| `Mail.Read` | Read user's email | ❌ No (user consents) |
| `Files.Read.All` | Read all files user can access | ✅ Yes |

### Application Permissions (Client Credentials - App Context)

| Permission | Description | Admin Consent |
|------------|-------------|---------------|
| `User.Read.All` | Read all users' full profiles | ✅ Always required |
| `User.ReadBasic.All` | Read all users' basic profiles | ✅ Always required |
| `Mail.Read` | Read all mailboxes | ✅ Always required |
| `Files.Read.All` | Read all files | ✅ Always required |

**Key Difference:** ALL application permissions require admin consent. Delegated permissions like `User.Read` don't.

---

## Microsoft Documentation

### On-Behalf-Of Flow

**Official Microsoft Documentation:**
- [Microsoft Identity Platform and OAuth 2.0 On-Behalf-Of flow](https://learn.microsoft.com/en-us/azure/active-directory/develop/v2-oauth2-on-behalf-of-flow)
- [Scenario: Web API that calls web APIs](https://learn.microsoft.com/en-us/azure/active-directory/develop/scenario-web-api-call-api-overview)
- [OnBehalfOfCredential Class](https://learn.microsoft.com/en-us/dotnet/api/azure.identity.onbehalfofcredential)

### MSAL (Alternative Approach)

**MSAL for JavaScript/Blazor:**
- [MSAL.js Overview](https://learn.microsoft.com/en-us/azure/active-directory/develop/msal-overview)
- [Tutorial: Sign in users in a Blazor WebAssembly app](https://learn.microsoft.com/en-us/azure/active-directory/develop/tutorial-blazor-webassembly)
- [Microsoft.Authentication.WebAssembly.Msal package](https://www.nuget.org/packages/Microsoft.Authentication.WebAssembly.Msal/)
- [Secure an ASP.NET Core Blazor WebAssembly standalone app with Azure AD](https://learn.microsoft.com/en-us/aspnet/core/blazor/security/webassembly/standalone-with-azure-active-directory)

### Token Handling

**Azure Static Web Apps Limitations:**
- [Azure Static Web Apps authentication and authorization](https://learn.microsoft.com/en-us/azure/static-web-apps/authentication-authorization)
- [Accessing user information](https://learn.microsoft.com/en-us/azure/static-web-apps/user-information)
- [GitHub Issue: Token store not available in Static Web Apps](https://github.com/Azure/static-web-apps/issues/794)

**Azure App Service Token Store (Not in SWA):**
- [Authentication and authorization in Azure App Service](https://learn.microsoft.com/en-us/azure/app-service/overview-authentication-authorization)
- [Access tokens in App Service](https://learn.microsoft.com/en-us/azure/app-service/configure-authentication-oauth-tokens)

### Permissions and Consent

**Admin Consent:**
- [Understanding Azure AD application consent experiences](https://learn.microsoft.com/en-us/azure/active-directory/develop/application-consent-experience)
- [Configure the admin consent workflow](https://learn.microsoft.com/en-us/azure/active-directory/manage-apps/configure-admin-consent-workflow)
- [Permissions and consent in the Microsoft identity platform](https://learn.microsoft.com/en-us/azure/active-directory/develop/v2-permissions-and-consent)

**Permission Types:**
- [Microsoft Graph permissions reference](https://learn.microsoft.com/en-us/graph/permissions-reference)
- [Application vs delegated permissions](https://learn.microsoft.com/en-us/graph/auth/auth-concepts#microsoft-graph-permissions)

### Microsoft Graph

**Calling Graph API:**
- [Microsoft Graph REST API v1.0 endpoint reference](https://learn.microsoft.com/en-us/graph/api/overview)
- [Get user](https://learn.microsoft.com/en-us/graph/api/user-get)
- [Best practices for Microsoft Graph](https://learn.microsoft.com/en-us/graph/best-practices-concept)

---

## Decision Matrix

Use this to decide which approach to implement:

| Requirement | Client Credentials | MSAL | OBO Flow |
|-------------|-------------------|------|----------|
| **Works in Azure Static Web Apps** | ✅ Yes | ✅ Yes (different auth) | ❌ No |
| **No admin consent needed** | ❌ No | ✅ Yes (for User.Read) | ❌ No |
| **Read user profiles** | ✅ Yes | ✅ Yes | ✅ Yes (if feasible) |
| **Access user's files/email** | ❌ No | ✅ Yes | ✅ Yes (if feasible) |
| **Simple implementation** | ✅ Yes | ❌ Moderate | ❌ Complex |
| **Keep SWA auth** | ✅ Yes | ❌ Must replace | ✅ Yes (if feasible) |
| **Token in browser** | ✅ No (secure) | ❌ Yes (less secure) | ✅ No |
| **Background jobs** | ✅ Yes | ❌ No | ❌ No |

---

## Summary

### For This Project

**Current Status:**
- ✅ Client Credentials flow implemented correctly
- ❌ Blocked by missing admin consent
- ❌ OBO flow not feasible in Static Web Apps architecture

**Recommendation:**

**If you can get admin consent:**
→ Stick with current implementation (Client Credentials)  
→ Simplest and most reliable

**If admin consent is impossible:**
→ Migrate to MSAL in Blazor  
→ More work but avoids admin consent requirement

**Don't attempt:**
→ OBO flow in Static Web Apps (won't work)

---

## Related Files

- `Research/application-auth.md` - Current implementation details
- `GRAPH_API_SETUP.md` - Setup instructions
- `Api/Services/GraphService.cs` - Current implementation (Client Credentials)
- `Client/Services/AuthenticationService.cs` - Static Web Apps auth helper

---

## Conclusion

While On-Behalf-Of flow is a powerful authentication pattern, it **cannot be implemented in Azure Static Web Apps** due to architectural limitations. The two viable alternatives are:

1. **Client Credentials** (current) - Requires admin consent
2. **MSAL in Blazor** - No admin consent, but more complex

Choose based on your ability to obtain admin consent and your application's requirements for accessing user-owned resources.

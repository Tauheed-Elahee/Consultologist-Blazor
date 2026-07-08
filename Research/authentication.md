# Authentication Implementation Research

**Date:** 2025-11-17  
**Status:** Current implementation using Azure Static Web Apps (SWA) EasyAuth with Azure Entra ID

---

## Executive Summary

This Blazor WebAssembly application uses **Azure Static Web Apps Built-in Authentication (EasyAuth)** with **Microsoft Azure Entra ID** as the identity provider. This is a platform-managed authentication solution where Azure handles the entire OAuth flow server-side.

### Key Findings

- **Current Setup:** Azure Entra ID via Azure Static Web Apps platform
- **Identity Provider:** Microsoft Azure Entra ID (formerly Azure Active Directory)
- **Primary Benefit:** Simple, secure, zero-maintenance authentication
- **Primary Limitation:** Cannot access tokens to call Microsoft Graph API or other Azure services on behalf of users

---

## Table of Contents

1. [Current Implementation](#current-implementation)
2. [Architecture & Authentication Flow](#architecture--authentication-flow)
3. [Alternative: Direct MSAL Integration](#alternative-direct-msal-integration)
4. [Detailed Comparison](#detailed-comparison)
5. [Token Management](#token-management)
6. [Capabilities & Limitations](#capabilities--limitations)
7. [Developer Experience](#developer-experience)
8. [Deployment & Hosting](#deployment--hosting)
9. [Migration Scenarios](#migration-scenarios)
10. [Recommendations](#recommendations)

---

## Current Implementation

### Overview

**Method:** Azure Static Web Apps (SWA) Built-in Authentication (EasyAuth)  
**Identity Provider:** Microsoft Azure Entra ID (AAD)  
**Architecture:** Platform-managed, server-side authentication

### Configuration

**File:** `Client/staticwebapp.config.json`
```json
{
  "routes": [
    {"route": "/admin/*", "allowedRoles": ["admin"]},
    {"route": "/clinician/*", "allowedRoles": ["clinician", "admin"]},
    {"route": "/api/*", "allowedRoles": ["authenticated"]},
    {"route": "/login", "statusCode": 200}
  ],
  "responseOverrides": {
    "401": {"redirect": "/.auth/login/aad?post_login_redirect_uri=/"},
    "403": {"rewrite": "/forbidden.html"}
  }
}
```

### Client-Side Implementation

**Authentication Service:** `Client/Services/AuthenticationService.cs`
```csharp
public class AuthenticationService
{
    private readonly HttpClient _httpClient;

    public AuthenticationService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ClientPrincipal?> GetUserInfoAsync()
    {
        var response = await _httpClient.GetFromJsonAsync<AuthResponse>("/.auth/me");
        return response?.ClientPrincipal;
    }
}
```

**Login Component:** `Client/Components/LoginDisplay.razor`
```razor
@if (user != null)
{
    <span>Hello, @user.UserDetails!</span>
    <a href="/.auth/logout" class="btn btn-link">Logout</a>
}
else
{
    <a href="/.auth/login/aad" class="btn btn-primary">Login</a>
}
```

### Server-Side Implementation

**Authentication Helper:** `Api/Helpers/AuthenticationHelper.cs`
```csharp
public static ClientPrincipal? GetClientPrincipal(HttpRequestData req)
{
    if (!req.Headers.TryGetValues("x-ms-client-principal", out var headerValues))
        return null;
        
    var header = headerValues.FirstOrDefault();
    if (string.IsNullOrEmpty(header))
        return null;
        
    var decoded = Convert.FromBase64String(header);
    var json = Encoding.UTF8.GetString(decoded);
    var principal = JsonSerializer.Deserialize<ClientPrincipal>(json);
    return principal;
}

public static bool IsAuthenticated(HttpRequestData req)
{
    var principal = GetClientPrincipal(req);
    return principal != null;
}
```

**Usage in API Functions:** `Api/WeatherForecastFunction.cs`
```csharp
[Function("WeatherForecast")]
public HttpResponseData Run([HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req)
{
    if (!AuthenticationHelper.IsAuthenticated(req))
    {
        return req.CreateResponse(HttpStatusCode.Unauthorized);
    }
    
    var principal = AuthenticationHelper.GetClientPrincipal(req);
    _logger.LogInformation($"User: {principal?.UserDetails}");
    
    // Process request...
}
```

### User Model

**File:** `Client/Models/UserInfo.cs` and `Api/Models/ClientPrincipal.cs`
```csharp
public class ClientPrincipal
{
    public string? IdentityProvider { get; set; }  // e.g., "aad"
    public string? UserId { get; set; }
    public string? UserDetails { get; set; }
    public IEnumerable<string>? UserRoles { get; set; }
    public IEnumerable<Claim>? Claims { get; set; }
}
```

### Role Management

**Custom Function:** `Api/GetRolesForUser/GetRolesForUser.cs`

This function maps Azure AD groups to application roles:
```csharp
// Maps Azure AD groups to roles
if (groupIds.Contains("<AAD-GROUP-ID-FOR-CLINICIANS>"))
    roles.Add("clinician");

if (groupIds.Contains("<AAD-GROUP-ID-FOR-ADMINS>"))
    roles.Add("admin");
```

**Note:** Currently uses placeholder group IDs that need to be configured with actual Azure AD group IDs.

### NuGet Packages

**Client Project:** `Client.csproj`
- `Microsoft.AspNetCore.Components.WebAssembly` (v8.0.0)
- **NO authentication-specific packages** - relies on SWA built-in auth

**API Project:** `Api.csproj`
- `Microsoft.Azure.Functions.Worker` (v1.20.0)
- `Microsoft.Azure.Functions.Worker.Extensions.Http` (v3.1.0)
- **NO authentication-specific packages** - uses SWA's `x-ms-client-principal` header

---

## Architecture & Authentication Flow

### Azure Static Web Apps EasyAuth Flow

```
1. User clicks login → /.auth/login/aad
2. Azure SWA platform redirects to Azure Entra ID
3. User authenticates with Microsoft credentials
4. Azure SWA creates StaticWebAppsAuthCookie (HTTP-only, secure)
5. User redirected back to app with cookie set
6. Client queries /.auth/me to get user information
7. Every API request includes cookie automatically
8. SWA edge nodes inject x-ms-client-principal header to API
```

### Where Tokens Are Stored

- **Access tokens:** Managed server-side by Azure platform (NOT accessible to app code)
- **Session:** `StaticWebAppsAuthCookie` (HTTP-only cookie, client-side)
- **User identity:** Base64-encoded in `x-ms-client-principal` header (server-side only)

### User Identity Flow

```
Browser → Cookie → SWA Edge → x-ms-client-principal Header → Azure Function
```

### Security Model

- Platform handles all OAuth flows
- Tokens never exposed to client JavaScript
- Cookie-based sessions (immune to XSS token theft)
- Authorization via `staticwebapp.config.json` routes
- No CORS issues (same origin after platform routing)

---

## Alternative: Direct MSAL Integration

### Microsoft.Identity.Web + MSAL Approach

This alternative provides direct client-side authentication management using the Microsoft Authentication Library (MSAL).

### Required Packages

**Client Project:**
```xml
<PackageReference Include="Microsoft.Authentication.WebAssembly.Msal" Version="8.0.0" />
<PackageReference Include="Microsoft.AspNetCore.Components.WebAssembly.Authentication" Version="8.0.0" />
```

**API Project:**
```xml
<PackageReference Include="System.IdentityModel.Tokens.Jwt" Version="7.0.3" />
<PackageReference Include="Microsoft.IdentityModel.Protocols.OpenIdConnect" Version="7.0.3" />
```

### Authentication Flow

```
1. Client-side MSAL initiates auth flow (PKCE)
2. User redirects to Azure Entra ID
3. Auth code returned to client
4. MSAL exchanges code for tokens (in browser)
5. Access token stored in browser (sessionStorage or localStorage)
6. Client adds Authorization: Bearer <token> to API calls
7. Azure Function validates JWT signature and claims
```

### Client Configuration

**File:** `Client/Program.cs`
```csharp
builder.Services.AddMsalAuthentication(options =>
{
    builder.Configuration.Bind("AzureAd", options.ProviderOptions.Authentication);
    options.ProviderOptions.DefaultAccessTokenScopes.Add("api://{API_CLIENT_ID}/.default");
    options.ProviderOptions.Cache.CacheLocation = "sessionStorage";
});
```

**File:** `Client/wwwroot/appsettings.json`
```json
{
  "AzureAd": {
    "Authority": "https://login.microsoftonline.com/{TENANT_ID}",
    "ClientId": "{CLIENT_APP_REGISTRATION_ID}",
    "ValidateAuthority": true
  }
}
```

### API Configuration

**File:** `Api/Program.cs`
```csharp
var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults(builder =>
    {
        builder.UseMiddleware<JwtBearerMiddleware>();
    })
    .Build();
```

**JWT Validation Middleware:** `Api/Middleware/JwtBearerMiddleware.cs`
```csharp
public class JwtBearerMiddleware : IFunctionsWorkerMiddleware
{
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var requestData = await context.GetHttpRequestDataAsync();
        
        if (!requestData.Headers.TryGetValues("Authorization", out var authHeaders))
        {
            context.Items["User"] = null;
            await next(context);
            return;
        }

        var token = authHeaders.First().Replace("Bearer ", "");
        
        // Validate JWT token
        var handler = new JwtSecurityTokenHandler();
        var principal = handler.ValidateToken(token, validationParameters, out _);
        context.Items["User"] = principal;
        
        await next(context);
    }
}
```

### Azure Setup Required

1. **Blazor Client App Registration**
   - Name: "Consultologist-Blazor-Client"
   - Redirect URIs: `https://your-site.azurestaticapps.net/authentication/login-callback`
   - API permissions: `api://{API_CLIENT_ID}/.default`

2. **API App Registration**
   - Name: "Consultologist-API"
   - Expose an API: `api://{client-id}`
   - Scopes: `access_as_user`
   - App roles: (for role-based authorization)

---

## Detailed Comparison

### Architecture Comparison

| Aspect | EasyAuth (Current) | MSAL (Direct) |
|--------|-------------------|---------------|
| **Authentication Flow** | Server-side, platform-managed | Client-side, library-managed |
| **Token Location** | Server-side (hidden from app) | Browser storage (accessible) |
| **User Identity Passing** | Cookie → Header injection | Bearer token in Authorization header |
| **Validation** | Platform pre-validates | Manual JWT validation in code |
| **Configuration** | Declarative (JSON config) | Programmatic (code + settings) |

### Implementation Comparison

| Aspect | EasyAuth (Current) | MSAL (Direct) |
|--------|-------------------|---------------|
| **Client Login** | `<a href="/.auth/login/aad">` | `Navigation.NavigateTo("authentication/login")` |
| **Get User Info** | `await HttpClient.GetFromJsonAsync("/.auth/me")` | `<AuthorizeView>@context.User</AuthorizeView>` |
| **API Auth Check** | `AuthenticationHelper.GetClientPrincipal(req)` | `context.Items["User"] as ClaimsPrincipal` |
| **Packages Required** | None | 4 packages (client + API) |
| **Azure Setup** | None (uses SWA built-in) | 2 App Registrations + permissions |
| **Lines of Code** | ~50-100 lines | ~300-400 lines |

### Security Comparison

| Aspect | EasyAuth (Current) | MSAL (Direct) |
|--------|-------------------|---------------|
| **Token Storage** | Server-side (most secure) | Browser storage (less secure) |
| **XSS Vulnerability** | None (HTTP-only cookie) | High (tokens in JavaScript storage) |
| **CSRF Protection** | Built-in (SameSite cookies) | Not applicable (stateless tokens) |
| **Token Theft Risk** | Very low | Medium (if XSS vulnerability exists) |
| **Platform Trust** | Trust Azure to secure tokens | Trust your code to secure tokens |

---

## Token Management

### Access Token Handling

#### EasyAuth (Current)
- **Location:** Managed by Azure platform (NOT accessible to your code)
- **Lifespan:** Typically 1 hour (platform manages)
- **Usage:** Platform uses internally, app only sees user identity
- **Refresh:** Automatic, transparent to app
- **API Calls:** Cookie-based, no bearer tokens

**Critical Limitation:** Cannot access raw access token to call Graph API or other Azure services!

#### MSAL (Direct)
- **Location:** Browser sessionStorage/localStorage (accessible via JavaScript)
- **Lifespan:** 1 hour (configurable in Azure AD)
- **Usage:** Manually attached to API requests as `Authorization: Bearer {token}`
- **Refresh:** Managed by MSAL library automatically
- **API Calls:** Token-based authentication

**Example - Getting Access Token:**
```csharp
@inject IAccessTokenProvider TokenProvider

var tokenResult = await TokenProvider.RequestAccessToken();
if (tokenResult.TryGetToken(out var token))
{
    var accessToken = token.Value;
    // Use token to call Graph API, Azure services, etc.
}
```

### Refresh Token Handling

#### EasyAuth (Current)
- **Location:** Managed by platform server-side
- **Refresh Flow:** Automatic, transparent
- **User Experience:** Seamless, no interruption
- **Configuration:** None needed

#### MSAL (Direct)
- **Location:** Browser storage (sessionStorage or localStorage)
- **Refresh Flow:** MSAL automatically refreshes before expiration
- **User Experience:** Usually seamless, may require re-auth if refresh token expires
- **Configuration:**
```csharp
options.ProviderOptions.Cache.CacheLocation = "localStorage"; // Persists across tabs
// or
options.ProviderOptions.Cache.CacheLocation = "sessionStorage"; // Single tab only
```

### Token Expiration

| Aspect | EasyAuth | MSAL |
|--------|----------|------|
| **Access Token** | Managed by platform | 1 hour (requires refresh) |
| **Refresh Token** | Managed by platform | 90 days (configurable) |
| **Session Duration** | Hours/days | Until refresh token expires |
| **Renewal Code** | None needed | Automatic via MSAL |
| **User Re-auth** | Rarely needed | When refresh token expires |

---

## Capabilities & Limitations

### EasyAuth (Current) - Capabilities

✅ Simple user authentication  
✅ Role-based authorization  
✅ Basic user claims (userId, userDetails, userRoles)  
✅ Route-level protection via configuration  
✅ No code needed for auth flow  
✅ Works perfectly with Azure Static Web Apps  
✅ No token management code  
✅ Automatic security updates from Azure  
✅ Cookie-based (more secure from XSS)  
✅ HTTP-only cookies prevent JavaScript access  

### EasyAuth (Current) - Limitations

❌ **Cannot access access tokens** (MAJOR limitation!)  
❌ **Cannot call Microsoft Graph API** directly  
❌ Cannot call Azure APIs (Storage, Key Vault, etc.) on behalf of user  
❌ Limited claims available  
❌ No custom scopes  
❌ Platform-locked (only works on SWA/App Service)  
❌ Cannot customize token lifetime  
❌ Cannot implement custom auth flows  
❌ Limited debugging of auth internals  
❌ Cannot use conditional access tokens  

### MSAL (Direct) - Capabilities

✅ Full access to access tokens  
✅ **Can call Microsoft Graph API** (calendar, email, OneDrive, Teams)  
✅ Can call any Azure API with proper scopes  
✅ Rich claims and custom claims  
✅ Custom scopes and permissions  
✅ Works on any hosting platform  
✅ Full control over token lifetime (via Azure AD config)  
✅ Custom authentication flows  
✅ On-behalf-of flows for service-to-service  
✅ Multi-tenant support (with configuration)  
✅ Conditional Access policy support  
✅ Built-in Blazor components (`AuthorizeView`, etc.)  

### MSAL (Direct) - Limitations

❌ More complex implementation (300+ lines of code)  
❌ Requires two Azure App Registrations  
❌ Manual token validation code  
❌ Tokens in browser (security risk if XSS vulnerability exists)  
❌ Must handle token refresh errors  
❌ CORS configuration needed  
❌ More code to maintain  
❌ Debugging authentication issues more complex  
❌ Requires understanding of OAuth/JWT  
❌ Higher maintenance burden  

### Microsoft Graph API Access

This is the **most significant difference** between the two approaches.

#### EasyAuth (Current)

**❌ CANNOT DIRECTLY ACCESS GRAPH API**

```csharp
// This does NOT work with EasyAuth - no access token available!
var response = await httpClient.GetAsync("https://graph.microsoft.com/v1.0/me");
// Returns 401 Unauthorized
```

**Workaround (Hybrid Approach):**
```csharp
// 1. Get loginHint from /.auth/me
var user = await AuthService.GetUserInfoAsync();
var loginHint = user.UserDetails; // email address

// 2. Use MSAL.js on top of EasyAuth
// Load msal.js library and acquire token silently with loginHint
// This is complex and not a clean solution
```

#### MSAL (Direct)

**✅ CLEAN GRAPH API ACCESS**

```csharp
@inject IAccessTokenProvider TokenProvider
@inject HttpClient Http

// Configure scopes in Program.cs:
options.ProviderOptions.DefaultAccessTokenScopes.Add("User.Read");
options.ProviderOptions.DefaultAccessTokenScopes.Add("Calendars.Read");

// Call Graph API:
var tokenResult = await TokenProvider.RequestAccessToken(
    new AccessTokenRequestOptions 
    { 
        Scopes = new[] { "https://graph.microsoft.com/User.Read" }
    });

if (tokenResult.TryGetToken(out var token))
{
    Http.DefaultRequestHeaders.Authorization = 
        new AuthenticationHeaderValue("Bearer", token.Value);
    
    var graphUser = await Http.GetFromJsonAsync<GraphUser>(
        "https://graph.microsoft.com/v1.0/me");
    
    var calendar = await Http.GetFromJsonAsync<CalendarEvents>(
        "https://graph.microsoft.com/v1.0/me/calendar/events");
}
```

---

## Developer Experience

### Setup Complexity

#### EasyAuth (Current)
- **Setup Time:** 15-30 minutes
- **Azure Configuration:** None (uses SWA built-in)
- **Code Changes:** Minimal (~50-100 lines)
- **Maintenance:** Very low (platform manages everything)
- **Skill Level:** Junior developer can implement
- **Learning Curve:** Minimal

**Setup Steps:**
1. Create `staticwebapp.config.json`
2. Create `AuthenticationService.cs`
3. Create `AuthenticationHelper.cs`
4. Add login/logout links
5. Deploy to Azure Static Web Apps

#### MSAL (Direct)
- **Setup Time:** 2-4 hours (first time), 1-2 hours (experienced)
- **Azure Configuration:** 2 App Registrations, permissions, scopes
- **Code Changes:** Significant (~300-400 lines)
- **Maintenance:** Medium (handle MSAL updates, token issues)
- **Skill Level:** Intermediate to advanced developer
- **Learning Curve:** Steep (OAuth, JWT, PKCE concepts)

**Setup Steps:**
1. Create Blazor Client App Registration
2. Create API App Registration
3. Configure API permissions and scopes
4. Grant admin consent
5. Configure redirect URIs
6. Install NuGet packages (4 packages)
7. Configure `appsettings.json` (client + API)
8. Implement authentication state provider
9. Create authorization message handler
10. Implement JWT validation middleware
11. Update all auth-related components
12. Configure CORS
13. Test token flows

### Debugging Capabilities

#### EasyAuth (Current)

**Debugging:**
```csharp
var principal = AuthenticationHelper.GetClientPrincipal(req);
_logger.LogInformation($"User: {principal?.UserId}");
_logger.LogInformation($"Roles: {string.Join(",", principal?.UserRoles)}");
```

**Pros:**
- Simple header inspection
- SWA CLI for local testing

**Cons:**
- Black box authentication (platform-managed)
- Limited logging of auth flow
- Hard to debug token issues (no access to tokens)
- Must rely on Azure platform logs

#### MSAL (Direct)

**Debugging:**
```csharp
// Rich debugging - see full token
var handler = new JwtSecurityTokenHandler();
var jsonToken = handler.ReadToken(token) as JwtSecurityToken;

foreach (var claim in jsonToken.Claims)
{
    _logger.LogInformation($"Claim: {claim.Type} = {claim.Value}");
}

// Inspect token at jwt.ms
// Copy token from browser DevTools → https://jwt.ms
```

**Pros:**
- Full visibility into tokens
- Rich browser console logs from MSAL
- Can inspect token claims
- jwt.ms for token inspection

**Cons:**
- More moving parts to debug
- Token expiration issues
- CORS errors common
- MSAL error messages sometimes cryptic

### Local Development Experience

#### EasyAuth (Current)

**Using Static Web Apps CLI:**
```bash
npm install -g @azure/static-web-apps-cli

# Start development
swa start http://localhost:5000 --api-location http://localhost:7071

# Simulated auth at:
# http://localhost:4280/.auth/login/aad?post_login_redirect_uri=/
```

**Pros:**
- SWA CLI simulates platform well
- Can test auth flows locally
- Mock users for testing

**Cons:**
- Additional tool required (SWA CLI)
- Not identical to production
- Limited provider simulation
- Need to configure mock users

#### MSAL (Direct)

**Standard dotnet run:**
```bash
# Client
cd Client && dotnet run

# API
cd Api && func start

# Real Azure AD authentication even locally
```

**Pros:**
- Authentic authentication flow
- Same tokens in dev and prod
- No special tooling needed
- Standard debugging tools work

**Cons:**
- Requires internet connection
- Redirect URIs must include localhost
- Real users/accounts needed for testing
- Cannot test offline

### Testing Authentication Flows

#### EasyAuth (Current)

```csharp
// Unit testing - must mock platform behavior
var mockRequest = new Mock<HttpRequestData>();
var headerValues = new[] { encodedPrincipal };
mockRequest.Setup(r => r.Headers.TryGetValues("x-ms-client-principal", out headerValues))
    .Returns(true);
    
var principal = AuthenticationHelper.GetClientPrincipal(mockRequest.Object);
Assert.NotNull(principal);
```

**Challenges:**
- Must mock SWA platform behavior
- Integration tests require SWA CLI or deployment
- Hard to test different auth scenarios

#### MSAL (Direct)

```csharp
// Unit testing - easy to mock
var mockTokenProvider = new Mock<IAccessTokenProvider>();
mockTokenProvider.Setup(x => x.RequestAccessToken(It.IsAny<AccessTokenRequestOptions>()))
    .ReturnsAsync(new AccessTokenResult(...));

// Can test with real tokens
// Integration tests more straightforward
```

**Benefits:**
- Standard mocking patterns
- Can use real tokens in tests
- Integration tests straightforward

---

## Deployment & Hosting

### Platform Requirements

#### EasyAuth (Current)

**MUST be hosted on:**
- Azure Static Web Apps
- Azure App Service (with EasyAuth enabled)

**CANNOT deploy to:**
- IIS
- Kestrel standalone
- AWS/GCP
- Docker containers (without App Service)
- GitHub Pages
- Netlify, Vercel, etc.

**Complete platform lock-in to Azure**

#### MSAL (Direct)

**CAN deploy ANYWHERE:**
- Azure Static Web Apps (yes, can use both!)
- Azure App Service
- IIS
- Docker
- Kubernetes
- AWS/GCP
- Any static site host + separate API
- On-premises servers
- GitHub Pages (client only)
- Netlify, Vercel (client only)

**Platform agnostic, fully portable**

### Portability to Other Hosting Environments

#### Migrating Away from EasyAuth

**Difficulty:** HARD - Complete rewrite needed

**Required Changes:**
1. Remove all `/.auth/*` calls
2. Install MSAL packages
3. Implement MSAL authentication
4. Rewrite `AuthenticationService`
5. Rewrite `AuthenticationHelper`
6. Change all login/logout links
7. Add token management code
8. Create 2 Azure App Registrations
9. Configure API permissions and scopes
10. Reconfigure routing/authorization
11. Update deployment pipeline
12. Update tests

**Estimated Effort:** 1-2 weeks for experienced developer

#### Migrating Away from MSAL

**Difficulty:** EASY - Portable code

**Required Changes:**
- Deployment configuration
- Environment variables
- CORS settings (if moving domains)
- No code changes needed

**Estimated Effort:** Few hours for deployment config

### CI/CD Considerations

#### EasyAuth (Current)

**File:** `.github/workflows/azure-static-web-apps-*.yml`
```yaml
- name: Build And Deploy
  uses: Azure/static-web-apps-deploy@v1
  with:
    azure_static_web_apps_api_token: ${{ secrets.AZURE_STATIC_WEB_APPS_API_TOKEN }}
    app_location: "./Client"
    api_location: "Api"
    output_location: "wwwroot"
```

**Pros:**
- Single deployment action
- Platform configures everything
- No auth configuration in CI/CD
- Simple pipeline

**Cons:**
- Locked to SWA deployment
- Cannot deploy separately
- Must use Azure's workflow
- Limited flexibility

#### MSAL (Direct)

```yaml
# Can use any deployment method
- name: Publish Blazor
  run: dotnet publish Client/Client.csproj -c Release -o ./publish/client
  
- name: Publish API
  run: dotnet publish Api/Api.csproj -c Release -o ./publish/api

- name: Deploy Client
  # Deploy to any static host

- name: Deploy API
  # Deploy to any platform
```

**Pros:**
- Flexible deployment options
- Can deploy client and API separately
- Can use blue/green deployments
- Works with any CI/CD system
- Platform-independent

**Cons:**
- More configuration needed
- Must manage App Registration secrets
- More complex pipelines
- Need to configure environment variables

---

## Migration Scenarios

### Scenario 1: Stay with EasyAuth (No Changes)

**When:**
- You only need basic authentication
- You don't need to call Graph API or Azure services
- You're happy with Azure Static Web Apps
- You value simplicity over flexibility

**Action:** None - current implementation is solid

### Scenario 2: Migrate to MSAL

**When:**
- You need to call Microsoft Graph API
- You need to access Azure services on behalf of users
- You want to host elsewhere in the future
- You need advanced authentication scenarios

**Effort:** 2-3 days for small app

**High-Level Steps:**

1. **Azure Setup** (2-3 hours)
   - Create Blazor Client App Registration
   - Create API App Registration
   - Configure permissions and scopes
   - Grant admin consent

2. **Client Changes** (4-6 hours)
   - Install MSAL packages
   - Configure authentication in `Program.cs`
   - Create `appsettings.json` with Azure AD config
   - Create `ApiAuthorizationMessageHandler`
   - Update `App.razor` with `<CascadingAuthenticationState>`
   - Create `Authentication.razor` page
   - Update `LoginDisplay.razor` to use `<AuthorizeView>`
   - Update all protected pages with `@attribute [Authorize]`

3. **API Changes** (3-4 hours)
   - Install JWT packages
   - Create `JwtBearerMiddleware`
   - Update `Program.cs` to use middleware
   - Update `AuthenticationHelper` to use `ClaimsPrincipal`
   - Update all functions to accept `FunctionContext`
   - Test JWT validation

4. **Configuration & Testing** (2-3 hours)
   - Update `staticwebapp.config.json`
   - Configure CORS if needed
   - Update redirect URIs in Azure
   - Test authentication flow
   - Test API calls with tokens
   - Test Graph API access

5. **Cleanup** (1 hour)
   - Remove old `AuthenticationService` (or update it)
   - Remove `ClientPrincipal` model
   - Update documentation

**Total Estimated Effort:** 12-17 hours (2-3 days)

### Scenario 3: Hybrid Approach

**When:**
- You need Graph API access
- You want to keep EasyAuth for app authentication
- You're okay with added complexity

**Approach:**
- Keep EasyAuth for app authentication
- Add MSAL.js (JavaScript library) for Graph API token acquisition only
- Use `loginHint` from `/.auth/me` to silently acquire Graph tokens

**Complexity:** High - managing two auth systems

**Recommendation:** Not recommended unless absolutely necessary

---

## Recommendations

### Decision Matrix

Use this matrix to decide which approach to use:

#### **Stay with EasyAuth (Current) if:**

✅ You only need basic user authentication  
✅ You don't need to call Microsoft Graph API  
✅ You don't need to call Azure APIs on behalf of users  
✅ You want the simplest possible implementation  
✅ You're comfortable being locked into Azure SWA  
✅ You prioritize security over flexibility  
✅ You have limited authentication expertise  
✅ You don't need access to raw tokens  
✅ You value minimal maintenance burden  

#### **Switch to MSAL if:**

✅ **You need to call Microsoft Graph API** (most common reason!)  
✅ You need to call Azure APIs (Storage, Key Vault, etc.) on behalf of users  
✅ You need rich claims and custom claims  
✅ You want portability to other hosting platforms  
✅ You need fine-grained scope-based authorization  
✅ You're implementing multi-tenant scenarios with custom logic  
✅ You need on-behalf-of flows  
✅ You want full control over the authentication experience  
✅ You might migrate away from Azure SWA in the future  
✅ You have intermediate to advanced authentication expertise  

### The Critical Question

**Do you need to call Microsoft Graph API or other Azure services on behalf of users?**

- **No** → Keep EasyAuth (simpler, more secure, less maintenance)
- **Yes** → Migrate to MSAL (required for token access)
- **Maybe later** → Stay with EasyAuth now, migrate when needed

### Current Status: EasyAuth is Appropriate

Based on the current codebase analysis:

✅ **Current implementation is well-architected**  
✅ **EasyAuth is appropriate for current needs**  
✅ **No immediate need to migrate**  

**Recommendation:** Keep the current EasyAuth implementation unless:
1. You identify a need to call Microsoft Graph API
2. You need to access user data from Office 365, OneDrive, Teams, etc.
3. You need to migrate away from Azure Static Web Apps

### Future Considerations

**If you later need Graph API access:**
- Budget 2-3 days for MSAL migration
- Plan Azure App Registration setup
- Consider whether hybrid approach makes sense
- Document decision and migration path

**If you need to change hosting platforms:**
- MSAL migration becomes necessary
- Budget additional time for deployment reconfiguration
- Consider portability in architecture decisions

---

## Additional Resources

### Documentation

**Azure Static Web Apps Authentication:**
- [Azure SWA Authentication Overview](https://docs.microsoft.com/en-us/azure/static-web-apps/authentication-authorization)
- [Custom Authentication in Azure SWA](https://docs.microsoft.com/en-us/azure/static-web-apps/authentication-custom)

**Microsoft Identity Platform:**
- [Microsoft Identity Platform Documentation](https://docs.microsoft.com/en-us/azure/active-directory/develop/)
- [MSAL for .NET](https://docs.microsoft.com/en-us/azure/active-directory/develop/msal-overview)
- [Microsoft.Identity.Web Documentation](https://docs.microsoft.com/en-us/azure/active-directory/develop/microsoft-identity-web)

**Blazor Authentication:**
- [Secure Blazor WebAssembly](https://docs.microsoft.com/en-us/aspnet/core/blazor/security/webassembly/)
- [Blazor WebAssembly with Azure Active Directory](https://docs.microsoft.com/en-us/aspnet/core/blazor/security/webassembly/azure-active-directory)

### Tools

- **JWT Debugger:** https://jwt.ms - Decode and inspect JWT tokens
- **Azure Portal:** https://portal.azure.com - Manage App Registrations
- **Static Web Apps CLI:** https://github.com/Azure/static-web-apps-cli

---

## Conclusion

This Blazor application uses a **well-architected authentication solution** with Azure Static Web Apps EasyAuth and Azure Entra ID. The platform-managed approach provides:

- Simple, secure authentication
- Zero maintenance overhead
- Minimal code complexity
- HTTP-only cookie security

The primary trade-off is the inability to access tokens for calling Microsoft Graph API or other Azure services on behalf of users. If this becomes a requirement, migration to direct MSAL integration would be necessary.

**Current Status:** No changes recommended unless Graph API access becomes a requirement.

---

**Document Version:** 1.0  
**Last Updated:** 2025-11-17  
**Author:** Research conducted via Claude Code

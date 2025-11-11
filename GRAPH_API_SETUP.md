# Microsoft Graph API Setup Guide - MSAL Authentication

This guide walks you through configuring Microsoft Graph API integration for user profile retrieval using **MSAL (Microsoft Authentication Library)** in Blazor WebAssembly.

## Overview

The application uses MSAL to authenticate users directly in the browser and call Microsoft Graph API to retrieve user profile information (display name, email, job title, etc.). This approach uses **delegated permissions** where:

- No admin consent required for basic `User.Read` permission
- Users authenticate directly in the browser via MSAL
- Access tokens are obtained client-side and used to call Graph API
- Each user can only access their own profile data
- **Replaces Azure Static Web Apps built-in authentication**

## Prerequisites

- Azure subscription with access to Azure Active Directory
- Azure Static Web App for hosting
- Access to Azure Portal to configure App Registration

---

## Part 1: Azure AD App Registration Configuration

### Step 1: Create or Update Your App Registration

1. Navigate to [Azure Portal](https://portal.azure.com)
2. Go to **Azure Active Directory**
3. Click **App registrations** in the left menu
4. Either find your existing app registration or click **New registration**:
   - **Name**: Your application name (e.g., "Consultologist Blazor")
   - **Supported account types**: Choose based on your needs (Single tenant recommended)
   - **Redirect URI**: Select **Single-page application (SPA)** and enter:
     - For local development: `http://localhost:5000/authentication/login-callback`
     - For production: `https://YOUR-APP-URL/authentication/login-callback`
   - Click **Register**

### Step 2: Configure Authentication Settings

1. In your App Registration, click **Authentication** in the left menu
2. Under **Single-page application**, add redirect URIs:
   - `http://localhost:5000/authentication/login-callback` (development)
   - `https://YOUR-APP-URL/authentication/login-callback` (production)
3. Under **Logout URL**, add:
   - `http://localhost:5000/authentication/logout-callback` (development)
   - `https://YOUR-APP-URL/authentication/logout-callback` (production)
4. Under **Implicit grant and hybrid flows**, ensure nothing is checked (MSAL uses authorization code flow with PKCE)
5. Click **Save**

### Step 3: Add Microsoft Graph API Permissions

1. In your App Registration, click **API permissions** in the left menu
2. Click **Add a permission**
3. Select **Microsoft Graph**
4. Select **Delegated permissions**
5. Search for and select **User.Read**
   - This permission allows reading the signed-in user's profile
   - Does NOT require admin consent
6. Click **Add permissions**

### Step 4: Note Your Configuration Values

From the **Overview** page of your App Registration, note down:

- **Application (client) ID** - You'll use this in `appsettings.json`
- **Directory (tenant) ID** - You'll use this in `appsettings.json`

---

## Part 2: Client Application Configuration

### Step 1: Update appsettings.json Files

The application has two configuration files that need your Azure AD values:

#### Production Configuration: `Client/wwwroot/appsettings.json`

```json
{
  "AzureAd": {
    "Authority": "https://login.microsoftonline.com/YOUR_TENANT_ID",
    "ClientId": "YOUR_CLIENT_ID",
    "ValidateAuthority": true
  },
  "MicrosoftGraph": {
    "BaseUrl": "https://graph.microsoft.com/v1.0",
    "Scopes": ["User.Read"]
  }
}
```

Replace:
- `YOUR_TENANT_ID` with your Directory (tenant) ID from Step 4
- `YOUR_CLIENT_ID` with your Application (client) ID from Step 4

#### Development Configuration: `Client/wwwroot/appsettings.Development.json`

```json
{
  "AzureAd": {
    "Authority": "https://login.microsoftonline.com/YOUR_TENANT_ID",
    "ClientId": "YOUR_CLIENT_ID",
    "ValidateAuthority": true
  },
  "MicrosoftGraph": {
    "BaseUrl": "https://graph.microsoft.com/v1.0",
    "Scopes": ["User.Read"]
  },
  "API_Prefix": "http://localhost:7071"
}
```

Replace the same values as above.

**Note:** These files contain only public configuration (no secrets) and are safe to commit to source control.

### Step 2: Verify NuGet Packages

Ensure `Client/Client.csproj` includes:

```xml
<PackageReference Include="Microsoft.Authentication.WebAssembly.Msal" Version="8.0.10" />
```

If not already present, run:

```bash
dotnet add Client package Microsoft.Authentication.WebAssembly.Msal
```

---

## Part 3: Testing the Integration

### Step 1: Start Local Development

1. Start the Blazor client:
   ```bash
   cd Client
   dotnet run
   ```

2. (Optional) If you need API functions, start the Azure Functions backend in a separate terminal:
   ```bash
   cd Api
   func start
   ```

### Step 2: Test Authentication and Profile Retrieval

1. Navigate to your local app (usually `http://localhost:5000`)
2. Click **Login** - you'll be redirected to Microsoft login page
3. Sign in with your Azure AD credentials
4. After successful login, you should be redirected back to the app
5. You should see:
   - Header shows: "Hello, [Your Display Name]!" 
   - Profile page shows full profile information from Microsoft Graph

### Step 3: Verify Profile Data

Navigate to `/profile` and verify you see:
- **Authentication State** section showing you're authenticated
- **Microsoft Graph Profile Data** section with:
  - Display Name
  - Given Name and Surname
  - Email (Mail)
  - User Principal Name
  - Job Title
  - Office Location
  - Phone numbers
- **User Claims** section showing all OIDC claims from Azure AD

---

## Architecture

```
User Browser
    ↓ (authenticates with MSAL)
Microsoft Identity Platform (Azure AD)
    ↓ (returns access token)
User Browser (Blazor WASM)
    ↓ (calls Graph API with access token)
Microsoft Graph API (/me)
    ↓
User Profile Data
```

### Flow:

1. User clicks Login → MSAL redirects to Azure AD
2. User authenticates with Azure AD
3. Azure AD redirects back with authorization code
4. MSAL exchanges code for access token (using PKCE)
5. Access token stored in browser (session storage)
6. Blazor app uses token to call Graph API directly
7. Graph API returns user profile data
8. Profile displayed in UI

### Key Differences from Static Web Apps Auth:

| Aspect | Static Web Apps Auth | MSAL (Current) |
|--------|---------------------|----------------|
| Authentication Location | Server-side | Client-side (browser) |
| Token Storage | Server session | Browser session storage |
| Graph API Calls | Via Azure Functions | Direct from browser |
| Admin Consent Required | Yes (for app permissions) | No (for User.Read) |
| Offline Access | No | Optional (with refresh tokens) |
| Custom Login UI | No | Yes (customizable) |

---

## Troubleshooting

### Issue: Login redirect not working

**Possible causes:**
1. Redirect URI not configured in Azure AD
2. Client ID or Tenant ID incorrect
3. App Registration configured for wrong account type

**Solution:**
- Verify redirect URIs in Azure AD match your app URL exactly
- Check `appsettings.json` values against Azure AD App Registration
- Ensure App Registration allows your account type (single/multi-tenant)

### Issue: "User.Read permission not granted"

**Possible causes:**
1. Permission not added in Azure AD
2. User needs to consent to permissions

**Solution:**
- Add User.Read delegated permission in Azure AD
- On first login, user will see consent screen - click Accept
- User.Read doesn't require admin consent

### Issue: Graph API call fails with 401 Unauthorized

**Possible causes:**
1. Access token expired
2. Token not included in request
3. Wrong scope requested

**Solution:**
- MSAL automatically handles token refresh
- Verify `UserProfileService.cs` includes Bearer token in request headers
- Check that "User.Read" scope is configured in `appsettings.json`

### Issue: Profile shows "(not available)" for all fields

**Possible causes:**
1. Graph API call failing
2. User's Azure AD profile incomplete
3. Token doesn't have correct permissions

**Solution:**
- Open browser console (F12) and check for errors
- Verify user has profile data in Azure AD
- Check that access token includes User.Read scope (inspect token at jwt.ms)

### Issue: Build errors about missing namespaces

**Possible causes:**
1. NuGet package not installed
2. Implicit usings not enabled

**Solution:**
- Run `dotnet restore` from project root
- Verify `Microsoft.Authentication.WebAssembly.Msal` package is installed
- Check that files include necessary `@using` directives

---

## Security Best Practices

### Token Storage

✅ MSAL stores tokens in **sessionStorage** by default (secure for SPAs)
✅ Tokens cleared when browser tab closes
✅ PKCE (Proof Key for Code Exchange) used for authorization code flow
✅ No secrets stored in client code (only Client ID, which is public)

### Production Hardening

✅ Enable **Conditional Access** policies in Azure AD
✅ Configure **Token Lifetime** policies (shorter is more secure)
✅ Enable **Multi-Factor Authentication (MFA)** for users
✅ Monitor sign-in logs in Azure AD
✅ Use **https** only in production (required for SPA auth)

### CORS Configuration

If calling Azure Functions from the SPA:
- Configure CORS in Azure Functions to allow your Static Web App domain
- Never use wildcard (*) in production
- Limit to specific origins

---

## Files Modified for MSAL Migration

### New Files Created:
- `Client/wwwroot/appsettings.json` - Production configuration
- `Client/Pages/Authentication.razor` - Handles login/logout callbacks
- `Client/Shared/RedirectToLogin.razor` - Redirects unauthenticated users

### Files Modified:
- `Client/Client.csproj` - Added MSAL package
- `Client/wwwroot/appsettings.Development.json` - Added Azure AD config
- `Client/Program.cs` - Configured MSAL authentication
- `Client/App.razor` - Added CascadingAuthenticationState and AuthorizeRouteView
- `Client/Services/UserProfileService.cs` - Rewritten to use MSAL tokens and call Graph API directly
- `Client/Components/LoginDisplay.razor` - Uses AuthorizeView and MSAL auth state
- `Client/Pages/Profile.razor` - Uses MSAL authentication state
- `Client/staticwebapp.config.json` - Removed Static Web Apps auth configuration

### Files No Longer Used:
- `Client/Services/AuthenticationService.cs` - Replaced by MSAL
- Azure Function `/api/GetUserProfile` - Graph calls now happen client-side

---

## API Endpoints (Optional - if using Azure Functions)

The Azure Functions API endpoints are now optional since Graph API calls happen directly from the browser. However, you may still need the API for:
- Calling other Azure services
- Server-side business logic
- Role-based access control

If you need to call Azure Functions from the authenticated Blazor app, you have two options:

### Option 1: Keep Static Web Apps Managed Functions
- Functions run on Static Web Apps infrastructure
- Use `x-ms-client-principal` header for authentication
- No extra cost

### Option 2: Use Separate Function App with Azure AD Auth
- More control and scalability
- Authenticate API calls using the same MSAL token
- Configure Function App to validate Azure AD tokens

---

## Advanced Scenarios

### Adding Additional Graph API Permissions

To access more user data (e.g., calendar, mail):

1. Add permission in Azure AD App Registration
2. Update `appsettings.json` to include the scope:
   ```json
   "Scopes": ["User.Read", "Calendars.Read", "Mail.Read"]
   ```
3. User will be prompted to consent on next login
4. Update `UserProfileService.cs` to request specific scopes:
   ```csharp
   var tokenResult = await _tokenProvider.RequestAccessToken(new AccessTokenRequestOptions
   {
       Scopes = new[] { "Calendars.Read" }
   });
   ```

### Calling Microsoft Graph from Azure Functions (On-Behalf-Of)

If you need the Function App to call Graph API on behalf of the user:

1. Configure Function App for Azure AD authentication
2. Pass user's access token from Blazor to Function
3. Use On-Behalf-Of flow to exchange token for Graph token
4. See `Research/obof-auth.md` for details

**Note:** This is NOT needed for basic user profile access - direct Graph calls from browser are simpler.

### Silent Token Refresh

MSAL automatically handles token refresh using a hidden iframe. No code changes needed.

### Logout

To log out:
- User clicks Logout link → navigates to `/authentication/logout`
- `Authentication.razor` handles logout callback
- Tokens cleared from browser storage
- User redirected to home page

---

## Support and Documentation

- [Microsoft Authentication Library (MSAL) for Blazor](https://learn.microsoft.com/en-us/aspnet/core/blazor/security/webassembly/msal)
- [Microsoft Graph API Documentation](https://learn.microsoft.com/en-us/graph/overview)
- [Azure AD App Registration Guide](https://learn.microsoft.com/en-us/azure/active-directory/develop/quickstart-register-app)
- [MSAL.js Browser Documentation](https://learn.microsoft.com/en-us/azure/active-directory/develop/msal-overview)
- [Secure Blazor WebAssembly](https://learn.microsoft.com/en-us/aspnet/core/blazor/security/webassembly/)

---

## Quick Reference

### Configuration Values Needed

| Setting | Description | Where to Find |
|---------|-------------|---------------|
| `Authority` | Azure AD login URL with Tenant ID | `https://login.microsoftonline.com/{TENANT_ID}` |
| `ClientId` | Application (Client) ID | App Registration → Overview |
| `Scopes` | Permissions to request | Start with `["User.Read"]` |

### Redirect URIs to Configure

| Environment | Login Callback | Logout Callback |
|-------------|----------------|-----------------|
| Local Dev | `http://localhost:5000/authentication/login-callback` | `http://localhost:5000/authentication/logout-callback` |
| Production | `https://YOUR-DOMAIN/authentication/login-callback` | `https://YOUR-DOMAIN/authentication/logout-callback` |

### Files Requiring Manual Edits

After deployment, you MUST edit these files with your actual Azure AD values:

1. `Client/wwwroot/appsettings.json` - Replace `YOUR_TENANT_ID` and `YOUR_CLIENT_ID`
2. `Client/wwwroot/appsettings.Development.json` - Replace `YOUR_TENANT_ID` and `YOUR_CLIENT_ID`

These are the ONLY manual edits required - all other code is ready to use.

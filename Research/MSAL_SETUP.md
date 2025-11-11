# MSAL Manual Setup Steps

This document contains the specific manual edits you need to make to complete the MSAL migration.

## Overview

The code migration to MSAL is complete. You now need to:
1. Configure your Azure AD App Registration
2. Update configuration files with your Azure AD values

---

## Part 1: Azure AD App Registration Configuration

### Step 1: Navigate to Your App Registration

1. Go to [Azure Portal](https://portal.azure.com)
2. Navigate to **Azure Active Directory**
3. Click **App registrations** in the left menu
4. Find your existing app registration (or create a new one if needed)

### Step 2: Configure Platform Settings

1. In your App Registration, click **Authentication** in the left menu
2. Under **Platform configurations**, find **Single-page application**
   - If it doesn't exist, click **Add a platform** → **Single-page application**
3. Add the following **Redirect URIs**:
   - For local development: `http://localhost:5000/authentication/login-callback`
   - For production: `https://YOUR-STATIC-WEB-APP-URL/authentication/login-callback`
   - Example production URL: `https://app.consultologist.ai/authentication/login-callback`
4. Add **Logout URLs** (in the same section):
   - For local development: `http://localhost:5000/authentication/logout-callback`
   - For production: `https://YOUR-STATIC-WEB-APP-URL/authentication/logout-callback`
5. Under **Implicit grant and hybrid flows**, ensure **nothing is checked** (MSAL uses modern auth flow)
6. Click **Save**

### Step 3: Verify API Permissions

1. Click **API permissions** in the left menu
2. Verify **Microsoft Graph** → **User.Read** (Delegated) permission exists
   - If not, click **Add a permission** → **Microsoft Graph** → **Delegated permissions** → Select **User.Read** → **Add permissions**
3. You do **NOT** need to click "Grant admin consent" for User.Read (it's pre-consented)

### Step 4: Copy Your Configuration Values

1. Click **Overview** in the left menu
2. **Copy and save** these two values (you'll need them in Part 2):
   - **Application (client) ID** - Example: `12345678-1234-1234-1234-123456789abc`
   - **Directory (tenant) ID** - Example: `87654321-4321-4321-4321-cba987654321`

---

## Part 2: Update Configuration Files

You need to edit **TWO files** with your Azure AD values from Part 1, Step 4.

### File 1: Production Configuration

**File:** `Client/wwwroot/appsettings.json`

**Find this:**
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

**Replace:**
- `YOUR_TENANT_ID` → Your **Directory (tenant) ID** from Part 1, Step 4
- `YOUR_CLIENT_ID` → Your **Application (client) ID** from Part 1, Step 4

**Example (after editing):**
```json
{
  "AzureAd": {
    "Authority": "https://login.microsoftonline.com/87654321-4321-4321-4321-cba987654321",
    "ClientId": "12345678-1234-1234-1234-123456789abc",
    "ValidateAuthority": true
  },
  "MicrosoftGraph": {
    "BaseUrl": "https://graph.microsoft.com/v1.0",
    "Scopes": ["User.Read"]
  }
}
```

### File 2: Development Configuration

**File:** `Client/wwwroot/appsettings.Development.json`

**Find this:**
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

**Replace:**
- `YOUR_TENANT_ID` → Your **Directory (tenant) ID** from Part 1, Step 4
- `YOUR_CLIENT_ID` → Your **Application (client) ID** from Part 1, Step 4

**Example (after editing):**
```json
{
  "AzureAd": {
    "Authority": "https://login.microsoftonline.com/87654321-4321-4321-4321-cba987654321",
    "ClientId": "12345678-1234-1234-1234-123456789abc",
    "ValidateAuthority": true
  },
  "MicrosoftGraph": {
    "BaseUrl": "https://graph.microsoft.com/v1.0",
    "Scopes": ["User.Read"]
  },
  "API_Prefix": "http://localhost:7071"
}
```

---

## Part 3: Testing Locally

After making the above edits, test the implementation:

### Step 1: Start the Application

```bash
cd Client
dotnet run
```

### Step 2: Test Login Flow

1. Navigate to `http://localhost:5000` (or the port shown in the console)
2. Click the **Login** button in the header
3. You should be redirected to Microsoft's login page
4. Sign in with your Azure AD account
5. If this is the first time, you may see a consent screen - click **Accept**
6. You should be redirected back to the app and see "Hello, [Your Name]!" in the header

### Step 3: Verify Profile Page

1. Click on your name or navigate to `/profile`
2. You should see:
   - **Authentication State** section showing you're authenticated
   - **Microsoft Graph Profile Data** with your actual name, email, etc.
   - **User Claims** showing claims from Azure AD

### Troubleshooting

If login doesn't work:

1. **Check Browser Console (F12)** for error messages
2. **Verify Redirect URIs** in Azure AD match your local URL exactly (including port number)
3. **Clear Browser Cache** and try again
4. **Check Configuration Files** - ensure no typos in Tenant ID or Client ID

---

## Part 4: Deploying to Production

### Before Deployment

Ensure you've added your production redirect URIs in Azure AD (from Part 1, Step 2):
- `https://YOUR-STATIC-WEB-APP-URL/authentication/login-callback`
- `https://YOUR-STATIC-WEB-APP-URL/authentication/logout-callback`

### Deployment Steps

1. Commit and push your changes to the repository
2. Azure Static Web Apps will automatically deploy
3. After deployment, navigate to your production URL
4. Test the login flow as described in Part 3

### Important Notes

- The configuration files (`appsettings.json`) contain **only public values** (Client ID and Tenant ID)
- These are **safe to commit** to source control
- No secrets or client secrets are needed for MSAL in browser-based apps
- The Client ID is considered public information in OAuth 2.0 / OIDC

---

## Summary Checklist

- [ ] Azure AD App Registration configured with SPA platform
- [ ] Redirect URIs added for both local and production environments
- [ ] User.Read permission verified in API permissions
- [ ] Tenant ID and Client ID copied from Azure AD
- [ ] `Client/wwwroot/appsettings.json` updated with real values
- [ ] `Client/wwwroot/appsettings.Development.json` updated with real values
- [ ] Application tested locally
- [ ] Login works and displays correct name
- [ ] Profile page shows Graph API data
- [ ] Changes committed and deployed to production
- [ ] Production login tested

---

## Quick Reference

### Values You Need from Azure AD

| Value | Where to Find | Used In |
|-------|---------------|---------|
| **Tenant ID** | App Registration → Overview → Directory (tenant) ID | Both appsettings files → `Authority` URL |
| **Client ID** | App Registration → Overview → Application (client) ID | Both appsettings files → `ClientId` field |

### Redirect URIs to Add

| Environment | Login Callback URL | Logout Callback URL |
|-------------|-------------------|---------------------|
| **Local Dev** | `http://localhost:5000/authentication/login-callback` | `http://localhost:5000/authentication/logout-callback` |
| **Production** | `https://YOUR-DOMAIN/authentication/login-callback` | `https://YOUR-DOMAIN/authentication/logout-callback` |

**Note:** If your local dev uses a different port (e.g., 5001), update the URLs accordingly.

---

## Support

If you encounter issues:
1. Check browser console for detailed error messages
2. Verify all redirect URIs match exactly (case-sensitive, must include protocol and port)
3. Ensure Tenant ID and Client ID are correct (no extra spaces or characters)
4. Refer to `GRAPH_API_SETUP.md` for detailed troubleshooting guide

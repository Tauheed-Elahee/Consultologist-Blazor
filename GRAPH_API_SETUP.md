# Microsoft Graph API Setup Guide

This guide walks you through configuring Microsoft Graph API integration for user profile retrieval using the On-Behalf-Of flow.

## Overview

The application uses Microsoft Graph API to retrieve user profile information (display name, email, job title, etc.) from Azure Active Directory. This approach uses **delegated permissions** with **On-Behalf-Of flow**, which means:

- No admin consent required for basic `User.Read` permission
- Users authenticate themselves via Azure Static Web Apps
- The Azure Function calls Graph API to get the authenticated user's profile
- Each user can only access their own profile data

## Prerequisites

- Azure subscription with access to Azure Active Directory
- Azure Static Web App already configured with Azure AD authentication
- Access to Azure Portal to configure App Registration

---

## Part 1: Azure AD App Registration Configuration

### Step 1: Find Your App Registration

1. Navigate to [Azure Portal](https://portal.azure.com)
2. Go to **Azure Active Directory**
3. Click **App registrations** in the left menu
4. Find your app registration (the one used for Static Web Apps authentication)
   - If you don't have one, you'll need to create it for Static Web Apps first

### Step 2: Add Microsoft Graph API Permissions

1. In your App Registration, click **API permissions** in the left menu
2. Click **Add a permission**
3. Select **Microsoft Graph**
4. Select **Delegated permissions**
5. Search for and select **User.Read**
   - This permission allows reading the signed-in user's profile
   - Does NOT require admin consent
6. Click **Add permissions**

### Step 3: Create a Client Secret

1. In your App Registration, click **Certificates & secrets** in the left menu
2. Click **New client secret**
3. Enter a description (e.g., "Graph API Access")
4. Select an expiration period (recommendation: 12 months for development, shorter for production)
5. Click **Add**
6. **IMPORTANT:** Copy the secret **Value** immediately - it won't be shown again
7. Store this secret securely - you'll need it for configuration

### Step 4: Note Your Configuration Values

From the **Overview** page of your App Registration, note down:

- **Application (client) ID** - You'll use this as `AzureAd_ClientId`
- **Directory (tenant) ID** - You'll use this as `AzureAd_TenantId`
- **Client Secret Value** (from Step 3) - You'll use this as `AzureAd_ClientSecret`

---

## Part 2: Local Development Configuration

### Step 1: Configure Azure Function Settings

1. Navigate to the `Api` folder in your project
2. Open `local.settings.json` (create it if it doesn't exist using the template)
3. Add your configuration values:

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "AzureAd_TenantId": "YOUR_TENANT_ID_HERE",
    "AzureAd_ClientId": "YOUR_CLIENT_ID_HERE",
    "AzureAd_ClientSecret": "YOUR_CLIENT_SECRET_HERE"
  }
}
```

4. Replace the placeholder values with your actual values from Part 1, Step 4
5. Save the file

**Note:** `local.settings.json` is already in `.gitignore` and will NOT be committed to source control.

### Step 2: Restore NuGet Packages

Run the following command from the project root:

```bash
dotnet restore
```

This will install the required packages:
- `Microsoft.Graph` - Microsoft Graph SDK
- `Azure.Identity` - Azure authentication library

---

## Part 3: Azure Deployment Configuration

When deploying to Azure, you need to configure the same settings as environment variables.

### Option A: Azure Portal Configuration

1. Navigate to your **Azure Function App** in Azure Portal
2. Click **Configuration** in the left menu (under Settings)
3. Add the following Application Settings:

| Name | Value |
|------|-------|
| `AzureAd_TenantId` | Your Tenant ID |
| `AzureAd_ClientId` | Your Client ID |
| `AzureAd_ClientSecret` | Your Client Secret |

4. Click **Save**
5. Click **Continue** to restart the Function App

### Option B: Azure CLI Configuration

```bash
# Set variables
FUNCTION_APP_NAME="your-function-app-name"
RESOURCE_GROUP="your-resource-group"
TENANT_ID="your-tenant-id"
CLIENT_ID="your-client-id"
CLIENT_SECRET="your-client-secret"

# Configure settings
az functionapp config appsettings set \
  --name $FUNCTION_APP_NAME \
  --resource-group $RESOURCE_GROUP \
  --settings \
    "AzureAd_TenantId=$TENANT_ID" \
    "AzureAd_ClientId=$CLIENT_ID" \
    "AzureAd_ClientSecret=$CLIENT_SECRET"
```

---

## Part 4: Testing the Integration

### Step 1: Start Local Development

1. Start the Azure Functions backend:
   ```bash
   cd Api
   func start
   ```

2. In a separate terminal, start the Blazor client:
   ```bash
   cd Client
   dotnet run
   ```

### Step 2: Test Authentication and Profile Retrieval

1. Navigate to your local app (usually `http://localhost:5000` or similar)
2. Click **Login** - you'll be redirected to Azure AD
3. Sign in with your Azure AD credentials
4. After successful login, you should see:
   - Header shows: "Hello, [Your Display Name]!" instead of your email
   - Profile page shows full profile information from Microsoft Graph

### Step 3: Verify Profile Data

Navigate to `/profile` and verify you see:
- **Microsoft Graph Profile Data** section with:
  - Display Name
  - Email
  - Job Title
  - Office Location
  - Phone numbers
- **Static Web Apps Authentication Data** section with SWA auth info

---

## Troubleshooting

### Issue: "Unauthorized" when calling Graph API

**Possible causes:**
1. Client Secret expired or incorrect
2. Tenant ID or Client ID incorrect
3. App Registration permissions not configured

**Solution:**
- Verify all configuration values in `local.settings.json`
- Check Azure Function logs for detailed error messages
- Ensure `User.Read` permission is added to your App Registration

### Issue: "Profile error" shown in header

**Possible causes:**
1. Graph API endpoint not accessible
2. Invalid user ID from Static Web Apps auth
3. Missing configuration values

**Solution:**
- Open browser console (F12) to see detailed error messages
- Verify Azure Function is running and accessible
- Check that user is properly authenticated via Static Web Apps

### Issue: Claims are null in Profile page

This is expected! Azure Static Web Apps with Azure AD doesn't populate the claims array. That's why we're using Graph API instead.

### Issue: Display name still shows email

**Possible causes:**
1. Graph API call is failing silently
2. User's Azure AD profile doesn't have display name set

**Solution:**
- Check browser console for errors
- Navigate to `/profile` to see if Graph API data loads
- Verify the user's profile in Azure AD has a display name

---

## Security Best Practices

### For Development

✅ Use `local.settings.json` for secrets (already in `.gitignore`)
✅ Never commit secrets to source control
✅ Use short-lived client secrets (12 months max)
✅ Rotate secrets regularly

### For Production

✅ Use **Azure Key Vault** for secret storage (recommended)
✅ Enable **Managed Identity** for Function App
✅ Use **shorter secret expiration** (3-6 months)
✅ Implement **secret rotation** process
✅ Monitor API usage and set up alerts

### Migrating to Azure Key Vault

When ready for production, migrate secrets to Key Vault:

1. Create an Azure Key Vault
2. Store Client Secret in Key Vault
3. Enable Managed Identity on Function App
4. Grant Function App access to Key Vault
5. Update configuration to reference Key Vault:
   ```
   AzureAd:ClientSecret = @Microsoft.KeyVault(SecretUri=https://your-vault.vault.azure.net/secrets/GraphApiSecret/)
   ```

---

## API Endpoints

### GET /api/GetUserProfile

Retrieves the current authenticated user's profile from Microsoft Graph.

**Authentication:** Required (Azure Static Web Apps)

**Response:**
```json
{
  "id": "user-object-id",
  "displayName": "John Doe",
  "email": "john.doe@company.com",
  "jobTitle": "Software Engineer",
  "officeLocation": "Building 5",
  "mobilePhone": "+1-555-0100",
  "businessPhones": ["+1-555-0101"]
}
```

**Error Responses:**
- `401 Unauthorized` - User not authenticated
- `404 Not Found` - User profile not found in Azure AD
- `500 Internal Server Error` - Graph API error (check logs)

---

## Architecture

```
User Browser
    ↓
Static Web App (Blazor WASM)
    ↓ (authenticated request with SWA headers)
Azure Function (/api/GetUserProfile)
    ↓ (uses Client Credentials + User ID)
Microsoft Graph API (/users/{userId})
    ↓
User Profile Data
```

### Flow:

1. User authenticates via Azure Static Web Apps → gets Azure AD token
2. Blazor app calls `/api/GetUserProfile`
3. Azure Function extracts user ID from SWA authentication headers
4. Function uses Client Credentials to authenticate to Graph API
5. Function calls Graph API to get user profile by ID
6. Profile data returned to Blazor app
7. Blazor app displays user's name

---

## Migration to App-Only Authentication (Future)

When you're ready to get IT approval and use app-only authentication:

### Changes Required:

1. **Azure AD Permission Change:**
   - Remove: `User.Read` (Delegated)
   - Add: `User.ReadBasic.All` (Application)
   - **Requires admin consent**

2. **Code Change in `GraphService.cs`:**
   - Already using `ClientSecretCredential` - no changes needed!
   - Current implementation works for both approaches

3. **Benefits:**
   - More reliable (doesn't depend on user's token)
   - Simpler token management
   - Can read multiple users if needed later

**No other code changes required** - the architecture supports both approaches.

---

## Support and Documentation

- [Microsoft Graph API Documentation](https://learn.microsoft.com/en-us/graph/overview)
- [Azure Static Web Apps Authentication](https://learn.microsoft.com/en-us/azure/static-web-apps/authentication-authorization)
- [Azure Identity Library](https://learn.microsoft.com/en-us/dotnet/api/overview/azure/identity-readme)
- [Graph API User Resource](https://learn.microsoft.com/en-us/graph/api/resources/user)

---

## Quick Reference

### Configuration Values Needed

| Setting | Description | Where to Find |
|---------|-------------|---------------|
| `AzureAd_TenantId` | Your Azure AD Tenant ID | App Registration → Overview |
| `AzureAd_ClientId` | Application (Client) ID | App Registration → Overview |
| `AzureAd_ClientSecret` | Client Secret Value | App Registration → Certificates & secrets |

### Files Modified

- `Api/Api.csproj` - Added NuGet packages
- `Api/Services/GraphService.cs` - Graph API service
- `Api/GetUserProfileFunction.cs` - API endpoint
- `Api/Program.cs` - DI registration
- `Client/Models/UserProfile.cs` - Profile model
- `Client/Services/UserProfileService.cs` - Client service
- `Client/Components/LoginDisplay.razor` - Display name in header
- `Client/Pages/Profile.razor` - Full profile page
- `Client/Models/UserInfo.cs` - Removed claims-based logic

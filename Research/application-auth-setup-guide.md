# Application Authentication Setup Guide
## Complete Configuration Walkthrough for Client Credentials Flow

---

## Table of Contents

1. [Overview](#overview)
2. [Prerequisites](#prerequisites)
3. [Part 1: Azure AD App Registration](#part-1-azure-ad-app-registration)
4. [Part 2: API Permissions](#part-2-api-permissions)
5. [Part 3: Admin Consent](#part-3-admin-consent)
6. [Part 4: Client Secret](#part-4-client-secret)
7. [Part 5: Local Configuration](#part-5-local-configuration)
8. [Part 6: Local Testing](#part-6-local-testing)
9. [Part 7: Azure Deployment](#part-7-azure-deployment)
10. [Part 8: Production Testing](#part-8-production-testing)
11. [Part 9: Troubleshooting](#part-9-troubleshooting)
12. [Part 10: Security Best Practices](#part-10-security-best-practices)
13. [Appendix](#appendix)

---

## Overview

### What This Guide Covers

This guide walks you through the complete configuration process for **Application Authentication** using the **Client Credentials Flow** in your Blazor WebAssembly + Azure Functions application.

### What You'll Accomplish

By the end of this guide, you will have:
- ✅ Configured Azure AD App Registration with proper permissions
- ✅ Obtained admin consent for Microsoft Graph API access
- ✅ Secured your client credentials properly
- ✅ Configured local development environment
- ✅ Deployed and tested in Azure
- ✅ Verified the authentication flow works end-to-end

### Architecture Overview

```
User Browser (Blazor WASM)
    ↓ Authenticates via Azure Static Web Apps
Azure Static Web Apps
    ↓ Passes user ID in x-ms-client-principal header
Azure Function (/api/GetUserProfile)
    ↓ Authenticates with Client ID + Secret
Azure AD Token Endpoint
    ↓ Returns app-level access token
Azure Function
    ↓ Calls Graph API with token
Microsoft Graph API
    ↓ Returns user profile data
User sees their profile information
```

### Time Estimate

- **If you have admin rights**: 15-20 minutes
- **If you need to request admin consent**: 15 minutes + approval wait time

---

## Prerequisites

### Required Access & Permissions

- [ ] **Azure Subscription** with an active tenant
- [ ] **Azure AD Permissions** - One of the following roles:
  - Global Administrator (can do everything)
  - Cloud Application Administrator (can grant consent)
  - Application Administrator (can grant consent)
  - Application Developer (can create app, but NOT grant admin consent)
  
- [ ] **Development Tools**:
  - .NET 8 SDK installed
  - Azure Functions Core Tools v4
  - Visual Studio Code or Visual Studio 2022
  - Git (for version control)

### Required Knowledge

- Basic understanding of Azure Portal
- Basic understanding of OAuth 2.0 concepts
- Familiarity with environment variables and configuration

### If You DON'T Have Admin Rights

If you cannot grant admin consent yourself:
1. Follow steps to create the app registration
2. Document the Application ID
3. Request admin consent from your IT department
4. Provide them with:
   - Application (Client) ID
   - Permission needed: `User.ReadBasic.All` (Application permission)
   - Justification: "To retrieve user profile information for application display"

---

## Part 1: Azure AD App Registration

### Option A: Use Existing App Registration

If your Static Web Apps authentication already uses an Azure AD App Registration, you can reuse it.

**To verify:**
1. Go to [Azure Portal](https://portal.azure.com)
2. Navigate to **Azure Active Directory** → **App registrations**
3. Look for an app registration with a name matching your application
4. Check if it has redirect URIs configured for your Static Web App

**If found:** Note down the **Application (client) ID** and proceed to [Part 2](#part-2-api-permissions).

**If not found or unsure:** Proceed with Option B to create a new one.

---

### Option B: Create New App Registration

#### Step 1.1: Navigate to App Registrations

1. Sign in to [Azure Portal](https://portal.azure.com)
2. Click on **Azure Active Directory** (or search for it in the top search bar)
3. In the left menu, click **App registrations**
4. Click **+ New registration** at the top

#### Step 1.2: Configure Basic Information

Fill in the registration form:

| Field | Value | Example |
|-------|-------|---------|
| **Name** | Your application name | `Consultologist-Blazor` or `Consultologist-App` |
| **Supported account types** | **Accounts in this organizational directory only** (Single tenant) | Select this option |
| **Redirect URI** | Platform: **Web**<br>URI: Your Static Web App callback | `https://your-app.azurestaticapps.net/.auth/login/aad/callback` |

**Important Notes:**
- For **local testing**, you may also add: `http://localhost:4280/.auth/login/aad/callback`
- You can add more redirect URIs later
- The redirect URI is for user authentication via Static Web Apps, not for the Azure Function

#### Step 1.3: Create the Registration

1. Click **Register** button at the bottom
2. Wait for the registration to complete (takes a few seconds)
3. You'll be redirected to the app's **Overview** page

#### Step 1.4: Record Important Information

From the **Overview** page, copy and save these values:

| Field | Where to Find | Where You'll Use It |
|-------|---------------|---------------------|
| **Application (client) ID** | Overview page, under "Essentials" | `AzureAd_ClientId` in local.settings.json |
| **Directory (tenant) ID** | Overview page, under "Essentials" | `AzureAd_TenantId` in local.settings.json |
| **Object ID** | Overview page, under "Essentials" | For reference only |

**Example:**
```
Application (client) ID: 12345678-1234-1234-1234-123456789abc
Directory (tenant) ID:   87654321-4321-4321-4321-cba987654321
```

💡 **Tip:** Create a temporary text file to store these values while you work through the setup.

---

## Part 2: API Permissions

### Why Permissions Are Needed

Your Azure Function needs permission to read user profiles from Microsoft Graph API on behalf of the application (not individual users).

### Step 2.1: Navigate to API Permissions

1. In your App Registration, click **API permissions** in the left menu
2. You'll see a list of current permissions (by default, only `User.Read` delegated permission exists)

### Step 2.2: Add Microsoft Graph Permission

1. Click **+ Add a permission** button
2. In the "Request API permissions" panel, click **Microsoft Graph**
3. Click **Application permissions** (NOT "Delegated permissions")
   - **Important:** Application permissions are different from delegated permissions
   - Application permissions let the app act independently
   - Delegated permissions require a user to be signed in

### Step 2.3: Select User Read Permission

1. In the search box, type: `User.Read`
2. Expand the **User** category
3. You'll see two options:
   - ✅ **User.ReadBasic.All** - Read all users' basic profiles (RECOMMENDED)
   - **User.Read.All** - Read all users' full profiles (more access than needed)

**Which one to choose?**

| Permission | What It Includes | Recommendation |
|------------|------------------|----------------|
| `User.ReadBasic.All` | displayName, id, userPrincipalName, mail, jobTitle | ✅ **Use this** - sufficient for most apps |
| `User.Read.All` | All basic fields + mobilePhone, businessPhones, officeLocation, and more | Use only if you need extra fields |

4. Check the box next to **User.ReadBasic.All**
5. Click **Add permissions** button at the bottom

### Step 2.4: Verify Permission Was Added

You should now see in the API permissions list:

| API / Permission name | Type | Admin consent required | Status |
|----------------------|------|------------------------|--------|
| Microsoft Graph / User.ReadBasic.All | Application | Yes | ⚠️ Not granted |

The status will show **"Not granted for [Your Tenant]"** with a warning icon - this is expected and will be fixed in the next section.

---

## Part 3: Admin Consent

### Understanding Admin Consent

**Why is it required?**
- Application permissions grant broad access (can read ANY user's profile)
- This is a security protection mechanism
- Only administrators can approve such permissions

**What happens when you grant consent?**
- The application receives the permission to access the Graph API
- Your Azure Function can now retrieve user profile data
- All users in your organization can use the app (no individual consent needed)

---

### Method 1: Azure Portal (Recommended - Easiest)

#### Step 3.1: Grant Consent

1. Still in the **API permissions** page of your App Registration
2. Look for the button: **"Grant admin consent for [Your Organization Name]"**
3. Click the button
4. A confirmation dialog will appear showing:
   - The app name
   - The permissions being granted
   - A warning that this affects all users
5. Click **Yes** to confirm

#### Step 3.2: Verify Consent Was Granted

After granting consent, the permissions list should update:

| API / Permission name | Type | Status |
|----------------------|------|--------|
| Microsoft Graph / User.ReadBasic.All | Application | ✅ **Granted for [Your Tenant]** |

You should see:
- ✅ Green checkmark in the Status column
- "Granted for [Your Tenant]" text
- The warning icon should be gone

**If the button is greyed out or missing:**
- You don't have sufficient permissions
- You need to request admin consent from someone with Global Administrator, Cloud Application Administrator, or Application Administrator role
- Proceed to "Method 4: Request Admin Consent" below

---

### Method 2: PowerShell (For Automation)

If you prefer scripting or need to grant consent via PowerShell:

#### Prerequisites
```powershell
# Install Azure AD module if not already installed
Install-Module AzureAD -Scope CurrentUser
```

#### Script to Grant Consent

```powershell
# Connect to Azure AD
Connect-AzureAD

# Replace these values
$clientId = "YOUR_APPLICATION_CLIENT_ID_HERE"  # From Part 1

# Get the service principal for your app
$sp = Get-AzureADServicePrincipal -Filter "appId eq '$clientId'"

# If the service principal doesn't exist, create it
if (-not $sp) {
    $sp = New-AzureADServicePrincipal -AppId $clientId
}

# Get Microsoft Graph service principal
$graphSp = Get-AzureADServicePrincipal -Filter "appId eq '00000003-0000-0000-c000-000000000000'"

# Get the User.ReadBasic.All permission
$permission = $graphSp.AppRoles | Where-Object {$_.Value -eq "User.ReadBasic.All"}

# Grant the permission
New-AzureADServiceAppRoleAssignment `
    -ObjectId $sp.ObjectId `
    -PrincipalId $sp.ObjectId `
    -ResourceId $graphSp.ObjectId `
    -Id $permission.Id

Write-Host "✅ Admin consent granted successfully!" -ForegroundColor Green
```

#### Verify

```powershell
# List all permissions granted to your app
Get-AzureADServiceAppRoleAssignment -ObjectId $sp.ObjectId | 
    Select-Object PrincipalDisplayName, ResourceDisplayName, Id

# You should see Microsoft Graph in the output
```

---

### Method 3: Azure CLI (For Automation)

If you prefer Azure CLI:

#### Prerequisites
```bash
# Install Azure CLI if not already installed
# Visit: https://docs.microsoft.com/en-us/cli/azure/install-azure-cli

# Login
az login
```

#### Commands to Grant Consent

```bash
# Replace with your values
CLIENT_ID="YOUR_APPLICATION_CLIENT_ID_HERE"
TENANT_ID="YOUR_TENANT_ID_HERE"

# Create service principal if it doesn't exist
az ad sp create --id $CLIENT_ID

# Grant admin consent
az ad app permission admin-consent --id $CLIENT_ID

# Verify the permission was granted
az ad app permission list --id $CLIENT_ID --output table
```

---

### Method 4: Request Admin Consent (If You Don't Have Admin Rights)

If you cannot grant consent yourself, you need to request it from your IT administrators.

#### Step 4.1: Generate Admin Consent URL

Create a special URL that administrators can click to grant consent:

```
https://login.microsoftonline.com/{TENANT_ID}/adminconsent?client_id={CLIENT_ID}
```

**Replace:**
- `{TENANT_ID}` - Your Directory (tenant) ID from Part 1
- `{CLIENT_ID}` - Your Application (client) ID from Part 1

**Example:**
```
https://login.microsoftonline.com/87654321-4321-4321-4321-cba987654321/adminconsent?client_id=12345678-1234-1234-1234-123456789abc
```

#### Step 4.2: Email Template for IT Department

```
Subject: Admin Consent Request for [App Name] Application

Hi [IT Admin Name],

I'm developing an application called [App Name] that requires access to 
Microsoft Graph API to display user profile information.

Please grant admin consent for the following:

Application Details:
- Application Name: [Your App Name]
- Application (Client) ID: [Your Client ID]
- Tenant ID: [Your Tenant ID]

Permission Required:
- API: Microsoft Graph
- Permission: User.ReadBasic.All (Application permission)
- Reason: To retrieve and display user profile information (name, email, job title)

Admin Consent URL:
[Paste the URL from Step 4.1]

Alternatively, you can grant consent via Azure Portal:
1. Go to Azure Portal → Azure Active Directory → App registrations
2. Find the app: [Your App Name]
3. Click "API permissions"
4. Click "Grant admin consent for [Org Name]"

This permission allows the application to read basic user profile information 
from Azure AD for all users in our organization. It does not grant access to 
emails, files, or other personal data.

Security Note:
- The permission is "Application" type, not "Delegated"
- Admin consent is required by design for this permission type
- The app will only use this to display user names and profile info

Please let me know if you need any additional information.

Thank you!
```

#### Step 4.3: After Admin Grants Consent

Once the admin clicks the URL or grants consent via Portal:
1. They'll see a consent screen showing the app name and requested permissions
2. After they click "Accept", the permission will be granted
3. You can verify by checking the API permissions page (should show green checkmark)
4. Proceed to Part 4

---

## Part 4: Client Secret

### What Is a Client Secret?

A client secret is like a password for your application. Your Azure Function uses it to prove its identity when requesting access tokens from Azure AD.

**Security Note:** Client secrets are sensitive! Treat them like passwords.

---

### Step 4.1: Navigate to Certificates & Secrets

1. In your App Registration, click **Certificates & secrets** in the left menu
2. Click the **Client secrets** tab (should be selected by default)
3. You'll see a list of current secrets (probably empty)

### Step 4.2: Create New Client Secret

1. Click **+ New client secret** button
2. Fill in the form:

| Field | Value | Recommendation |
|-------|-------|----------------|
| **Description** | Descriptive name | `Consultologist-API-Secret` or `Production-Secret-2024` |
| **Expires** | Expiration period | **180 days** (6 months) or **Custom** for specific date |

**Important Notes on Expiration:**
- ⚠️ When the secret expires, your app will STOP WORKING
- Set a calendar reminder to renew before expiration
- For production, use **180 days** or **1 year** and establish a renewal process
- Azure will send email notifications before expiration (if configured)

3. Click **Add** button

### Step 4.3: Copy the Secret Value IMMEDIATELY

After creating the secret, you'll see:

| Description | Secret ID | Value | Expires |
|-------------|-----------|-------|---------|
| Consultologist-API-Secret | abc123... | ••••••••••••••• (Click to show) | 6 months from now |

**⚠️ CRITICAL STEP:**

1. Click the **Copy** icon next to the **Value** field (NOT the Secret ID)
2. The value will look like: `abc~123XYZ...` (contains letters, numbers, ~, and other characters)
3. **Paste it into your temporary text file IMMEDIATELY**
4. You can NEVER see this value again after leaving this page
5. If you lose it, you'll have to create a new secret

**Example Secret Value:**
```
abc~xYz.123-456_789.qRs~tuV
```

### Step 4.4: Record Secret Information

Save these details:

| Field | Value | Where You'll Use It |
|-------|-------|---------------------|
| **Secret Value** | The long string you just copied | `AzureAd_ClientSecret` in local.settings.json |
| **Secret ID** | The shorter GUID | For reference/rotation tracking |
| **Expiration Date** | Date when it expires | Set calendar reminder |

---

### Step 4.5: Set Up Expiration Reminder

**Recommended:** Set a calendar reminder for **2 weeks before expiration** to renew the secret.

**When it's time to renew:**
1. Create a new secret (repeat Step 4.2)
2. Update your configuration with the new secret
3. Deploy the updated configuration
4. Verify the app works with the new secret
5. Delete the old secret

---

## Part 5: Local Configuration

Now you'll configure your local development environment to use the Azure AD credentials.

### Step 5.1: Locate Configuration File

Navigate to your project directory:
```bash
cd /home/thegreat/Projects/GitHub/Consultologist-Blazor/Api
```

You should see:
- `local.settings.json` - Your actual config file (gitignored)
- `local.settings.example.json` - Template file

### Step 5.2: Open local.settings.json

Open the file in your editor. It should look like this:

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

### Step 5.3: Update Configuration Values

Replace the placeholder values with your actual credentials from previous steps:

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "AzureAd_TenantId": "87654321-4321-4321-4321-cba987654321",
    "AzureAd_ClientId": "12345678-1234-1234-1234-123456789abc",
    "AzureAd_ClientSecret": "abc~xYz.123-456_789.qRs~tuV"
  }
}
```

**Mapping:**

| Configuration Key | Value Source | Example |
|------------------|--------------|---------|
| `AzureAd_TenantId` | Directory (tenant) ID from Part 1 | `87654321-4321-4321-4321-cba987654321` |
| `AzureAd_ClientId` | Application (client) ID from Part 1 | `12345678-1234-1234-1234-123456789abc` |
| `AzureAd_ClientSecret` | Secret Value from Part 4 | `abc~xYz.123-456_789.qRs~tuV` |

### Step 5.4: Verify JSON Syntax

**Common mistakes to avoid:**
- ❌ Don't add comma after the last item in the Values object
- ❌ Don't use single quotes (must be double quotes)
- ❌ Don't add comments (JSON doesn't support them)
- ✅ Ensure all quotes are properly closed
- ✅ Ensure all brackets are matched

**Validate your JSON:**
You can use a JSON validator online or in VS Code:
- VS Code will show red squiggly lines if there are syntax errors
- Save the file and check for error indicators

### Step 5.5: Verify File Is Gitignored

**Security check:**

```bash
# Check if local.settings.json is in .gitignore
cat .gitignore | grep local.settings.json
```

You should see `local.settings.json` in the output. If not:

```bash
# Add it to .gitignore
echo "local.settings.json" >> .gitignore
```

**⚠️ NEVER commit local.settings.json to source control!**

### Step 5.6: Save Configuration Summary

Create a secure note (password manager, secure document) with:

```
Azure AD Application: Consultologist-Blazor
Created: [Date]
Tenant ID: 87654321-4321-4321-4321-cba987654321
Client ID: 12345678-1234-1234-1234-123456789abc
Secret Created: [Date]
Secret Expires: [Date]
Secret Rotation Reminder: [Date - 2 weeks before expiration]
```

---

## Part 6: Local Testing

Now test that everything works locally before deploying to Azure.

### Step 6.1: Start Azure Functions

Open a terminal in the `Api` directory:

```bash
cd /home/thegreat/Projects/GitHub/Consultologist-Blazor/Api

# Start the Azure Functions runtime
func start
```

**Expected output:**
```
Azure Functions Core Tools
Core Tools Version:       4.x.x
Function Runtime Version: 4.x.x

Functions:
  GetUserProfile: [GET] http://localhost:7071/api/GetUserProfile
  GetRolesForUser: [GET] http://localhost:7071/api/GetRolesForUser

For detailed output, run func with --verbose flag.
```

**If you see errors:**
- Check that `local.settings.json` has correct syntax
- Verify all three Azure AD values are filled in
- Check that .NET 8 SDK is installed: `dotnet --version`

### Step 6.2: Start Blazor Client

Open a **new terminal** in the `Client` directory:

```bash
cd /home/thegreat/Projects/GitHub/Consultologist-Blazor/Client

# Start the Blazor development server
dotnet run
```

**Expected output:**
```
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://localhost:5000
info: Microsoft.Hosting.Lifetime[0]
      Application started. Press Ctrl+C to shut down.
```

### Step 6.3: Open Application in Browser

1. Open your browser to: `http://localhost:5000`
2. You should see your Blazor application

### Step 6.4: Test Authentication Flow

**Step 1: Check Initial State**
- The app should show a "Login" button or link
- You should NOT be logged in yet

**Step 2: Authenticate with Azure Static Web Apps**

⚠️ **Important Note:** 
- Local development doesn't use Azure Static Web Apps authentication
- The `/.auth/login/aad` route only works when deployed to Azure
- For local testing, you'll need to simulate the authentication header

**For local testing, you have two options:**

#### Option A: Test API Endpoint Directly (Quick Verification)

Test that the Graph Service is working correctly:

```bash
# In a new terminal, test the function directly
# You'll need a valid Azure AD user ID

curl -X GET "http://localhost:7071/api/GetUserProfile" \
  -H "Content-Type: application/json" \
  -H "x-ms-client-principal: eyJ1c2VySWQiOiJZT1VSX1VTRVJfSURfSEVSRSIsInVzZXJEZXRhaWxzIjoidGVzdEB0ZXN0LmNvbSIsImlkZW50aXR5UHJvdmlkZXIiOiJhYWQifQ=="
```

**To get a valid user ID:**
```bash
# Using Azure CLI
az ad user show --id "your.email@domain.com" --query objectId -o tsv

# Or using PowerShell
(Get-AzureADUser -ObjectId "your.email@domain.com").ObjectId
```

Then create the header:
```bash
# The header value is base64 encoded JSON
# JSON format: {"userId":"YOUR_USER_ID","userDetails":"email@domain.com","identityProvider":"aad"}

# Example:
echo '{"userId":"12345678-abcd-1234-abcd-123456789abc","userDetails":"test@test.com","identityProvider":"aad"}' | base64
```

#### Option B: Deploy to Azure for Full Testing (Recommended)

Since local development of Static Web Apps authentication is complex, the easiest approach is to deploy to Azure for full end-to-end testing.

**For now, verify the Function API works:**

### Step 6.5: Verify GraphService Configuration

Check the Azure Functions logs for successful startup:

**Look for these log entries:**
```
[Information] GraphService: Successfully initialized with tenantId: 87654321-4321-...
[Information] GetUserProfile function registered successfully
```

**If you see errors:**
```
[Error] GraphService: Missing configuration - AzureAd_TenantId
```

This means:
- `local.settings.json` is not configured correctly
- The Values are not being read
- Check for JSON syntax errors

### Step 6.6: Test Graph API Call (If You Used Option A)

If you tested the API directly with a valid user ID header:

**Success response (HTTP 200):**
```json
{
  "displayName": "John Doe",
  "email": "john.doe@company.com",
  "jobTitle": "Software Engineer",
  "officeLocation": "Building 1",
  "mobilePhone": null,
  "businessPhones": []
}
```

**Error responses:**

| Status Code | Error Message | Cause | Solution |
|-------------|---------------|-------|----------|
| 401 | `Authorization_RequestDenied` | Admin consent not granted | Go back to Part 3 and grant consent |
| 401 | `invalid_client` | Wrong Client ID or Secret | Check `local.settings.json` values |
| 404 | `User not found` | Invalid user ID | Check the user ID is correct |
| 500 | `Missing configuration` | Config not loaded | Check `local.settings.json` syntax |

### Step 6.7: Stop Local Servers

Once you've verified the configuration:

```bash
# In each terminal, press:
Ctrl + C
```

---

## Part 7: Azure Deployment

Now deploy your application to Azure and configure production settings.

### Step 7.1: Verify Azure Resources Exist

You should already have:
- ✅ Azure Static Web App (for Blazor client)
- ✅ Azure Function App (for API)

**To check:**
```bash
# List your Azure resources
az resource list --resource-type "Microsoft.Web/staticSites" -o table
az resource list --resource-type "Microsoft.Web/sites" --query "[?kind=='functionapp']" -o table
```

**If you don't have these resources, create them first:**
- Follow Azure Static Web Apps creation guide
- Ensure the Static Web App is linked to your GitHub repository
- Ensure the Function App is created with .NET 8 runtime

### Step 7.2: Configure Static Web Apps Authentication

#### Navigate to Static Web App

1. Go to [Azure Portal](https://portal.azure.com)
2. Search for your Static Web App
3. Click on it to open

#### Configure Authentication Provider

1. In the left menu, click **Authentication**
2. You should see authentication providers listed
3. If Azure AD is not configured, click **+ Add**
4. Select **Azure Active Directory**
5. Fill in:
   - **App registration type**: Existing
   - **Application (client) ID**: Your Client ID from Part 1
   - **Client secret**: Your Client Secret from Part 4 (same one)
   - **OpenID issuer endpoint**: `https://login.microsoftonline.com/{TENANT_ID}/v2.0`
     - Replace `{TENANT_ID}` with your Tenant ID

6. Click **OK** to save

**Verification:**
- You should see Azure Active Directory in the providers list
- Status should show as enabled

### Step 7.3: Configure Function App Settings

Your Azure Function needs the same configuration values you set locally.

#### Option A: Azure Portal (Manual)

1. Go to [Azure Portal](https://portal.azure.com)
2. Find your **Function App** (not Static Web App)
3. In the left menu, click **Configuration**
4. Under **Application settings** tab, click **+ New application setting**

Add these three settings one by one:

| Name | Value |
|------|-------|
| `AzureAd_TenantId` | Your Tenant ID from Part 1 |
| `AzureAd_ClientId` | Your Client ID from Part 1 |
| `AzureAd_ClientSecret` | Your Secret Value from Part 4 |

**For each setting:**
1. Click **+ New application setting**
2. Enter the Name
3. Enter the Value
4. Click **OK**

After adding all three:
1. Click **Save** at the top
2. Click **Continue** to confirm restart
3. Wait for the Function App to restart (takes ~30 seconds)

#### Option B: Azure CLI (Automated)

```bash
# Set variables
FUNCTION_APP_NAME="your-function-app-name"
RESOURCE_GROUP="your-resource-group-name"
TENANT_ID="87654321-4321-4321-4321-cba987654321"
CLIENT_ID="12345678-1234-1234-1234-123456789abc"
CLIENT_SECRET="abc~xYz.123-456_789.qRs~tuV"

# Add application settings
az functionapp config appsettings set \
  --name $FUNCTION_APP_NAME \
  --resource-group $RESOURCE_GROUP \
  --settings \
    "AzureAd_TenantId=$TENANT_ID" \
    "AzureAd_ClientId=$CLIENT_ID" \
    "AzureAd_ClientSecret=$CLIENT_SECRET"

echo "✅ Function App configured successfully!"
```

#### Verify Settings Were Applied

**Azure Portal:**
1. Refresh the Configuration page
2. You should see all three settings listed
3. Values should show as ••••••• (hidden for security)

**Azure CLI:**
```bash
az functionapp config appsettings list \
  --name $FUNCTION_APP_NAME \
  --resource-group $RESOURCE_GROUP \
  --query "[?name=='AzureAd_TenantId' || name=='AzureAd_ClientId' || name=='AzureAd_ClientSecret']" \
  --output table
```

### Step 7.4: Deploy Code to Azure

#### Deploy Blazor Client (Static Web App)

If using GitHub Actions (recommended):
1. Commit your code changes
2. Push to your repository
3. GitHub Actions will automatically build and deploy
4. Check the Actions tab in GitHub for deployment status

**Manual deployment:**
```bash
cd /home/thegreat/Projects/GitHub/Consultologist-Blazor

# Install Azure Static Web Apps CLI
npm install -g @azure/static-web-apps-cli

# Deploy
swa deploy --app-location "Client" \
  --api-location "Api" \
  --output-location "wwwroot" \
  --deployment-token "YOUR_DEPLOYMENT_TOKEN"
```

#### Deploy Azure Function

**Using Azure Functions Core Tools:**
```bash
cd /home/thegreat/Projects/GitHub/Consultologist-Blazor/Api

# Deploy to Azure
func azure functionapp publish YOUR_FUNCTION_APP_NAME
```

**Using VS Code:**
1. Install "Azure Functions" extension
2. Click Azure icon in sidebar
3. Right-click your Function App
4. Select "Deploy to Function App"

#### Verify Deployment

**Static Web App:**
1. Go to Azure Portal → Your Static Web App
2. Click **Overview**
3. Note the **URL** (e.g., `https://your-app.azurestaticapps.net`)
4. Click the URL to open your app
5. You should see the Blazor app load

**Function App:**
1. Go to Azure Portal → Your Function App
2. Click **Functions** in left menu
3. You should see `GetUserProfile` and `GetRolesForUser` listed

### Step 7.5: Verify App Registration Redirect URIs

Ensure your App Registration has the correct redirect URI for your deployed app:

1. Go to Azure Portal → Azure Active Directory → App registrations
2. Click your app
3. Click **Authentication** in left menu
4. Under **Platform configurations** → **Web**, verify redirect URI:
   - `https://your-app.azurestaticapps.net/.auth/login/aad/callback`
5. If missing, click **Add URI** and add it
6. Click **Save**

---

## Part 8: Production Testing

Now test your deployed application end-to-end.

### Step 8.1: Open Your Application

1. Navigate to your Static Web App URL: `https://your-app.azurestaticapps.net`
2. The Blazor application should load

### Step 8.2: Test Login Flow

1. Click the **Login** button/link in your app
2. You should be redirected to: `https://your-app.azurestaticapps.net/.auth/login/aad`
3. Azure AD login page should appear
4. Enter your organizational credentials (email and password)
5. If prompted for consent, click **Accept** (only first time)
6. You should be redirected back to your app
7. The UI should update to show you're logged in

### Step 8.3: Test User Profile Display

**In the navigation bar / header:**
- You should see your **display name** instead of "Login"
- Example: "Welcome, John Doe"

**If there's a Profile page:**
1. Navigate to `/profile` or click the profile link
2. You should see:
   - Display Name
   - Email address
   - Job Title
   - Office Location
   - Phone numbers (if configured in Azure AD)

### Step 8.4: Check Browser Developer Tools

Open browser DevTools (F12):

#### Network Tab
1. Filter by "api"
2. Look for call to `/api/GetUserProfile`
3. Click on it
4. Check **Headers** tab:
   - Status should be **200 OK**
   - Request should have `x-ms-client-principal` header (added by Static Web Apps)
5. Check **Response** tab:
   - Should show JSON with your profile data

#### Console Tab
- No errors should appear related to authentication
- If you see errors, note them for troubleshooting

### Step 8.5: Test Logout Flow

1. Click the **Logout** link
2. You should be redirected to: `https://your-app.azurestaticapps.net/.auth/logout`
3. You should be logged out
4. The UI should revert to showing "Login" button
5. The profile data should be cleared

### Step 8.6: Test from Different Browsers/Devices

Test in:
- ✅ Chrome/Edge (incognito mode)
- ✅ Firefox (private window)
- ✅ Safari (if on Mac)
- ✅ Mobile browser

This ensures authentication works across different environments.

### Step 8.7: Test with Different Users

If possible, test with:
- Different users in your organization
- User with different roles
- Guest user (if applicable)

Verify:
- Each user sees their own profile data
- Not someone else's data

---

## Part 9: Troubleshooting

### Common Errors and Solutions

#### Error 1: "Authorization_RequestDenied"

**Full error:**
```
Authorization_RequestDenied
Insufficient privileges to complete the operation
```

**Cause:** Admin consent not granted for the Graph API permission.

**Solution:**
1. Go back to [Part 3: Admin Consent](#part-3-admin-consent)
2. Verify the permission `User.ReadBasic.All` shows green checkmark in Portal
3. If not granted, grant consent again
4. Wait a few minutes for propagation
5. Retry your request

**Verification command:**
```bash
# Check if permission is granted
az ad app permission list --id YOUR_CLIENT_ID
```

---

#### Error 2: "invalid_client"

**Full error:**
```json
{
  "error": "invalid_client",
  "error_description": "AADSTS7000215: Invalid client secret provided..."
}
```

**Cause:** Client Secret is incorrect or expired.

**Solution:**
1. Verify `AzureAd_ClientSecret` value in configuration
   - Local: Check `Api/local.settings.json`
   - Azure: Check Function App → Configuration
2. Ensure you copied the **Value**, not the **Secret ID**
3. Check if the secret has expired:
   - Portal → App Registration → Certificates & secrets
   - Look at expiration date
4. If expired or wrong, create a new secret (Part 4)
5. Update configuration with new secret
6. Restart Function App

---

#### Error 3: "AADSTS700016: Application not found"

**Full error:**
```
AADSTS700016: Application with identifier 'xxx' was not found in the directory
```

**Cause:** Client ID is incorrect or app registration doesn't exist.

**Solution:**
1. Verify `AzureAd_ClientId` matches the Application (client) ID from Portal
2. Check you're in the correct Azure AD tenant
3. Verify the App Registration wasn't deleted
4. Copy the Client ID again from Portal → App Registration → Overview

---

#### Error 4: "User not found" or 404 from Graph API

**Full error:**
```json
{
  "error": {
    "code": "Request_ResourceNotFound",
    "message": "Resource 'xxx' does not exist..."
  }
}
```

**Cause:** The user ID from Static Web Apps doesn't match a user in Azure AD.

**Solution:**
1. Check the `x-ms-client-principal` header contains correct user ID
2. Verify the user exists in Azure AD:
   ```bash
   az ad user show --id "USER_ID"
   ```
3. Ensure Static Web Apps authentication is configured for Azure AD (not other providers)
4. Check `AuthenticationHelper.cs` is correctly extracting the userId

---

#### Error 5: "Missing configuration" in Function logs

**Full error:**
```
Missing required configuration: AzureAd_TenantId
```

**Cause:** Environment variables not configured.

**Solution:**

**Local:**
1. Check `Api/local.settings.json` exists
2. Verify JSON syntax is valid
3. Ensure the file contains all three values
4. Restart the Function (Ctrl+C and `func start` again)

**Azure:**
1. Portal → Function App → Configuration
2. Verify all three settings exist:
   - `AzureAd_TenantId`
   - `AzureAd_ClientId`
   - `AzureAd_ClientSecret`
3. Click **Save** and restart Function App

---

#### Error 6: Login redirects to wrong URL

**Problem:** After login, redirected to wrong URL or shows error.

**Cause:** Redirect URI not configured correctly.

**Solution:**
1. Portal → App Registration → Authentication
2. Verify redirect URI exactly matches:
   ```
   https://YOUR-APP.azurestaticapps.net/.auth/login/aad/callback
   ```
3. No trailing slash
4. Must be HTTPS (not HTTP)
5. Must include `.auth/login/aad/callback` path

---

#### Error 7: CORS errors in browser

**Problem:** Browser console shows CORS errors.

**Cause:** API calls blocked by CORS policy.

**Solution:**

**If using separate Function App (not linked to Static Web App):**
1. Portal → Function App → CORS
2. Add your Static Web App URL: `https://your-app.azurestaticapps.net`
3. Click **Save**

**If Function is linked to Static Web App:**
- CORS should be handled automatically
- Verify the Function is properly linked in Static Web App settings

---

#### Error 8: "401 Unauthorized" on API calls

**Problem:** API returns 401 even though user is logged in.

**Cause:** Static Web Apps not passing authentication to Function.

**Solution:**
1. Verify `staticwebapp.config.json` has route protection:
   ```json
   {
     "routes": [
       {
         "route": "/api/*",
         "allowedRoles": ["authenticated"]
       }
     ]
   }
   ```
2. Ensure Function App is linked to Static Web App (managed functions)
3. Check Function has `[Function("GetUserProfile")]` attribute
4. Verify HTTP trigger allows anonymous: `AuthorizationLevel.Anonymous`

---

### Diagnostic Commands

#### Check Azure AD Configuration

```bash
# List app permissions
az ad app permission list --id YOUR_CLIENT_ID --output table

# Show app details
az ad app show --id YOUR_CLIENT_ID --query "{name:displayName, id:appId, secret:passwordCredentials}"

# List service principal permissions (shows granted consent)
az ad sp show --id YOUR_CLIENT_ID --query "appRoles"
```

#### Check Function App Configuration

```bash
# List all app settings
az functionapp config appsettings list \
  --name YOUR_FUNCTION_APP_NAME \
  --resource-group YOUR_RESOURCE_GROUP \
  --output table

# Check specific setting
az functionapp config appsettings list \
  --name YOUR_FUNCTION_APP_NAME \
  --resource-group YOUR_RESOURCE_GROUP \
  --query "[?name=='AzureAd_ClientId'].value" \
  --output tsv
```

#### Test Graph API Directly

```bash
# Get access token using your app credentials
curl -X POST "https://login.microsoftonline.com/{TENANT_ID}/oauth2/v2.0/token" \
  -d "client_id={CLIENT_ID}" \
  -d "client_secret={CLIENT_SECRET}" \
  -d "scope=https://graph.microsoft.com/.default" \
  -d "grant_type=client_credentials"

# Use the access_token from the response to call Graph API
curl -H "Authorization: Bearer {ACCESS_TOKEN}" \
  "https://graph.microsoft.com/v1.0/users/{USER_ID}"
```

---

### Getting Help

If you're still stuck after trying troubleshooting steps:

1. **Check Azure Function logs:**
   - Portal → Function App → Log stream
   - Look for detailed error messages

2. **Enable Application Insights:**
   - Portal → Function App → Application Insights
   - View detailed telemetry and errors

3. **Review documentation:**
   - `GRAPH_API_SETUP.md` - Original setup guide
   - `Research/application-auth.md` - Architecture details
   - Microsoft Docs for specific error codes

4. **Check GitHub Issues:**
   - Search for similar issues in the repository
   - Create a new issue with error details

---

## Part 10: Security Best Practices

### 10.1: Protect Client Secrets

#### Never Commit Secrets to Source Control

**✅ Do:**
- Keep secrets in `local.settings.json` (gitignored)
- Use Azure Key Vault for production secrets
- Use environment variables
- Use managed identities when possible

**❌ Don't:**
- Commit `local.settings.json`
- Hardcode secrets in source code
- Include secrets in configuration files committed to Git
- Share secrets in chat messages or emails

#### Verify .gitignore

```bash
# Check what's ignored
cat .gitignore | grep -E "(local.settings|secrets|credentials)"

# Ensure local.settings.json is ignored
git check-ignore Api/local.settings.json
# Should output: Api/local.settings.json

# Check for accidentally committed secrets
git log --all --full-history -- "*local.settings.json"
# Should be empty
```

#### If You Accidentally Committed a Secret

1. **Immediately revoke the secret:**
   - Portal → App Registration → Certificates & secrets
   - Delete the compromised secret
   - Create a new one

2. **Remove from Git history:**
   ```bash
   # Use git-filter-repo or BFG Repo-Cleaner
   # This is complex - consider creating a new repository if needed
   ```

3. **Update all environments with new secret**

---

### 10.2: Use Azure Key Vault (Production Recommendation)

Instead of storing secrets in Function App Configuration, use Key Vault:

#### Step 1: Create Key Vault

```bash
az keyvault create \
  --name "consultologist-vault" \
  --resource-group "YOUR_RESOURCE_GROUP" \
  --location "eastus"
```

#### Step 2: Add Secrets to Key Vault

```bash
az keyvault secret set \
  --vault-name "consultologist-vault" \
  --name "AzureAd-ClientSecret" \
  --value "YOUR_SECRET_VALUE"
```

#### Step 3: Enable Managed Identity for Function App

```bash
az functionapp identity assign \
  --name "YOUR_FUNCTION_APP_NAME" \
  --resource-group "YOUR_RESOURCE_GROUP"
```

#### Step 4: Grant Function App Access to Key Vault

```bash
az keyvault set-policy \
  --name "consultologist-vault" \
  --object-id "FUNCTION_APP_PRINCIPAL_ID" \
  --secret-permissions get list
```

#### Step 5: Reference Key Vault in Function App Settings

```bash
az functionapp config appsettings set \
  --name "YOUR_FUNCTION_APP_NAME" \
  --resource-group "YOUR_RESOURCE_GROUP" \
  --settings "AzureAd_ClientSecret=@Microsoft.KeyVault(SecretUri=https://consultologist-vault.vault.azure.net/secrets/AzureAd-ClientSecret/)"
```

---

### 10.3: Rotate Secrets Regularly

#### Set Up Rotation Schedule

| Environment | Rotation Frequency | Reminder |
|-------------|-------------------|----------|
| Development | Every 180 days | Calendar reminder |
| Production | Every 90 days | Automated alerts |

#### Rotation Process

1. **2 weeks before expiration:**
   - Create new secret in Azure AD
   - Note the new value

2. **1 week before expiration:**
   - Update Azure Function App configuration with new secret
   - Deploy and verify it works
   - Keep old secret active (for rollback)

3. **After expiration of old secret:**
   - Delete the old secret from Azure AD
   - Remove old secret from Key Vault (if using)

#### Automation Script

```bash
#!/bin/bash
# secret-rotation.sh

VAULT_NAME="consultologist-vault"
APP_REG_ID="YOUR_CLIENT_ID"

# Create new secret in Azure AD (manual step or use API)
echo "Create new secret in Azure Portal"

# Update Key Vault
az keyvault secret set \
  --vault-name $VAULT_NAME \
  --name "AzureAd-ClientSecret" \
  --value "$NEW_SECRET_VALUE"

echo "✅ Secret rotated successfully"
echo "⚠️ Remember to delete old secret from App Registration after verification"
```

---

### 10.4: Monitor Access and Usage

#### Enable Azure AD Audit Logs

1. Portal → Azure Active Directory → Monitoring → Sign-in logs
2. Filter by your Application (client) ID
3. Monitor for:
   - Unusual access patterns
   - Failed authentication attempts
   - Access from unexpected locations

#### Enable Function App Monitoring

1. Portal → Function App → Application Insights
2. Set up alerts for:
   - High error rates
   - Failed authentication attempts
   - Unusual traffic patterns

#### Set Up Alerts

```bash
# Create alert for failed Graph API calls
az monitor metrics alert create \
  --name "graph-api-failures" \
  --resource-group "YOUR_RESOURCE_GROUP" \
  --scopes "/subscriptions/SUB_ID/resourceGroups/RG/providers/Microsoft.Web/sites/FUNC_APP" \
  --condition "count requests where responseCode >= 400" \
  --description "Alert on Graph API failures"
```

---

### 10.5: Principle of Least Privilege

#### Use Minimal Permissions

Instead of `User.Read.All`, use:
- ✅ `User.ReadBasic.All` - Only basic profile info
- ❌ `User.Read.All` - Full profile including sensitive data

#### Review Permissions Regularly

```bash
# List current permissions
az ad app permission list --id YOUR_CLIENT_ID

# Remove unnecessary permissions via Portal:
# Portal → App Registration → API permissions → Remove permission
```

#### Limit Token Lifetime (Advanced)

Configure shorter token lifetimes for sensitive environments:

1. Portal → Azure Active Directory → Security → Conditional Access
2. Create policy to limit token lifetime
3. Apply to your application

---

### 10.6: Secure Local Development

#### Use User Secrets in .NET (Alternative to local.settings.json)

```bash
cd Api

# Initialize user secrets
dotnet user-secrets init

# Add secrets
dotnet user-secrets set "AzureAd_TenantId" "YOUR_TENANT_ID"
dotnet user-secrets set "AzureAd_ClientId" "YOUR_CLIENT_ID"
dotnet user-secrets set "AzureAd_ClientSecret" "YOUR_SECRET"
```

Update `Program.cs` to read from user secrets:
```csharp
builder.Configuration.AddUserSecrets<Program>();
```

#### Encrypt local.settings.json (Alternative)

Azure Functions supports encryption:

```bash
func settings encrypt
```

This encrypts sensitive values in `local.settings.json`.

---

### 10.7: Security Checklist

Before going to production, verify:

- [ ] `local.settings.json` is in `.gitignore`
- [ ] No secrets committed to Git history
- [ ] Client secret stored in Key Vault (production)
- [ ] Managed identity enabled for Function App
- [ ] Using `User.ReadBasic.All` (not `User.Read.All`)
- [ ] Secret expiration reminder set
- [ ] Application Insights enabled
- [ ] Alerts configured for failures
- [ ] Azure AD audit logs reviewed
- [ ] CORS properly configured
- [ ] HTTPS enforced (no HTTP)
- [ ] Authentication required on all API routes

---

## Appendix

### A. Quick Reference

#### Configuration Values Summary

| Value | Where to Find | Where to Use |
|-------|---------------|--------------|
| **Tenant ID** | Portal → Azure AD → Overview → Tenant ID | `AzureAd_TenantId` |
| **Client ID** | Portal → App Registration → Overview → Application (client) ID | `AzureAd_ClientId` |
| **Client Secret** | Portal → App Registration → Certificates & secrets → Value | `AzureAd_ClientSecret` |

#### Important URLs

| Purpose | URL Pattern |
|---------|-------------|
| **Azure Portal** | https://portal.azure.com |
| **App Registrations** | Portal → Azure Active Directory → App registrations |
| **Static Web App Auth** | https://YOUR-APP.azurestaticapps.net/.auth/login/aad |
| **Graph API Explorer** | https://developer.microsoft.com/graph/graph-explorer |
| **Admin Consent URL** | https://login.microsoftonline.com/{TENANT_ID}/adminconsent?client_id={CLIENT_ID} |

#### Permission Details

| Permission | Type | Scope | Admin Consent | What It Grants |
|------------|------|-------|---------------|----------------|
| `User.ReadBasic.All` | Application | All users | Required | Read basic profile (name, email, job title) |
| `User.Read.All` | Application | All users | Required | Read full profile (includes phone, office, etc.) |
| `User.Read` | Delegated | Current user | Not required | Read current user's profile only |

---

### B. Common Commands Cheat Sheet

#### Azure CLI

```bash
# Login
az login

# List subscriptions
az account list --output table

# Set active subscription
az account set --subscription "SUBSCRIPTION_ID"

# List app registrations
az ad app list --display-name "Consultologist" --output table

# Show app details
az ad app show --id YOUR_CLIENT_ID

# List Function Apps
az functionapp list --output table

# Get Function App settings
az functionapp config appsettings list --name FUNC_APP --resource-group RG

# Update Function App settings
az functionapp config appsettings set --name FUNC_APP --resource-group RG \
  --settings "AzureAd_ClientId=VALUE"
```

#### PowerShell

```powershell
# Connect to Azure AD
Connect-AzureAD

# Get app registration
Get-AzureADApplication -Filter "displayName eq 'Consultologist'"

# Get service principal
Get-AzureADServicePrincipal -Filter "appId eq 'YOUR_CLIENT_ID'"

# List permissions
Get-AzureADServiceAppRoleAssignment -ObjectId "SP_OBJECT_ID"

# Grant admin consent (see Part 3 for full script)
New-AzureADServiceAppRoleAssignment -ObjectId $sp.ObjectId ...
```

#### Azure Functions Core Tools

```bash
# Start local function
func start

# Deploy to Azure
func azure functionapp publish FUNCTION_APP_NAME

# List functions
func azure functionapp list-functions FUNCTION_APP_NAME

# View logs
func azure functionapp logstream FUNCTION_APP_NAME
```

#### .NET CLI

```bash
# Build solution
dotnet build

# Run Blazor app
cd Client && dotnet run

# Restore packages
dotnet restore

# User secrets
dotnet user-secrets set "Key" "Value"
dotnet user-secrets list
```

---

### C. Verification Checklist

Use this checklist to verify your setup:

#### Azure AD App Registration
- [ ] App registration created
- [ ] Application (client) ID recorded
- [ ] Directory (tenant) ID recorded
- [ ] Redirect URI configured for Static Web App
- [ ] API permission `User.ReadBasic.All` added (Application type)
- [ ] Admin consent granted (green checkmark visible)
- [ ] Client secret created
- [ ] Client secret value recorded
- [ ] Secret expiration date noted

#### Local Configuration
- [ ] `Api/local.settings.json` exists
- [ ] `AzureAd_TenantId` set correctly
- [ ] `AzureAd_ClientId` set correctly
- [ ] `AzureAd_ClientSecret` set correctly
- [ ] JSON syntax is valid
- [ ] File is in `.gitignore`

#### Azure Configuration
- [ ] Static Web App created
- [ ] Static Web App authentication configured (Azure AD)
- [ ] Function App created
- [ ] Function App settings configured (3 AzureAd values)
- [ ] Code deployed to Static Web App
- [ ] Code deployed to Function App
- [ ] Function App linked to Static Web App (if using managed functions)

#### Testing
- [ ] Local function starts without errors
- [ ] Can authenticate in deployed app
- [ ] User profile displays correctly
- [ ] Logout works correctly
- [ ] No errors in browser console
- [ ] API returns 200 (not 401 or 403)
- [ ] Tested in multiple browsers

#### Security
- [ ] `local.settings.json` in `.gitignore`
- [ ] No secrets committed to Git
- [ ] Secret expiration reminder set
- [ ] Application Insights enabled
- [ ] HTTPS enforced

---

### D. Troubleshooting Decision Tree

```
API call fails
│
├─ Status 401 "Authorization_RequestDenied"
│  └─ Admin consent not granted → Go to Part 3
│
├─ Status 401 "invalid_client"
│  └─ Wrong Client ID or Secret → Verify configuration in Part 5/7
│
├─ Status 404 "User not found"
│  └─ Invalid user ID → Check Static Web Apps auth
│
├─ Status 500 "Missing configuration"
│  └─ Config not loaded → Check local.settings.json or Function App settings
│
└─ Status 403 "Insufficient permissions"
   └─ Wrong permission granted → Check you have User.ReadBasic.All, not User.Read
```

---

### E. Migration from Other Auth Methods

#### If Migrating from MSAL in Blazor

1. **Remove MSAL packages:**
   ```bash
   cd Client
   dotnet remove package Microsoft.Authentication.WebAssembly.Msal
   ```

2. **Remove MSAL configuration:**
   - Delete `Program.cs` MSAL setup
   - Remove `appsettings.json` MSAL config

3. **Configure Static Web Apps authentication:**
   - Follow Part 7.2

4. **Update components:**
   - Replace `AuthenticationStateProvider` with `AuthenticationService`
   - Use `/.auth/me` endpoint instead of MSAL

#### If Migrating from On-Behalf-Of Flow

1. **Update GraphService.cs:**
   - Replace `OnBehalfOfCredential` with `ClientSecretCredential`
   - Remove user token parameter

2. **Simplify GetUserProfileFunction.cs:**
   - Remove token extraction logic
   - Only extract user ID from `x-ms-client-principal`

3. **Grant admin consent:**
   - Change from delegated to application permissions
   - Follow Part 3

---

### F. Additional Resources

#### Microsoft Documentation
- [Client Credentials Flow](https://learn.microsoft.com/en-us/azure/active-directory/develop/v2-oauth2-client-creds-grant-flow)
- [Microsoft Graph Permissions Reference](https://learn.microsoft.com/en-us/graph/permissions-reference)
- [Azure Static Web Apps Authentication](https://learn.microsoft.com/en-us/azure/static-web-apps/authentication-authorization)
- [Azure Functions Configuration](https://learn.microsoft.com/en-us/azure/azure-functions/functions-app-settings)

#### Tools
- [Graph Explorer](https://developer.microsoft.com/graph/graph-explorer) - Test Graph API calls
- [JWT.io](https://jwt.io) - Decode access tokens
- [JSON Formatter](https://jsonformatter.org) - Validate JSON syntax

#### Project Documentation
- `GRAPH_API_SETUP.md` - Original detailed setup guide
- `Research/application-auth.md` - Architecture explanation and design decisions
- `Research/obof-auth.md` - Alternative authentication approaches

---

### G. Support and Feedback

#### Getting Help

1. **Check existing documentation:**
   - This guide
   - `GRAPH_API_SETUP.md`
   - Microsoft Docs

2. **Review troubleshooting section:**
   - Part 9 of this guide

3. **Check logs:**
   - Azure Function logs
   - Browser console
   - Application Insights

4. **Search for similar issues:**
   - GitHub repository issues
   - Stack Overflow
   - Microsoft Q&A

5. **Create an issue:**
   - Include error messages
   - Include configuration (redact secrets!)
   - Include steps to reproduce

---

## Summary

You've now completed the full setup of Application Authentication using Client Credentials Flow!

### What You Accomplished

✅ Configured Azure AD App Registration  
✅ Granted admin consent for Graph API access  
✅ Secured client credentials  
✅ Configured local and production environments  
✅ Deployed to Azure  
✅ Tested end-to-end authentication  
✅ Implemented security best practices  

### Next Steps

1. **Monitor your application:**
   - Review Application Insights regularly
   - Check for errors or unusual patterns

2. **Set up secret rotation:**
   - Calendar reminder before expiration
   - Document rotation process

3. **Add features:**
   - Display additional profile fields
   - Implement role-based access
   - Add custom claims

4. **Optimize performance:**
   - Implement caching for user profiles
   - Optimize API calls

### Maintenance Reminders

- [ ] Set calendar reminder for secret expiration
- [ ] Review Azure AD audit logs monthly
- [ ] Update documentation when configuration changes
- [ ] Test authentication after Azure updates

---

**Congratulations! Your application authentication is now fully configured and production-ready.**

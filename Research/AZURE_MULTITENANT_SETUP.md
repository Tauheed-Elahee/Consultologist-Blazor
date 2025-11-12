# Azure Multi-Tenant Configuration

## Overview

This application is configured to support **multi-tenant authentication**, allowing users from any Azure AD organization to sign in. This document describes the required Azure Portal configuration.

## Required Azure App Registration Changes

### Step 1: Navigate to Your App Registration

1. Go to [Azure Portal](https://portal.azure.com)
2. Navigate to **Azure Active Directory** → **App registrations**
3. Find and click on your application:
   - **Application (client) ID**: `7aea065e-4632-43d3-adb7-9cd315f2b8da`

### Step 2: Configure Multi-Tenant Support

1. In the left sidebar, click **Authentication**
2. Scroll down to **Supported account types** section
3. Select: **"Accounts in any organizational directory (Any Azure AD directory - Multitenant)"**
4. Click **Save** at the top of the page

### Step 3: Verify Configuration

After saving, verify that:
- ✅ Supported account types shows: **"Accounts in any organizational directory (Any Azure AD directory - Multitenant)"**
- ✅ Application type shows: **"Web"** or **"Single-page application (SPA)"**
- ✅ Redirect URIs are correctly configured for your deployment environment

## Application Configuration

The application's `wwwroot/appsettings.json` has been configured with:

```json
{
  "AzureAd": {
    "Authority": "https://login.microsoftonline.com/organizations",
    "ClientId": "7aea065e-4632-43d3-adb7-9cd315f2b8da",
    "ValidateAuthority": true
  }
}
```

**Key Points:**
- Authority URL uses `organizations` instead of a specific tenant ID
- This allows users from any Azure AD tenant to sign in
- Personal Microsoft accounts are **NOT** supported (use `common` if needed)

## How Multi-Tenant Authentication Works

### For Users in Your Organization
- Users sign in normally with their work/school accounts
- No additional consent required

### For External Users (Other Organizations)

1. **First-time sign-in:**
   - User attempts to sign in
   - Azure AD prompts: "Needs permission to access resources in your organization"
   - User sees list of requested permissions (e.g., `User.Read`)
   - User must click **"Accept"** or have their admin approve

2. **Admin Consent (if required):**
   - Some organizations require admin approval for external apps
   - The user's Azure AD admin must approve your app for their organization
   - Once approved, all users in that organization can sign in

3. **Subsequent sign-ins:**
   - Users sign in normally without additional consent prompts
   - Your app appears in their organization's enterprise applications

## API Permissions

Ensure your app registration has the necessary API permissions configured:

1. Navigate to **API permissions** in your app registration
2. Verify permissions are configured (e.g., `User.Read` for Microsoft Graph)
3. For multi-tenant apps, consider which permissions require admin consent:
   - **User delegated permissions** (e.g., `User.Read`) - Usually don't require admin consent
   - **Application permissions** - Always require admin consent
   - **Sensitive delegated permissions** - May require admin consent

## Security Considerations

### 1. Token Validation
- The app validates tokens from any Azure AD organization
- Ensure you implement proper authorization logic in your app
- Don't rely solely on authentication - implement role-based access control

### 2. Tenant Isolation (if needed)
If you need to restrict access to specific tenants:

```csharp
// Add in Program.cs or authentication configuration
builder.Services.Configure<MsalProviderOptions>(options =>
{
    // Add allowed tenant IDs
    var allowedTenants = new[] 
    { 
        "4258958f-9334-4a8c-af82-d7cddc47ae50",  // Your tenant
        "another-tenant-id-here" 
    };
    
    // Validate tenant in token
    // (Implementation depends on your authorization requirements)
});
```

### 3. Admin Consent URL (Optional)
To pre-approve your app for an organization, send admins this consent URL:

```
https://login.microsoftonline.com/{tenant-id}/v2.0/adminconsent
  ?client_id=7aea065e-4632-43d3-adb7-9cd315f2b8da
  &redirect_uri=https://app.consultologist.ai/authentication/login-callback
  &scope=https://graph.microsoft.com/User.Read
```

Replace `{tenant-id}` with the target organization's tenant ID or use `organizations` for any organization.

## Troubleshooting

### Error: "Selected user account does not exist in tenant"

**Cause:** The Azure App Registration is still configured as single-tenant.

**Solution:** Follow Step 2 above to change to multi-tenant configuration.

### Error: "AADSTS700016: Application not found in directory"

**Cause:** User's organization hasn't consented to the app yet.

**Solutions:**
1. User clicks "Accept" on the consent prompt when signing in
2. User's admin pre-approves the app using admin consent URL
3. User contacts their IT admin to approve the application

### Error: "AADSTS50020: User account from identity provider does not exist"

**Cause:** User is trying to sign in with a personal Microsoft account, but the app only allows organizational accounts.

**Solution:** 
- If personal accounts are needed, change Authority to `https://login.microsoftonline.com/common`
- Update Azure App Registration to "Accounts in any organizational directory and personal Microsoft accounts"

## Testing Multi-Tenant Configuration

1. **Test with your organization's users:**
   - Should work immediately without changes

2. **Test with external organization users:**
   - Have a user from another Azure AD organization attempt to sign in
   - They should see a consent prompt
   - After accepting, they should be able to sign in successfully

3. **Verify token claims:**
   - Check the `tid` (tenant ID) claim in the access token
   - Should show different tenant IDs for users from different organizations

## References

- [Microsoft Identity Platform Multi-Tenant Apps](https://docs.microsoft.com/en-us/azure/active-directory/develop/howto-convert-app-to-be-multi-tenant)
- [Admin Consent Experience](https://docs.microsoft.com/en-us/azure/active-directory/develop/v2-admin-consent)
- [Supported Account Types](https://docs.microsoft.com/en-us/azure/active-directory/develop/supported-accounts-validation)

## Support

For issues with Azure AD configuration, contact your Azure administrator or refer to Microsoft's documentation.

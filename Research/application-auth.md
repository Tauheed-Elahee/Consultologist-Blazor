# Application Authentication (Client Credentials Flow)

## Overview

Application authentication, also known as **Client Credentials Flow**, is an OAuth 2.0 authentication pattern where an application authenticates as itself (not on behalf of a user) to access resources.

In our implementation, the Azure Function uses its own identity to authenticate to Microsoft Graph API and retrieve user profiles.

---

## How It Works

### Authentication Flow

```
┌──────────────────┐
│ Azure Function   │
│ (Your API)       │
└────────┬─────────┘
         │ 1. Authenticate with Client ID + Secret
         ▼
┌──────────────────┐
│  Azure AD        │
│  Token Endpoint  │
└────────┬─────────┘
         │ 2. Returns access token (app identity)
         ▼
┌──────────────────┐
│ Azure Function   │
│ (Has app token)  │
└────────┬─────────┘
         │ 3. Call Graph API with token
         ▼
┌──────────────────┐
│ Microsoft Graph  │
│ GET /users/{id}  │
└──────────────────┘
```

### Step-by-Step Process

1. **User authenticates** via Azure Static Web Apps
2. **Static Web Apps** passes user ID in `x-ms-client-principal` header to Azure Function
3. **Azure Function** authenticates to Azure AD using:
   - Client ID (Application ID)
   - Client Secret (password)
   - Tenant ID
4. **Azure AD** issues an access token representing the **application** (not the user)
5. **Azure Function** calls Microsoft Graph API with the app token
6. **Graph API** verifies the app has permission to read user data
7. **Graph API** returns the requested user's profile

---

## Current Implementation

### Code in GraphService.cs

```csharp
using Azure.Identity;
using Microsoft.Graph;

public class GraphService
{
    private readonly ClientSecretCredential _credential;
    
    public GraphService(IConfiguration configuration)
    {
        var tenantId = configuration["AzureAd_TenantId"];
        var clientId = configuration["AzureAd_ClientId"];
        var clientSecret = configuration["AzureAd_ClientSecret"];
        
        // Authenticate as the application itself
        _credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
    }
    
    public async Task<User?> GetUserProfileAsync(string userId)
    {
        var graphClient = new GraphServiceClient(_credential);
        
        // Query user by their object ID (from Static Web Apps auth)
        return await graphClient.Users[userId].GetAsync();
    }
}
```

### Environment Variables Required

| Variable | Description | Example |
|----------|-------------|---------|
| `AzureAd_TenantId` | Your Azure AD Tenant ID | `12345678-1234-1234-1234-123456789abc` |
| `AzureAd_ClientId` | Application (Client) ID from App Registration | `87654321-4321-4321-4321-cba987654321` |
| `AzureAd_ClientSecret` | Client Secret value (not ID) | `abc~123XYZ...` |

---

## Permissions Required

### Application Permissions (Not Delegated)

The app needs **Application Permissions** because it acts as itself, not on behalf of a user.

**Required Permission:**
- **`User.Read.All`** - Read all users' full profiles
- **OR** **`User.ReadBasic.All`** - Read all users' basic profiles (recommended)

### Why Not Delegated Permissions?

- Delegated permissions require a user's access token
- Static Web Apps doesn't pass user tokens to Azure Functions
- Application permissions allow the app to act independently

---

## Admin Consent Requirement

### Why Admin Consent is Required

⚠️ **All Application Permissions require admin consent** - this is a security feature by design.

**Reasons:**
1. Application permissions grant broad access (can read ANY user's data)
2. The application acts independently without user interaction
3. Protects organizations from rogue applications

### How to Grant Admin Consent

**Azure Portal:**

1. Go to **Azure Portal** → **Azure Active Directory**
2. Click **App registrations** → Find your app
3. Click **API permissions** in left menu
4. Verify `User.Read.All` or `User.ReadBasic.All` is listed
5. Click **"Grant admin consent for [Your Organization]"**
6. Confirm by clicking **"Yes"**
7. Status should show green checkmark ✅

**PowerShell:**
```powershell
Connect-AzureAD
$sp = Get-AzureADServicePrincipal -Filter "appId eq 'YOUR_CLIENT_ID'"
$graphSp = Get-AzureADServicePrincipal -Filter "appId eq '00000003-0000-0000-c000-000000000000'"
$permission = $graphSp.AppRoles | Where-Object {$_.Value -eq "User.Read.All"}
New-AzureADServiceAppRoleAssignment -ObjectId $sp.ObjectId -PrincipalId $sp.ObjectId `
    -ResourceId $graphSp.ObjectId -Id $permission.Id
```

**Azure CLI:**
```bash
az ad app permission grant \
  --id YOUR_APP_REGISTRATION_ID \
  --api 00000003-0000-0000-c000-000000000000 \
  --scope User.Read.All
```

### Who Can Grant Consent?

Users with these Azure AD roles:
- **Global Administrator**
- **Cloud Application Administrator**
- **Application Administrator**

---

## Advantages

### ✅ Pros

1. **Simple Implementation**
   - Straightforward code
   - No complex token handling
   - Works with Static Web Apps seamlessly

2. **Reliable**
   - Doesn't depend on user tokens
   - App token cached and reused
   - Fewer points of failure

3. **Performance**
   - App tokens last longer (up to 24 hours)
   - Can be cached efficiently
   - Reduces token acquisition overhead

4. **Works Perfectly for Reading User Data**
   - Azure Function receives user ID from Static Web Apps
   - Can query any user's profile by ID
   - No token exchange required

5. **Better for Server-Side Operations**
   - Background jobs can run without user context
   - Scheduled tasks can access Graph API
   - Service-to-service authentication

---

## Disadvantages

### ❌ Cons

1. **Requires Admin Consent**
   - Cannot bypass this requirement
   - May need IT approval process
   - Can take time to get approved

2. **Broad Permissions**
   - App can read ANY user's profile
   - Not limited to current user
   - Potential security concern if app is compromised

3. **No User Context**
   - Cannot access user-specific resources (e.g., user's OneDrive)
   - Cannot perform actions "as the user"
   - Audit logs show app name, not user name

4. **Cannot Access User-Owned Resources**
   - Can't read user's emails, calendar, files
   - Limited to directory data (user profiles, groups)
   - Not suitable for accessing personal data

---

## When to Use Application Authentication

### ✅ Good Use Cases

- **Reading user profiles** from Azure AD
- **Directory queries** (list users, groups)
- **Service-to-service** authentication
- **Background jobs** without user interaction
- **Server-side operations** that don't require user context
- **Scenarios where admin consent is available**

### ❌ Not Suitable For

- Accessing user's personal data (emails, files, calendar)
- Scenarios where admin consent is impossible
- When you need to audit which user performed an action
- Applications that require user-specific permissions

---

## Security Considerations

### Best Practices

1. **Protect Client Secrets**
   - ✅ Store in Azure Key Vault (production)
   - ✅ Use environment variables (development)
   - ❌ Never commit to source control
   - ✅ Rotate secrets regularly

2. **Use Least Privilege**
   - ✅ Use `User.ReadBasic.All` instead of `User.Read.All` when possible
   - ✅ Only request permissions you actually need
   - ❌ Avoid requesting broad permissions unnecessarily

3. **Monitor Access**
   - ✅ Enable Azure AD audit logs
   - ✅ Monitor Graph API usage
   - ✅ Set up alerts for suspicious activity

4. **Implement Token Caching**
   - ✅ Cache access tokens (they last hours)
   - ✅ Respect token expiration
   - ✅ Handle token refresh gracefully

---

## Troubleshooting

### Error: "Insufficient privileges to complete the operation"

**Problem:** Admin consent not granted

**Solution:**
1. Verify permission is added in App Registration → API permissions
2. Check if "Grant admin consent" was clicked
3. Verify you have admin rights to grant consent
4. Check if permission status shows green checkmark

### Error: "Authorization_RequestDenied"

**Problem:** Same as above - admin consent missing

**Solution:** See GRAPH_API_SETUP.md for detailed steps

### Error: "invalid_client"

**Problem:** Client ID or Client Secret is incorrect

**Solution:**
1. Verify `AzureAd_ClientId` matches App Registration Client ID
2. Verify `AzureAd_ClientSecret` is the **Value** not **Secret ID**
3. Check if secret has expired
4. Create new secret if needed

### Error: "User not found"

**Problem:** User ID from Static Web Apps doesn't match Azure AD

**Solution:**
1. Verify user exists in Azure AD
2. Check if userId format is correct (should be GUID)
3. Verify authentication provider is Azure AD (`aad`)

---

## Microsoft Documentation

### Official Resources

**OAuth 2.0 Client Credentials Flow:**
- [Microsoft Identity Platform - Client Credentials Flow](https://learn.microsoft.com/en-us/azure/active-directory/develop/v2-oauth2-client-creds-grant-flow)
- [OAuth 2.0 Client Credentials Grant](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-client-creds-grant-flow)

**Microsoft Graph Permissions:**
- [Microsoft Graph Permissions Reference](https://learn.microsoft.com/en-us/graph/permissions-reference)
- [Understanding Application vs Delegated Permissions](https://learn.microsoft.com/en-us/graph/auth/auth-concepts#microsoft-graph-permissions)
- [User Resource Permissions](https://learn.microsoft.com/en-us/graph/permissions-reference#user-permissions)

**Azure Identity Library:**
- [Azure Identity Client Library for .NET](https://learn.microsoft.com/en-us/dotnet/api/overview/azure/identity-readme)
- [ClientSecretCredential Class](https://learn.microsoft.com/en-us/dotnet/api/azure.identity.clientsecretcredential)

**Admin Consent:**
- [Admin Consent Overview](https://learn.microsoft.com/en-us/azure/active-directory/manage-apps/consent-and-permissions-overview)
- [Grant Tenant-Wide Admin Consent](https://learn.microsoft.com/en-us/azure/active-directory/manage-apps/grant-admin-consent)

**Azure Static Web Apps:**
- [Azure Static Web Apps Authentication](https://learn.microsoft.com/en-us/azure/static-web-apps/authentication-authorization)
- [Accessing User Information](https://learn.microsoft.com/en-us/azure/static-web-apps/user-information)

**Microsoft Graph SDK:**
- [Microsoft Graph SDK for .NET](https://learn.microsoft.com/en-us/graph/sdks/sdks-overview)
- [Use Microsoft Graph with Azure Functions](https://learn.microsoft.com/en-us/graph/tutorials/azure-functions)

---

## Comparison with Other Auth Methods

| Feature | Client Credentials | Delegated (MSAL) | On-Behalf-Of |
|---------|-------------------|------------------|---------------|
| **Admin Consent** | Required | Not for User.Read | Required |
| **User Context** | No | Yes | Yes |
| **Implementation** | Simple | Moderate | Complex |
| **Token Location** | Server-side | Client-side | Server-side |
| **Works in SWA** | Yes | Yes (different arch) | No |
| **Access User Data** | No | Yes | Yes |

---

## Related Files in This Project

- `Api/Services/GraphService.cs` - Implementation of Client Credentials
- `Api/GetUserProfileFunction.cs` - Function that uses GraphService
- `Api/Helpers/AuthenticationHelper.cs` - Extracts user ID from SWA auth
- `GRAPH_API_SETUP.md` - Setup instructions
- `Research/obof-auth.md` - Alternative authentication approach

---

## Summary

Application Authentication (Client Credentials Flow) is the **current and recommended approach** for this project because:

✅ Works seamlessly with Azure Static Web Apps  
✅ Simple and reliable implementation  
✅ Perfect for reading user profiles by ID  
✅ Code is already correct  

❌ Requires admin consent (only blocker)

**Next Step:** Get admin consent for `User.ReadBasic.All` permission to resolve the "Authorization_RequestDenied" error.

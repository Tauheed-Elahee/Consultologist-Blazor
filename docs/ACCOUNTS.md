# Accounts, Entra API Access, and LinkedIn Login

## Summary

This app currently uses Microsoft Entra ID login through MSAL in the Blazor WebAssembly client. Protected Azure Functions validate an Entra access token for the Consultologist API and resolve the caller to an internal app account stored in Azure Tables.

LinkedIn login is possible, but it should not be implemented as browser-only authorization. A Blazor WebAssembly app runs on the user's machine, so any rule like "only allow verified LinkedIn accounts" must be enforced by a trusted server-side component.

The preferred lightweight server-side component for this repository is the existing Azure Functions project under `Api/`.

## Current Entra Setup

The production setup uses two app registrations:

- **Consultologist SPA**: `7aea065e-4632-43d3-adb7-9cd315f2b8da`
- **Consultologist API**: `b3866040-8bae-4c01-88ba-ecff646df451`

The SPA signs users in and requests the delegated API scope only when calling protected Functions:

```text
api://b3866040-8bae-4c01-88ba-ecff646df451/access_as_user
```

The API app registration must expose:

```text
Application ID URI: api://b3866040-8bae-4c01-88ba-ecff646df451
Scope: access_as_user
requestedAccessTokenVersion: 2
```

The SPA app registration must have delegated permission to the API scope. The API registration may also pre-authorize the SPA client application for `access_as_user`.

The Function App settings should be:

```text
Auth__Authority=https://login.microsoftonline.com/4258958f-9334-4a8c-af82-d7cddc47ae50/v2.0
Auth__Audience=b3866040-8bae-4c01-88ba-ecff646df451
Auth__RequiredScope=access_as_user
AccountStorage__ConnectionStringName=AzureWebJobsStorage
```

Expected access-token claims:

```text
ver=2.0
iss=https://login.microsoftonline.com/4258958f-9334-4a8c-af82-d7cddc47ae50/v2.0
aud=b3866040-8bae-4c01-88ba-ecff646df451
scp=access_as_user
```

Bearer tokens must not be pasted into logs, issues, or chat. Use decoded claim summaries when debugging.

## Account Settings

Authenticated user preferences are stored server-side in Azure Table Storage so they follow the app account across browsers and devices.

Current tables:

- `AppUsers`: internal app user records.
- `IdentityLinks`: provider identity to app user lookup.
- `UserIdentityLinks`: app user to linked identities lookup.
- `AccountSettings`: per-user settings keyed by app user ID and setting key.

The generic settings endpoint shape is:

```text
GET    /api/Account/Settings/{key}
PUT    /api/Account/Settings/{key}
DELETE /api/Account/Settings/{key}
```

The initial setting key is:

```text
consult.sectionStandardsMarkdown
```

`GET` returns `404` when the setting has not been saved, and the Blazor app falls back to `wwwroot/templates/consult_section_standards.md`. `PUT` accepts a JSON body with `value` and `contentType`. `DELETE` removes the override and restores the default behavior.

## LinkedIn Login

LinkedIn login can be added through an OAuth 2.0 / OpenID Connect flow. The app should not store LinkedIn client secrets or perform privileged token exchange directly in Blazor WebAssembly.

Recommended implementation options:

- Use Azure Static Web Apps custom OpenID Connect authentication for LinkedIn, then enforce authorization in an API-backed gate.
- Use Microsoft Entra External ID or a similar identity broker to present LinkedIn as a login option while the app continues to use a Microsoft identity layer.
- Add a backend-for-frontend or serverless auth endpoint that handles LinkedIn OAuth, profile lookup, verification checks, and app session issuance.

## Verified LinkedIn Requirement

LinkedIn sign-in alone does not prove the LinkedIn account is identity-verified. To accept only verified LinkedIn users, the trusted server-side component should call LinkedIn's Verified on LinkedIn API after login.

The verification check should call:

```text
GET https://api.linkedin.com/rest/verificationReport
```

The LinkedIn app must request the required verification scope, such as `r_verify_details`.

The access policy should decide which verification categories are acceptable:

- `IDENTITY`: government-ID based verification.
- `WORKPLACE`: workplace verification, such as company email or Microsoft Entra-backed workplace verification.

Default recommendation: require at least `IDENTITY`. If the product needs workplace-specific trust, require `WORKPLACE` as well.

If the user is not verified, the app should deny access or direct the user to LinkedIn's verification URL when LinkedIn provides one.

## Server-Side Enforcement

The authorization decision must be made by a trusted component, such as an Azure Function. Browser-side checks may improve the user experience, but they are not authorization.

The server-side component should:

- Complete or receive the LinkedIn authentication result.
- Use a securely stored LinkedIn client secret when required.
- Call LinkedIn profile and verification APIs.
- Decide whether the LinkedIn identity satisfies the app's access policy.
- Issue or allow access only after verification succeeds.
- Enforce the same rule for protected APIs, not only for visible UI.

## Account Linking

The app should use one internal app user record and attach provider identities to it. Provider identity IDs are the source of truth; email addresses are only candidate match signals.

For Microsoft Entra ID:

- Store a stable Entra identity key, preferably tenant ID plus object ID.
- Treat Entra `oid` as stronger than email-like claims.
- Use `preferred_username`, `email`, or `upn` only as candidate email values.

For LinkedIn:

- Store LinkedIn's stable OpenID Connect `sub`, or a stable ID returned by LinkedIn's verified identity APIs.
- Require the configured LinkedIn verification policy before linking or granting access.
- Use LinkedIn's primary email only as a candidate match when available.

## Recommended Flow

For Entra login:

1. Resolve the user by the stored Entra identity key.
2. If no linked Entra identity exists, check whether the trusted Entra email matches an existing internal account.
3. If a match exists, require confirmation before linking unless the account is already trusted by policy.
4. After linking, future Entra logins resolve to the same internal app user.

For LinkedIn login:

1. Authenticate with LinkedIn through the server-side auth flow.
2. Call LinkedIn verification APIs.
3. Reject access if the required verification category is missing.
4. Resolve the user by stored LinkedIn identity ID.
5. If no linked LinkedIn identity exists, compare LinkedIn's verified or trusted email candidate to existing app users.
6. If the email matches an Entra-linked user, require the user to sign in with Entra once to confirm the link.
7. After linking, LinkedIn and Entra logins both resolve to the same internal app user.

## Do Not Do

- Do not store LinkedIn client secrets in Blazor WebAssembly.
- Do not trust browser-side checks as authorization.
- Do not merge accounts based only on matching email addresses.
- Do not treat LinkedIn sign-in as proof of LinkedIn identity verification.
- Do not use mutable profile fields, display names, or email alone as permanent account keys.

## Acceptance Criteria

- LinkedIn login is documented as possible.
- Verified LinkedIn-only access is documented as requiring LinkedIn's verification APIs.
- A backend or serverless function is documented as required for secure enforcement.
- Entra and LinkedIn account linking is documented around stable provider IDs.
- Email matching is documented as a candidate match, not sole proof of identity.

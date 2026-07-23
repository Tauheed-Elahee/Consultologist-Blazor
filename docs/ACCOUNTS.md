# Accounts, Entra API Access, and LinkedIn Linking

## Summary

This app uses Microsoft Entra ID login through MSAL in the Blazor WebAssembly client. Protected Azure Functions validate an Entra access token for the Consultologist API and resolve the caller to an internal app account stored in Azure Tables.

LinkedIn is a **verification signal**, not a login provider (#133): a Connect LinkedIn flow on the Profile page proves the signed-in user controls a real LinkedIn identity, and the stored link feeds the operator's manual activation decision. Microsoft remains the only credential authority. The flow is enforced server-side in the Azure Functions project — browser-only checks are never authorization.

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

The SPA app registration must have delegated permission to the API scope.

Since 2026-07-18 sign-in is **multi-tenant**, and since 2026-07-23 (#132) it
also accepts **personal Microsoft accounts**: both registrations use
`signInAudience: AzureADandPersonalMicrosoftAccount`, and the API
registration lists the SPA client id in `api.knownClientApplications` so a
foreign tenant grants one combined consent covering both apps (see the
"Multi-tenant sign-in" section in `CONFIGURATION.md` for where each setting
lives, the MSA-specific registration constraints, and what a foreign tenant
still needs).

The Function App settings should be:

```text
Auth__Authority=https://login.microsoftonline.com/common/v2.0
Auth__Audience=b3866040-8bae-4c01-88ba-ecff646df451
Auth__RequiredScope=access_as_user
AccountStorage__TableServiceUri=https://consultologistjobqueue.table.core.windows.net
```

Expected access-token claims (the issuer varies per signing tenant; the
validator accepts any tenant's issuer under the `common` authority — for
personal accounts that tenant is Microsoft's consumer tenant
`9188040d-6c67-4c5b-b112-36a304b66dad`):

```text
ver=2.0
iss=https://login.microsoftonline.com/<tenant-id>/v2.0
aud=b3866040-8bae-4c01-88ba-ecff646df451
scp=access_as_user
```

Accounts are tenant-agnostic: a first sign-in from any organizational tenant
resolves-or-creates an app account exactly like a home-tenant sign-in.

Bearer tokens must not be pasted into logs, issues, or chat. Use decoded claim summaries when debugging.

## Account Statuses and Activation (#191)

New accounts land **`Pending`** (since #191; before that they were created
`Active`, despite docs claiming otherwise). The activation flip in the
`AppUsers` table is the sole admission control for self-provisioned sign-ups.

| Status | Meaning |
|---|---|
| `Pending` | Created on first sign-in; every protected endpoint returns 403 |
| `Active` | Activated by the operator; full access |
| `Disabled` | Deliberately turned off by the operator; 403 everywhere |

Comparison is ordinal and case-sensitive (`AccountAuthorizer.IsActive`), so
the exact strings above are the vocabulary. The one exception to the 403
gate is `GET /api/Account/Me`, which returns the caller's own profile
(including `Status`) for any authenticated account so the client can show an
"awaiting activation" banner instead of a broken app.

Activation runbook (operator's az login holds Storage Table Data Contributor
on `consultologistjobqueue`; find the `RowKey` via the banner user's report
or an entity query on `AppUsers`):

```bash
az storage entity merge \
  --auth-mode login \
  --account-name consultologistjobqueue \
  --table-name AppUsers \
  --entity PartitionKey=app-user RowKey=<AppUserId> Status=Active
```

The same command with `Status=Disabled` turns an account off. Existing rows
created before #191 keep their `Active` status — no migration.

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

The current setting key is:

```text
consult.workflowPackage
```

It holds the account's workflow-package pin (`name` or `name@version`), resolved server-side at job start; when unset, the `WorkflowPackages__Default` app setting (`general@latest`) applies. `GET` returns `404` when the setting has not been saved. `PUT` accepts a JSON body with `value` and `contentType`. `DELETE` removes the pin and restores the default resolution.

Historical: the original key, `consult.sectionStandardsMarkdown` (a per-account section-standards override), retired 2026-07-15 with the specVersion-5 input model (#71) — section standards are now package data, and account customization is package forking (the in-app editor, #57; see docs/customizable-workflow/in-app-editing.md).

## LinkedIn Linking (#133, implemented)

LinkedIn is an account-**verification** signal, explicitly not a login
provider — that would mean re-platforming auth, rejected in favor of
Microsoft remaining the only credential authority. The Connect LinkedIn
button on the Profile page runs LinkedIn's Sign-In-with-LinkedIn OpenID
Connect code flow through two server-side Functions endpoints
(`AccountLinkedIn.cs`); the client secret lives in Function App settings
(`docs/CONFIGURATION.md`, "LinkedIn identity linking") and never reaches the
browser.

Flow:

1. `GET /api/Account/LinkedIn/Start` (bearer-authed; **no IsActive gate** —
   linking is an input to activating a `Pending` account): validates the
   browser `Origin` against the CORS allow-list, writes a single-use state
   row (`LinkedInLinkStates` table: CSPRNG state + nonce, the app user id,
   the return origin, 10-minute expiry) and returns the LinkedIn
   authorization URL as JSON; the client navigates top-level.
2. The user consents on linkedin.com (scopes `openid profile email`;
   personal LinkedIn credentials never touch this app).
3. `GET /api/Account/LinkedIn/Callback` (anonymous — the browser arrives by
   redirect): consumes the state (ETag-conditioned delete; a replayed or
   expired callback gets a 400, not a redirect), exchanges the code
   server-side, validates the id_token (LinkedIn issuer, our client id
   audience; the nonce is sent in the authorize request but validated
   opportunistically — LinkedIn does not echo it into id_tokens, so the
   single-use state is the replay defense), and stores the link. Only the id_token is consumed —
   the access token is discarded and LinkedIn APIs are never called on the
   user's behalf. The browser is then 302'd to
   `{origin}/profile?linkedin=connected` (or `…?linkedin=error&reason=…`).

What is stored (`IdentityLinks` PK `linkedin` / `UserIdentityLinks`): the
stable OIDC `sub` (pairwise per LinkedIn app), issuer
`https://www.linkedin.com/oauth`, and the token's name/email/picture
claims. One LinkedIn identity vouches for at most one app account —
linking an identity already attached to a different account is refused
(`already-linked`). Re-linking the same account is idempotent and
refreshes the claims. `Account/Me` exposes the link in `LinkedIdentities`
so both the user and the operator (reviewing a `Pending` account) can see
it. There is no self-service unlink yet; the operator can delete the two
table rows if needed.

Meaning of "verified", two layers:

1. **Proof of account control** (always): a real, demonstrably
   user-controlled LinkedIn identity — an OAuth proof, never a typed-in
   profile URL.
2. **Verified on LinkedIn categories** (best-effort): the callback also
   calls `GET https://api.linkedin.com/rest/verificationReport` (scope
   `r_verify`, `LinkedIn-Version` per `LinkedIn__ApiVersion`) with the
   just-issued access token and stores the member's verified categories
   (`IDENTITY` = government-ID, `WORKPLACE` = work email/Entra) on the
   link. The app's LinkedIn registration has the self-serve **Verified on
   LinkedIn** product attached, currently the **Development tier**: the
   report is only available for members who are admins of the developer
   app (everyone else gets 403 → categories stay empty, linking still
   succeeds). Apply for the Lite tier before external users.

Both layers are **inputs to the operator's manual activation judgment** —
never an automated gate. That is also a LinkedIn policy requirement: the
Verified on LinkedIn API is licensed for trust enhancement, not for
eligibility decisions, employment screening, or KYC.

## Do Not Do

- Do not store LinkedIn client secrets in Blazor WebAssembly.
- Do not trust browser-side checks as authorization.
- Do not merge accounts based only on matching email addresses.
- Do not treat a linked LinkedIn identity as proof of the profile's claims —
  it proves control of the account, nothing more.
- Do not use mutable profile fields, display names, or email alone as
  permanent account keys (links key on provider + issuer + `sub`).
- Do not accept a LinkedIn identity as a bearer credential — only
  `entra-external-id` identities sign in.

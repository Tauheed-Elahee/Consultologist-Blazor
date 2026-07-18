# Configuration Reference

Environment variables (Function App app settings) read by `Consultologist.Api`, and the
frontend's configuration keys. Inventoried from the code on 2026-07-10.

Note on naming: `__` (double underscore) is .NET's hierarchy separator, so
`Auth__Authority` surfaces as `Auth:Authority` to `IConfiguration`. Settings read
through `IConfiguration` accept either form; settings read directly via
`Environment.GetEnvironmentVariable` (the `AzureAI__*`, `AgentAttestation__*`, and
`WorkflowPackages__Default` groups) must use the exact `__` name.

## Authentication (`Auth/BearerTokenValidator.cs`)

| Variable | Accepted values | Default | Required |
|---|---|---|---|
| `Auth__Authority` | Entra authority URL. Production uses the multi-tenant `https://login.microsoftonline.com/organizations/v2.0` (since 2026-07-18); a tenanted `https://login.microsoftonline.com/<tenant-id>/v2.0` locks sign-in to one tenant. Issuer validation adapts automatically: the validator accepts whatever the authority's OIDC metadata declares, including the `{tenantid}` template the `organizations` endpoint publishes | — | yes (startup throws without it) |
| `Auth__Audience` | Expected token audience, e.g. `api://<client-id>` | — | yes |
| `Auth__RequiredScope` | Scope name callers must carry, e.g. `access_as_user` | none (scope check skipped) | no |

### Multi-tenant sign-in (2026-07-18)

Sign-in accepts any Microsoft Entra organizational tenant. Three settings make
that true, and only one of them lives in this repo's deployment surface:

1. **`signInAudience` = `AzureADMultipleOrgs`** on **both** app registrations
   (the SPA and the API). Portal: App registrations → *Authentication* →
   "Supported account types" (also visible in the registration's Manifest).
   The API being single-tenant is the classic failure: a foreign tenant has
   no service principal for it and sign-in dies with **AADSTS500011**
   ("resource principal not found") — a consent/provisioning problem, never a
   credential one.
2. **`api.knownClientApplications` = `[<spa-client-id>]`** on the **API**
   registration, so a foreign tenant consents to the SPA and the API in one
   combined prompt. Manifest-only — there is no portal blade for it. Do not
   confuse it with the *Expose an API* blade's "Authorized client
   applications" (`preAuthorizedApplications`), which *skips* consent rather
   than bundling it.
3. **`Auth__Authority`** on the Function App set to the `organizations`
   authority (table above). Locally this comes from `local.settings.json`
   (gitignored).

What a foreign tenant still needs: consent. Where user consent is allowed,
the first sign-in shows the combined prompt and provisions both service
principals; tenants that gate consent need their admin to visit
`https://login.microsoftonline.com/<their-tenant-id>/adminconsent?client_id=<spa-client-id>`.

Credential posture is unchanged by any of this: the SPA is a public client
(authorization code + PKCE — client secrets/certificates are never used and
none exist), and the API only validates incoming tokens, reaching storage via
the user-assigned managed identity. Neither registration carries a
credential, so there is nothing to expire or rotate.

A first sign-in from a foreign tenant creates an app account like any other,
and it lands **inactive** — the activation gate is the admission control for
cross-tenant users (see `docs/ACCOUNTS.md`).

## Azure AI Foundry agent (`Agents/AgentSectionGenerator.cs`)

| Variable | Accepted values | Default | Required |
|---|---|---|---|
| `AzureAI__Endpoint` | Foundry project endpoint, e.g. `https://<resource>.services.ai.azure.com/api/projects/<project>` | — | yes (agent calls throw without it) |
| `AzureAI__ApiVersion` | Foundry agents API version | `v1` | no |
| `AzureAI__NetworkTimeoutSeconds` | Non-negative integer (seconds) | `270` | no |
| `AzureAI__MaxRetries` | Non-negative integer (SDK-internal retries per call; durable retries stack on top) | `0` | no |
| `AZURE_CLIENT_ID` | Client id (GUID) of the user-assigned managed identity | — | yes when running in Azure (detected via `WEBSITE_INSTANCE_ID`) |

Agent name/version pins are **not** app settings: they live in the bundled,
git-tracked output-contract catalog (`agents/output-contracts.json`), keyed by output
contract id (`text` = prose default, `concept-list` = structured concepts, schema at
`agents/schemas/concept-list.json`). Selection is catalog-driven per node; every
entry's agent version is recorded in per-job provenance (`agentVersions` map). The
former `AzureAI__AgentName`/`AgentVersion`/`ConceptAgentName`/`ConceptAgentVersion`
settings are retired — delete them from the Function App.

## Agent attestation (`Agents/AgentAttestationService.cs`)

| Variable | Accepted values | Default | Required |
|---|---|---|---|
| `AgentAttestation__Enforce` | `true` (case-insensitive) = drift fails host startup; any other value = drift logs an error only | warn-only | no |
| `AgentAttestation__ManifestDirectory` | Directory holding the attested agent YAMLs and `output-contracts.json` (replaces the former `AgentAttestation__ManifestPath`) | `agents/` under the app base directory | no |

Every output-contract catalog entry is attested: the deployed agent against its git
manifest (`{agent-name}.yaml`, including the `text.format` block — type/name/strict
and canonical-JSON schema comparison), plus the catalog↔manifest schema cross-check
(a catalog entry whose declared schema differs from its agent manifest's is a
startup failure under enforce).

Transient check failures (Foundry unreachable) only warn, even in enforce mode —
only proven disagreement is fatal. A catalog entry with no git manifest is proven
disagreement, not transient.

## Workflow packages (`Workflow/WorkflowPackageStore.cs`, `Workflow/WorkflowPackages.cs`)

| Variable | Accepted values | Default | Required |
|---|---|---|---|
| `WorkflowPackages__BlobServiceUri` | Blob service URI of the **private** registry (acct-* forks), e.g. `https://<account>.blob.core.windows.net` — enables Entra ID auth via the managed identity (reading needs Storage Blob Data Reader; the in-app editor's publish endpoint needs Storage Blob Data **Contributor**) | none (falls back to connection string) | recommended in Azure |
| `WorkflowPackages__PublicBlobServiceUri` | Blob service URI of the **public** registry (repo-owned packages; anonymous read, no credential) — e.g. `https://consultologistpublic.blob.core.windows.net`. Unset → repo-owned packages resolve from the private container AND the output-contract catalog loads from the bundled `agents/` directory (local dev) | none | yes in Azure since #92 |
| `OutputContracts__Pin` | Catalog registry ref: `output-contracts@latest` or `output-contracts@vYYYY.MM.N` — the version loaded at startup and stamped into job records as `catalogRef` (#93). Activating a new catalog = publish + (optionally bump a concrete pin) + restart; no redeploy | `output-contracts@latest` | no |

`GET /api/Public/Chain` (#95) is anonymous with open CORS and requires only `WorkflowPackages__PublicBlobServiceUri`; it 503s when the public registry is unconfigured.
| `WorkflowPackages__ConnectionStringName` | *Name of another setting* holding a storage connection string (local-dev fallback path) | `AzureWebJobsStorage` | no |
| `WorkflowPackages__Default` | Package ref: `name@vYYYY.MM.N` or `name@latest` | `general@latest` | no |

## Storage stores (Azure Tables)

Each store reads a setting *name*, then resolves that named setting as the connection
string. All default to `AzureWebJobsStorage`.

| Variable | Read in |
|---|---|
| `AccountStorage__ConnectionStringName` | `Auth/AccountStore.cs`, `Auth/AccountSettingsStore.cs` (also the fallback name for the two below) |
| `ConsultGenerationJobEventStorage__ConnectionStringName` | `Jobs/ConsultGenerationJobEventStore.cs` |
| `ConsultGenerationJobIndexStorage__ConnectionStringName` | `Jobs/ConsultGenerationJobIndexStore.cs` |

## Platform / runtime (not set by application code)

| Variable | Notes |
|---|---|
| `AzureWebJobsStorage` | Storage connection string; required by the Functions host and Durable Functions, and the default for every store above. Locally: `UseDevelopmentStorage=true` (Azurite). |
| `FUNCTIONS_WORKER_RUNTIME` | Must be `dotnet-isolated`. |
| `WEBSITE_INSTANCE_ID` | Provided by Azure; the code only reads it to detect "running in Azure". Never set manually. |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Telemetry destination. |

## Legacy settings

None. The assistants-era leftovers (`AzureAI__AgentId__old`, `AzureAI__Endpoint__old`,
`AzureAI__ApiVersion__old`) and the retired agent pin settings
(`AzureAI__AgentName`/`AgentVersion`/`ConceptAgentName`/`ConceptAgentVersion`,
replaced by the output-contract catalog) were deleted from the Function App
2026-07-15.

## Frontend (`src/Consultologist.Web/wwwroot/appsettings.json`)

The Blazor WASM app is configured by this JSON file (bundled, per-environment overrides
via `appsettings.Development.json`), not by environment variables:

- `AzureAd:Authority`, `AzureAd:ClientId`, `AzureAd:ValidateAuthority` — MSAL sign-in.
- `AzureFunction:ApiScope` — scope requested for API tokens.
- `AzureFunction:*Url` — endpoint URLs: `AccountMeUrl`, `ConsultGenerationJobsUrl`,
  `DiagnosticsSseExitUrl`, `WorkflowPackageCurrentUrl`, and the editor pair
  `WorkflowPackageContentUrl` / `WorkflowPackagePublishUrl` (#57),
  `WorkflowPackageLineageUrl` (#89), and `WorkflowPackageDiagramUrl` (#114). (`AgentProxyUrl`
  and `ConsultGenerationUrl` were removed with their legacy endpoints in milestone 3.)
- `AzureFunction:TimeoutSeconds` — HTTP client timeout for AI calls (default 240 when
  absent; shipped value 300).

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
| `Auth__Authority` | Entra authority URL. Production uses `https://login.microsoftonline.com/common/v2.0` (since 2026-07-23, #132 — includes personal Microsoft accounts); `…/organizations/v2.0` restricts to work/school tenants; a tenanted `…/<tenant-id>/v2.0` locks sign-in to one tenant. Issuer validation adapts automatically: the validator accepts whatever the authority's OIDC metadata declares, including the `{tenantid}` template the `common`/`organizations` endpoints publish | — | yes (startup throws without it) |
| `Auth__Audience` | Expected token audience, e.g. `api://<client-id>` | — | yes |
| `Auth__RequiredScope` | Scope name callers must carry, e.g. `access_as_user` | none (scope check skipped) | no |

### Multi-tenant sign-in (2026-07-18; personal accounts 2026-07-23)

Sign-in accepts any Microsoft Entra organizational tenant and, since #132,
personal Microsoft accounts. Three settings make that true, and only one of
them lives in this repo's deployment surface:

1. **`signInAudience` = `AzureADandPersonalMicrosoftAccount`** on **both**
   app registrations (the SPA and the API); `AzureADMultipleOrgs` is the
   work/school-only variant used 2026-07-18 → 2026-07-23. Portal: App
   registrations → *Authentication* → "Supported account types" (also
   visible in the registration's Manifest). Two MSA-specific constraints:
   Entra refuses the widening unless the registration's
   `api.requestedAccessTokenVersion` is `2` (the SPA registration had it
   unset and needed a Graph PATCH first), and identifier URIs must be the
   `api://<client-id>` form (already the case). The API being single-tenant
   is the classic failure: a foreign tenant has no service principal for it
   and sign-in dies with **AADSTS500011** ("resource principal not found") —
   a consent/provisioning problem, never a credential one.
2. **`api.knownClientApplications` = `[<spa-client-id>]`** on the **API**
   registration, so a foreign tenant consents to the SPA and the API in one
   combined prompt. Manifest-only — there is no portal blade for it. Do not
   confuse it with the *Expose an API* blade's "Authorized client
   applications" (`preAuthorizedApplications`), which *skips* consent rather
   than bundling it.
3. **`Auth__Authority`** on the Function App set to the `common` authority
   (table above), and the client's `AzureAd:Authority`
   (`src/Consultologist.Web/wwwroot/appsettings.json`) set to
   `https://login.microsoftonline.com/common` — MSAL takes the authority
   without `/v2.0`; the API's OIDC-metadata URL takes it with. Locally the
   Function value comes from `local.settings.json` (gitignored).

What a foreign tenant still needs: consent. Where user consent is allowed,
the first sign-in shows the combined prompt and provisions both service
principals; tenants that gate consent need their admin to visit
`https://login.microsoftonline.com/<their-tenant-id>/adminconsent?client_id=<spa-client-id>`.
Personal Microsoft accounts have no tenant admin — the user always consents
for themselves, which is exactly why this path exists as the fallback for
users whose IT departments block consent (#132).

CSP note: MSA sign-in can bounce through `login.live.com`, but only via
top-level navigation (redirect login mode), which CSP does not restrict —
`staticwebapp.config.json` needs no new origins. Token endpoints and silent
renewal stay on `login.microsoftonline.com`, already in `connect-src`.

Credential posture is unchanged by any of this: the SPA is a public client
(authorization code + PKCE — client secrets/certificates are never used and
none exist), and the API only validates incoming tokens, reaching storage via
the user-assigned managed identity. Neither registration carries a
credential, so there is nothing to expire or rotate.

A first sign-in from a foreign tenant creates an app account like any other,
and it lands **`Pending`** (since #191) — the activation flip in the
`AppUsers` table is the admission control for self-provisioned sign-ups (see
"Account Statuses and Activation" in `docs/ACCOUNTS.md` for the runbook).

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
| `AgentAttestation__Enforce` | `true` (case-insensitive) = drift fails host startup; any other value = drift logs an error only. Since #16 the production baseline is the registry's published definition (CI-only channel); the submodule-pinned copy (`external/consultologist-agents/agents`, bundled into the build output) is the baseline only in local dev | warn-only | no |
| `AgentAttestation__ManifestDirectory` | Directory holding the attested agent YAMLs and `output-contracts.json` (replaces the former `AgentAttestation__ManifestPath`) | `agents/` under the app base directory (populated at build from the `external/consultologist-agents` submodule) | no |

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
| `OutputContracts__Pin` | Catalog registry ref: `output-contracts@latest` or `output-contracts@vYYYY.MM.N` — the version loaded at startup and stamped into job records as `catalogRef` (#93). Activating a new catalog = publish + bump the concrete pin + restart; production pins explicitly (set 2026-07-23) so catalog releases never activate implicitly | `output-contracts@latest` | no |

`GET /api/Public/Chain` (#95) is anonymous with open CORS and requires only `WorkflowPackages__PublicBlobServiceUri`; it 503s when the public registry is unconfigured.
| `WorkflowPackages__ConnectionStringName` | *Name of another setting* holding a storage connection string (local-dev fallback path) | `AzureWebJobsStorage` | no |
| `WorkflowPackages__Default` | Package ref: `name@vYYYY.MM.N` or `name@latest` | `general@latest` | no |

## Storage stores (Azure Tables)

Entra ID first (#10, mirroring the workflow-package registry): when a
`…__TableServiceUri` setting is present the store authenticates as the app's
managed identity (the `AZURE_CLIENT_ID` user-assigned identity needs
**Storage Table Data Contributor** on the account). The named connection
string remains only as the local-dev (Azurite) fallback.

| Variable | Read in |
|---|---|
| `AccountStorage__TableServiceUri` | `Auth/AccountStore.cs`, `Auth/AccountSettingsStore.cs` (also the fallback URI for the two below). Production: `https://consultologistjobqueue.table.core.windows.net` |
| `ConsultGenerationJobEventStorage__TableServiceUri` | `Jobs/ConsultGenerationJobEventStore.cs` (optional override) |
| `ConsultGenerationJobIndexStorage__TableServiceUri` | `Jobs/ConsultGenerationJobIndexStore.cs` (optional override) |
| `AccountStorage__ConnectionStringName` | Local-dev fallback name (default `AzureWebJobsStorage`); same chain as before for the two job stores |

## Platform / runtime (not set by application code)

| Variable | Notes |
|---|---|
| `AzureWebJobsStorage` | Local-dev only (`UseDevelopmentStorage=true`, Azurite). In production the host and Durable Functions use the identity-based form instead: `AzureWebJobsStorage__accountName` + `__blobServiceUri`/`__queueServiceUri`/`__tableServiceUri` + `__credential=managedidentity` + `__clientId` (the user-assigned identity, which needs Storage Blob Data Owner, Queue Data Contributor, and Table Data Contributor on the account). Shared-key access is disabled on `consultologistjobqueue` (#10). |
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
  `WorkflowPackageLineageUrl` (#89), `WorkflowPackageDiagramUrl` (#114),
  `WorkflowPackageMineUrl` (#134 — the package selector's fork listing), and
  `WorkflowPackageDiagramPreviewUrl` (#144 — POST a manifest, get its diagram;
  the editor's pending-edits graph preview). (`AgentProxyUrl`
  and `ConsultGenerationUrl` were removed with their legacy endpoints in milestone 3.)
- `AzureFunction:TimeoutSeconds` — HTTP client timeout for AI calls (default 240 when
  absent; shipped value 300).

## Static Web App staging environments (#156)

Every PR gets a staging environment on the SWA (`consultologist-blazor`);
the workflow's close job removes it when the PR closes. The historical
leak was a race, not a close failure: a still-in-flight PR build could
finish *after* the close job and re-create the environment. The workflow's
`concurrency` group (one run per branch, newest wins, superseded builds
canceled) makes that impossible — the close-event run is always the
newest in its branch group.

A weekly scheduled sweep (`.github/workflows/swa-staging-sweep.yml`,
Mondays 09:00 UTC, also runnable via workflow_dispatch) deletes any
staging environment whose PR is no longer open — it authenticates via
GitHub OIDC as `consultologist-blazor-swa-sweep` (Contributor scoped to
the SWA resource only) and never touches `default` (#182).

Manual sweep, only if the scheduled one is unavailable (also mind the
10-environment cap):

```bash
az staticwebapp environment list -n consultologist-blazor \
  --query "[].{name:name, source:sourceBranch}" -o table
az staticwebapp environment delete -n consultologist-blazor \
  --environment-name <name> --yes
```

An automated no-open-PR sweep becomes a trivial scheduled job once #16
establishes GitHub→Azure OIDC for CI.

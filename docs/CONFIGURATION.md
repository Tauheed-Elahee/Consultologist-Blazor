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
| `Auth__Authority` | Entra authority URL, e.g. `https://login.microsoftonline.com/<tenant-id>/v2.0` | — | yes (startup throws without it) |
| `Auth__Audience` | Expected token audience, e.g. `api://<client-id>` | — | yes |
| `Auth__RequiredScope` | Scope name callers must carry, e.g. `access_as_user` | none (scope check skipped) | no |

## Azure AI Foundry agent (`Agents/AgentSectionGenerator.cs`)

| Variable | Accepted values | Default | Required |
|---|---|---|---|
| `AzureAI__Endpoint` | Foundry project endpoint, e.g. `https://<resource>.services.ai.azure.com/api/projects/<project>` | — | yes (agent calls throw without it) |
| `AzureAI__AgentName` | Prose agent name in the Foundry project (currently `test-json`) | — | yes |
| `AzureAI__AgentVersion` | Published prose agent version as a string (currently `47`) | — | yes |
| `AzureAI__ConceptAgentName` | Structured-output agent for the concept analysis stages (currently `concept-extraction`; published with a `json_schema` text format — see `agents/concept-extraction.yaml`) | — | yes (analysis activities throw without it) |
| `AzureAI__ConceptAgentVersion` | Published concept agent version as a string (currently `1`); recorded in per-job provenance | — | yes |
| `AzureAI__ApiVersion` | Foundry agents API version | `v1` | no |
| `AzureAI__NetworkTimeoutSeconds` | Non-negative integer (seconds) | `270` | no |
| `AzureAI__MaxRetries` | Non-negative integer (SDK-internal retries per call; durable retries stack on top) | `0` | no |
| `AZURE_CLIENT_ID` | Client id (GUID) of the user-assigned managed identity | — | yes when running in Azure (detected via `WEBSITE_INSTANCE_ID`) |

## Agent attestation (`Agents/AgentAttestationService.cs`)

| Variable | Accepted values | Default | Required |
|---|---|---|---|
| `AgentAttestation__Enforce` | `true` (case-insensitive) = drift fails host startup; any other value = drift logs an error only | warn-only | no |
| `AgentAttestation__ManifestDirectory` | Directory holding the attested agent YAMLs (`{agent-name}.yaml` per pinned agent; replaces the former `AgentAttestation__ManifestPath`) | `agents/` under the app base directory | no |

Every pinned agent is attested: `AzureAI__AgentName`/`AgentVersion` and, when
configured, `AzureAI__ConceptAgentName`/`ConceptAgentVersion` — including the
`text.format` block (type/name/strict and canonical-JSON schema comparison).

Transient check failures (Foundry unreachable, manifest missing) only warn, even in
enforce mode — only proven disagreement is fatal.

## Workflow packages (`Workflow/WorkflowPackageStore.cs`, `Workflow/WorkflowPackages.cs`)

| Variable | Accepted values | Default | Required |
|---|---|---|---|
| `WorkflowPackages__BlobServiceUri` | Blob service URI, e.g. `https://<account>.blob.core.windows.net` — enables Entra ID auth via the managed identity (needs Storage Blob Data Reader) | none (falls back to connection string) | recommended in Azure |
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

## Legacy settings present in the deployed app but no longer read

`AzureAI__AgentId__old`, `AzureAI__Endpoint__old`, `AzureAI__ApiVersion__old` — leftovers
from the assistants-era integration; safe to delete from the Function App configuration.

## Frontend (`src/Consultologist.Web/wwwroot/appsettings.json`)

The Blazor WASM app is configured by this JSON file (bundled, per-environment overrides
via `appsettings.Development.json`), not by environment variables:

- `AzureAd:Authority`, `AzureAd:ClientId`, `AzureAd:ValidateAuthority` — MSAL sign-in.
- `AzureFunction:ApiScope` — scope requested for API tokens.
- `AzureFunction:*Url` — endpoint URLs: `AccountMeUrl`, `ConsultGenerationJobsUrl`,
  `DiagnosticsSseExitUrl`, `WorkflowPackageCurrentUrl`. (`AgentProxyUrl` and
  `ConsultGenerationUrl` were removed with their legacy endpoints in milestone 3.)
- `AzureFunction:TimeoutSeconds` — HTTP client timeout for AI calls (default 240 when
  absent; shipped value 300).

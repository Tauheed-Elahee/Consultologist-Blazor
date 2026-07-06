# AGENTS.md

This file provides guidance to AI coding agents working with code in this repository.

## Commands

```bash
dotnet build Consultologist.sln          # build all three projects
dotnet test                              # run the xUnit suite (tests/)
dotnet test --filter FunctionCorsTests   # run a single test class
dotnet run --project src/Consultologist.Web   # frontend on http://localhost:5000
cd src/Consultologist.Api && func start       # backend (Azure Functions Core Tools)
```

Prerequisites: .NET 10 SDK (pinned in `global.json`) and the WASM workload (`dotnet workload install wasm-tools`).

All `bin`/`obj` output is centralized to `build/bin|obj/<ProjectName>/` via `Directory.Build.props` — project folders never contain build artifacts. NuGet versions are centralized in `Directory.Packages.props` (central package management; `Version=` on a `PackageReference` will not build).

## Architecture

Two independently deployed applications in one solution, plus a test project:

- **`src/Consultologist.Web`** — standalone Blazor WebAssembly PWA (Fluent UI). Auth is Microsoft Entra ID via MSAL (`Microsoft.Authentication.WebAssembly.Msal`); config lives in `wwwroot/appsettings.json`. Deployed to Azure Static Web Apps.
- **`src/Consultologist.Api`** — .NET 10 isolated Azure Functions. Deployed to a separate Azure Function App (the SWA's `api_location` is intentionally empty); the frontend calls it cross-origin, so every HTTP function applies `FunctionCors` manually and new endpoints must too.
- **`tests/`** — xUnit + NSubstitute against the Api project. `Consultologist.Api.csproj` grants `InternalsVisibleTo` to it.

### Consult generation flow (the core feature)

`Jobs/ConsultGenerationJobs.cs` (~2,700 lines) holds the Durable Functions pipeline: an HTTP starter, an orchestrator, per-section activities, and a durable entity tracking job state. Activities call `Agents/AgentSectionGenerator`, which drives Azure AI Foundry agents. Progress events are persisted to Azure Table Storage (`Jobs/ConsultGenerationJobEventStore`, `...JobIndexStore`) so the browser can stream them via SSE **with resume support** (`Last-Event-ID`) — see `docs/SSE_RESUME.md` and `docs/CONSULT_GENERATION_EVENTS.md`. `Agents/ConsultGeneration.cs` is the older direct (non-durable) SSE endpoint over the same generator. The frontend consumes streams through `Services/AI/AIEndpointService`.

### Auth chain (Api)

`Auth/BearerTokenValidator` (Entra JWT validation) → `Auth/AccountAuthorizer` → `Auth/AccountStore` (Azure Tables: app users + provider identity links). HTTP functions resolve the account per request; `AccountAuthorizer.IsActive` gates disabled accounts.

### Conventions and constraints

- Namespaces mirror folders (`Consultologist.Api.Jobs`, `Consultologist.Web.Services.AI`, …).
- `[Function("Name")]` strings and durable orchestrator/entity/activity class names are the deployed contract — renaming a function string or an activity class affects the live Function App and any in-flight durable orchestrations; namespaces are safe to change.
- The scoped-CSS bundle is named after the assembly: `wwwroot/index.html` links `Consultologist.Web.styles.css`. Renaming the Web project breaks this link unless index.html is updated.
- CI is path-filtered: the Function App deploys only on `src/Consultologist.Api/**` changes; the SWA deploy ignores api/tests/docs/markdown changes but builds previews for every PR. Tests run in their own workflow.
- Design docs live in `docs/` (indexed by `docs/README.md`); `docs/research/` is historical point-in-time material — don't update it to match later refactors.

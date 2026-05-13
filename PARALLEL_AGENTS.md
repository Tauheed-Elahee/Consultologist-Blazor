# Parallel Section Calls, Backend Parallelism, Async Jobs

## Summary

This document captures the planned performance path for consult section generation.

The first improvement is to keep the current single-section Azure Function contract and run all section calls concurrently from the Blazor app. The next step is backend parallelism: the Blazor app sends one consult-generation request, and the backend fans out section calls concurrently before returning one combined response. The later architecture, if needed, is an async job-based generation flow that moves long-running work out of browser-held HTTP requests.

## Current State

- `Pages/Consults.razor` now calls `IAIEndpointService.GenerateConsultAsync(...)` once per consult.
- `IAIEndpointService.GenerateConsultAsync(...)` sends one aggregate request to `Api/ConsultGeneration`.
- `Api/ConsultGeneration` validates the draft and sections, fans out one Foundry call per section concurrently, and returns generated and failed sections keyed by section ID.
- `IAIEndpointService.InvokeAgentAsync(...)` and `Api/AgentProxy` remain available for compatibility with one-section callers.
- `Api/AgentProxy` and `Api/ConsultGeneration` both use `AgentSectionGenerator` for prompt construction and Foundry invocation.
- Recent logs show individual section latency is usually around 3-12 seconds.
- Total consult time is now mostly bounded by the slowest section call plus aggregate request overhead, while still carrying synchronous HTTP timeout risk.

## Phase 1: Parallel Section Calls

Implement this first.

- Keep `Api/AgentProxy` and `IAIEndpointService.InvokeAgentAsync(...)` as single-section APIs.
- Update `CreateConsultAsync` to start one task for each item in `sectionStandards`.
- Await all section tasks with `Task.WhenAll`.
- Preserve rendered note order by continuing to render in `sectionStandards` order and storing results in `generatedSections[section.Id]`.
- Track per-section state:
  - `pending`
  - `running`
  - `completed`
  - `failed`
- Show progress as `Generating X/Y sections...` instead of only showing one active section name.
- If one section fails, keep successful sections and show a concise error listing failed section names.
- Continue calling Foundry for every section. Do not add rule-based empty-section skips in this phase.
- Do not add backend parallelism in this phase. The browser launches all section requests concurrently.

## Phase 2: Backend Parallelism

Implement this after Phase 1 if the browser-side fan-out works but the app should use one frontend request per consult.

- Add a backend API that accepts the consult draft plus all section standards in a single request.
- Keep the existing single-section `Api/AgentProxy` contract available for compatibility while adding the aggregate backend endpoint.
- The backend endpoint should start one Foundry call per section concurrently and await them with `Task.WhenAll`.
- Return one combined response containing completed sections and failed sections keyed by section ID.
- Preserve partial results: one failed section should not discard successful sections.
- Preserve rendered note order in the Blazor page by continuing to render using template order and section IDs.
- Move per-section progress and failure tracking to the response shape returned by the backend endpoint.
- The Blazor page should call the aggregate backend endpoint once per consult instead of launching one browser request per section.
- Do not add durable job storage, polling, cancellation endpoints, or background workers in this phase. The frontend still waits on one HTTP request.
- This phase reduces browser request fan-out and centralizes concurrency control, logging, and retry behavior. It does not solve long-running HTTP timeout risk.

### Implemented API Structure

The API now has two HTTP entry points that share the same Foundry generation service.

Primary consult-generation path:

```text
Blazor UI
  Pages/Consults.razor
        |
        | one request with all sections
        v
  IAIEndpointService.GenerateConsultAsync(...)
        |
        v
POST /api/ConsultGeneration
  Api/ConsultGeneration.cs
        |
        | validates draft + sections
        | starts one task per section
        | awaits Task.WhenAll(...)
        v
  AgentSectionGenerator.GenerateSectionAsync(...)
        |
        | builds prompt
        | calls Azure AI Foundry agent
        v
Azure AI Foundry Agent
```

Compatibility path:

```text
Any existing caller
        |
        | one request for one section
        v
  IAIEndpointService.InvokeAgentAsync(...)
        |
        v
POST /api/AgentProxy
  Api/AgentProxy.cs
        |
        v
  AgentSectionGenerator.GenerateSectionAsync(...)
        |
        v
Azure AI Foundry Agent
```

Expanded structure:

```text
BlazorWasm
├─ Pages/Consults.razor
│  └─ calls GenerateConsultAsync once per consult
│
└─ Services/AI/AIEndpointService.cs
   ├─ GenerateConsultAsync(...)
   │  └─ POST AzureFunction:ConsultGenerationUrl
   │
   └─ InvokeAgentAsync(...)
      └─ POST AzureFunction:AgentProxyUrl

Azure Functions API
├─ ConsultGeneration.cs
│  ├─ endpoint: POST /api/ConsultGeneration
│  ├─ accepts: ConsultGenerationRequest
│  ├─ fans out all sections concurrently
│  └─ returns: ConsultGenerationResponse
│
├─ AgentProxy.cs
│  ├─ endpoint: POST /api/AgentProxy
│  ├─ accepts: AgentSectionRequest
│  └─ returns: AgentResponse
│
├─ AgentSectionGenerator.cs
│  ├─ shared service used by both endpoints
│  ├─ builds the oncology section prompt
│  ├─ configures Azure AI Project client
│  └─ invokes Foundry agent
│
└─ Models
   ├─ AgentSectionRequest
   ├─ AgentResponse
   ├─ ConsultGenerationRequest
   ├─ ConsultGenerationSectionRequest
   └─ ConsultGenerationResponse
```

The key change is that `ConsultGeneration` owns backend parallelism, while `AgentSectionGenerator` owns the actual single-section Foundry call. `AgentProxy` remains a thin compatibility endpoint for callers that still generate one section at a time.

## Phase 3: Async Job-Based Generation

Implement this later only if direct parallel calls still hit timeout or reliability limits.

- Add a backend job API that starts generation and returns quickly.
- Proposed future endpoints:
  - `POST /api/ConsultGenerationJobs`
  - `GET /api/ConsultGenerationJobs/{jobId}`
  - optional later endpoint: `POST /api/ConsultGenerationJobs/{jobId}/cancel`
- `POST /api/ConsultGenerationJobs` accepts the consult draft plus all section standards and returns `{ jobId }`.
- `GET /api/ConsultGenerationJobs/{jobId}` returns job status, progress, completed sections, and failed sections.
- Store job state in Azure Storage using the existing Function app storage account.
- Job status should support:
  - `queued`
  - `running`
  - `completed`
  - `failed`
  - `canceled`
- Section state should remain keyed by section ID so the UI can keep using the same rendering model.
- The job worker may internally generate all sections concurrently, matching the Phase 2 backend fan-out behavior.
- The Blazor page should eventually switch from direct `InvokeAgentAsync(...)` calls to:
  - start job
  - poll status
  - merge completed section results into `generatedSections`
- This later phase improves timeout resilience, refresh recovery, cancellation, and retry behavior. It does not inherently make the model faster.

### Async Job-Based Architecture

The async job-based architecture changes HTTP from the unit of work into a control plane for creating and inspecting durable generation jobs.

Job creation:

```text
Blazor UI
  |
  | POST /api/ConsultGenerationJobs
  | draft + all sections
  v
CreateGenerationJob Function
  |
  | creates job record
  | stores section work items
  | returns immediately
  v
Job Store
  |
  | HTTP 202 Accepted
  | { jobId }
  v
Blazor UI
```

Background generation:

```text
Background Worker / Queue Trigger / Durable Orchestrator
  |
  | loads job + pending sections
  v
Section Generation Workers
  |
  | concurrent Foundry calls
  v
AgentSectionGenerator
  |
  v
Azure AI Foundry Agent
  |
  v
Job Store updated per section
```

Progress polling:

```text
Blazor UI
  |
  | GET /api/ConsultGenerationJobs/{jobId}
  v
GetGenerationJob Function
  |
  | reads current job state
  v
Job Store
  |
  | returns status, completed count,
  | generated sections, failed sections
  v
Blazor UI updates progress/results
```

Optional cancellation:

```text
Blazor UI
  |
  | POST /api/ConsultGenerationJobs/{jobId}/cancel
  v
CancelGenerationJob Function
  |
  | marks job as cancelled
  v
Worker stops pending work
```

Future async API structure:

```text
Azure Functions API
├─ POST /api/ConsultGenerationJobs
│  └─ creates job, stores draft/sections, queues work
│
├─ GET /api/ConsultGenerationJobs/{jobId}
│  └─ returns job status + generated/failed sections
│
├─ POST /api/ConsultGenerationJobs/{jobId}/cancel
│  └─ optional cancellation endpoint
│
├─ QueueTrigger / Durable Function / Background Worker
│  └─ processes sections concurrently
│
├─ AgentSectionGenerator
│  └─ unchanged shared Foundry caller
│
└─ Job Store
   ├─ job metadata
   ├─ section statuses
   ├─ generated section text
   └─ failed section errors
```

Proposed job state model:

```text
ConsultGenerationJob
├─ JobId
├─ Status: Queued | Running | Completed | Failed | Cancelled
├─ TotalSectionCount
├─ CompletedSectionCount
├─ FailedSectionCount
├─ CreatedAt
├─ UpdatedAt
└─ Sections
   ├─ Id
   ├─ Name
   ├─ Status: Pending | Running | Completed | Failed
   ├─ GeneratedText
   └─ Error
```

## Architecture Comparison

Phase 1 browser fan-out:

```text
Blazor UI
  |-- POST /api/AgentProxy section A --> Foundry
  |-- POST /api/AgentProxy section B --> Foundry
  |-- POST /api/AgentProxy section C --> Foundry
  |
  v
Browser coordinates progress/results
```

Phase 2 synchronous backend fan-out:

```text
Blazor UI
  |
  | POST /api/ConsultGeneration
  v
Function
  |-- section A --> Foundry
  |-- section B --> Foundry
  |-- section C --> Foundry
  |
  v
One final HTTP response
```

Phase 3 async job-based generation:

```text
Blazor UI
  |
  | POST /api/ConsultGenerationJobs
  v
Job created, jobId returned immediately

Background workers
  |-- section A --> Foundry --> update job store
  |-- section B --> Foundry --> update job store
  |-- section C --> Foundry --> update job store

Blazor UI
  |
  | GET /api/ConsultGenerationJobs/{jobId}
  v
Progress + partial results returned repeatedly
```

Request lifecycle comparison:

```text
Phase 1
Browser opens one HTTP request per section.
Browser waits for and coordinates every section response.
```

```text
Phase 2
Browser waits open on one HTTP request.
Function holds that request open.
Backend does concurrent section generation.
Result returns once everything is done.
```

```text
Phase 3
Browser starts a durable job.
Background workers perform generation outside the original HTTP request.
Browser polls or subscribes for job status and partial results.
```

High-level architecture difference:

```text
Current Phase 2
Blazor -> Function -> Foundry -> Function -> Blazor
```

```text
Async Job-Based
Blazor -> Function -> Job Store -> Worker -> Foundry
   ^                    |
   |                    v
   +---- Poll/SSE ---- Job Status
```

The major conceptual change is that HTTP stops being the unit of work. In Phase 2, the request is the job. In the async architecture, the job is durable state, and HTTP is only used to create, inspect, and optionally cancel that job.

## Test Plan

- Verify section results still render in template order even when responses complete out of order.
- Verify failed sections do not erase successful sections.
- Verify progress count updates as each section completes.
- Verify all section requests are launched concurrently.
- Manually run full consult generation with the 9 default sections.
- Confirm browser logs show overlapping Function calls.
- Confirm total time is lower than sequential generation.
- Confirm Function logs still show one request per section.
- Run:

```bash
dotnet build Api/Api.csproj --no-restore
dotnet build BlazorWasm.csproj --no-restore
```

## Assumptions

- First implementation should optimize for low-risk speed improvement, not architectural redesign.
- Phase 1 should launch all configured sections concurrently.
- Partial results should be preserved if one section fails.
- Rule-based section skipping is out of scope for the first implementation.
- Backend parallelism is Phase 2 and should keep a synchronous request/response flow.
- Async/job-based generation is Phase 3 and should be reserved for timeout resilience, refresh recovery, cancellation, and retry needs.

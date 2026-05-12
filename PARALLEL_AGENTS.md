# Parallel Section Calls Now, Async Jobs Later

## Summary

This document captures the planned performance path for consult section generation.

The first improvement is to keep the current single-section Azure Function contract and run all section calls concurrently from the Blazor app. The later architecture, if needed, is an async job-based generation flow that moves long-running work out of browser-held HTTP requests.

## Current State

- `Pages/Consults.razor` currently generates sections sequentially in `CreateConsultAsync`.
- `IAIEndpointService.InvokeAgentAsync(...)` sends one section request to `Api/AgentProxy`.
- `Api/AgentProxy` calls Foundry for one section and returns `{ response, error, success }`.
- Recent logs show individual section latency is usually around 3-12 seconds.
- Total consult time is currently mostly the sum of all sequential section calls.

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

## Phase 2: Async Job-Based Generation

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
- The job worker may internally generate all sections concurrently, matching the Phase 1 browser behavior.
- The Blazor page should eventually switch from direct `InvokeAgentAsync(...)` calls to:
  - start job
  - poll status
  - merge completed section results into `generatedSections`
- This later phase improves timeout resilience, refresh recovery, cancellation, and retry behavior. It does not inherently make the model faster.

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
- Async/job-based generation is a future roadmap item, not part of the first parallel-calls implementation.

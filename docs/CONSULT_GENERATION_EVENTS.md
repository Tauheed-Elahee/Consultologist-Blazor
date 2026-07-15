# Consult Generation Event Flow

> **Updated for Milestone 5 (2026-07-15)**: the engine executes one node kind with
> per-(node, item) scheduling. The event *surface* is unchanged — `node-completed`
> fires per node-level completion (a forEach node completes when its last item
> settles), and `section-prose-step` events are synthesized from per-item node
> completions with identical payloads (step ids = the forEach chain's node ids).

This is the current consult generation flow after the .NET 10 SSE migration.

```mermaid
sequenceDiagram
    autonumber

    actor User
    participant Page as Pages/Consults.razor
    participant Client as AIEndpointService
    participant Jobs as Api/ConsultGenerationJobs
    participant Durable as Durable Task
    participant Entity as ConsultGenerationJobEntity
    participant Orchestrator as ConsultGenerationOrchestrator
    participant Activity as run-prompt-node / run-prose-step activities
    participant Agent as AgentSectionGenerator
    participant SSE as /events SSE stream

    User->>Page: Click "Create consult"
    Page->>Page: Snapshot draft and section standards
    Page->>Client: StartConsultGenerationJobAsync(draft, sections)
    Client->>Jobs: POST /api/ConsultGenerationJobs
    Jobs->>Entity: Signal Initialize(jobId, sections)
    Jobs->>Durable: Schedule ConsultGenerationOrchestrator(instanceId = jobId)
    Jobs-->>Client: 202 { jobId, statusUrl }
    Client-->>Page: ConsultGenerationJobStartResponse

    Page->>Client: StreamConsultGenerationJobEventsAsync(jobId)
    Client->>SSE: GET /api/ConsultGenerationJobs/{jobId}/events
    SSE->>Jobs: GetEventsAsync(jobId)
    Jobs->>Durable: Confirm job exists
    Jobs-->>Client: text/event-stream starts

    Durable->>Orchestrator: RunAsync(request)
    Orchestrator->>Entity: Initialize(jobId, sections)
    Orchestrator->>Entity: MarkRunning()
    Orchestrator->>Durable: Set custom status Running

    par One task per section
        Orchestrator->>Activity: CallActivityAsync(section A)
        Activity->>Agent: GenerateSectionAsync(draft, name, standard)
        Agent-->>Activity: Generated prose
        Activity-->>Orchestrator: SectionGenerationResult(success)
    and Other sections
        Orchestrator->>Activity: CallActivityAsync(section B...)
        Activity->>Agent: GenerateSectionAsync(...)
        Agent-->>Activity: Generated prose or error
        Activity-->>Orchestrator: SectionGenerationResult(success/failure)
    end

    Jobs->>Entity: Wait/read initial entity state
    Jobs-->>SSE: event: snapshot
    SSE-->>Client: snapshot JSON
    Client-->>Page: ConsultGenerationJobSseEvent("snapshot", json)
    Page->>Page: ApplyConsultGenerationJobResponse()

    loop Until terminal status or 5 minute stream timeout
        Jobs->>Entity: Poll job response every 1 second

        alt New completed section found
            Orchestrator->>Entity: CompleteSection(result)
            Orchestrator->>Durable: Set custom status Running
            Jobs-->>SSE: event: section-completed
            SSE-->>Client: section-completed JSON
            Client-->>Page: ConsultGenerationJobSseEvent("section-completed", json)
            Page->>Page: Add generated section text
        else New failed section found
            Orchestrator->>Entity: FailSection(result)
            Orchestrator->>Durable: Set custom status Running
            Jobs-->>SSE: event: section-failed
            SSE-->>Client: section-failed JSON
            Client-->>Page: ConsultGenerationJobSseEvent("section-failed", json)
            Page->>Page: Add failed section error
        else No section change for heartbeat interval
            Jobs-->>SSE: event: heartbeat
            SSE-->>Client: heartbeat JSON
            Client-->>Page: ConsultGenerationJobSseEvent("heartbeat", json)
        end
    end

    Orchestrator->>Entity: FinalizeJob(Completed or Failed)
    Orchestrator->>Durable: Set final custom status
    Jobs->>Entity: Poll terminal job response
    Jobs-->>SSE: event: done
    SSE-->>Client: done JSON
    Client-->>Page: ConsultGenerationJobSseEvent("done", json)
    Page->>Page: Apply final response and stop loading

    alt Stream setup, stream error, server error event, or timeout
        Jobs-->>SSE: event: error
        SSE-->>Client: error JSON
        Client-->>Page: ConsultGenerationJobSseEvent("error", json)
        Page->>Client: GetConsultGenerationJobAsync(jobId)
        Client->>Jobs: GET /api/ConsultGenerationJobs/{jobId}
        Jobs-->>Client: ConsultGenerationJobResponse
        Client-->>Page: Poll result
        Page->>Page: ApplyConsultGenerationJobResponse()
    end
```

## Event Contract

The `/events` endpoint emits these server-sent event names:

- `snapshot`: initial full `ConsultGenerationJobResponse`.
- `node-completed`: one DAG node's completion (or the map node's start), with
  `JobId`, `NodeId`, package-declared `Label`, `Message`, and node counts
  (milestone 4; replaces the fixed analysis stage events).
- `section-prose-step`: per-section prose progress with the step id, package-declared
  `Label`, `Message`, and step counts (milestone 3; replaces the fixed step-named
  events).
- `section-completed`: one completed section with `JobId`, `SectionId`, and generated `Text`.
- `section-failed`: one failed section with `JobId`, `SectionId`, and `Error`.
- `heartbeat`: stream keepalive with `JobId` and current `Status`.
- `done`: final full `ConsultGenerationJobResponse`.
- `error`: stream-level failure with `JobId` and `Error`.

Legacy event names (`analysis-started`, `concepts-extracted`, …,
`section-standard-draft-created`, …) are never emitted for new jobs but replay from
the event store for jobs that ran before milestones 3–4; the client ignores them.

Semantic events include persisted `id:` values in the form `{jobId}:{sequence:D12}`. Heartbeat events are not persisted and do not advance the semantic event sequence.

The Blazor page treats `snapshot`, `node-completed`, `section-prose-step`, `section-completed`, `section-failed`, and `done` as live UI update events. It falls back to polling `GET /api/ConsultGenerationJobs/{jobId}` when stream setup fails, the stream times out, the stream ends before `done`, event handling fails, or the server emits `error`.

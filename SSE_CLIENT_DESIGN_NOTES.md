# SSE Client Design Notes

## Native EventSource Alternative

Native `EventSource` is the browser's built-in SSE client. Instead of Blazor `HttpClient` manually opening and parsing the stream, JavaScript would do:

```js
const source = new EventSource("/api/ConsultGenerationJobs/{jobId}/events");
source.addEventListener("section-completed", event => {
  // event.lastEventId is populated by SSE id:
});
```

The browser automatically reconnects and sends `Last-Event-ID` after a dropped connection.

The problem here is auth.

Native `EventSource` cannot set an `Authorization: Bearer ...` header. The current SSE endpoint requires bearer auth, and the current `HttpClient` path adds that bearer token in `AIEndpointService`.

If we switched to native `EventSource`, the rest of the codebase would need a larger auth/session change.

Likely codebase changes:

- Add a JS SSE wrapper:
  - opens `EventSource`
  - subscribes to named events
  - forwards events into Blazor via JS interop
  - handles `open`, `error`, reconnect timing, and close/dispose
- Replace `AIEndpointService.StreamConsultGenerationJobEventsAsync(...)`:
  - no more `HttpClient.SendAsync(... ResponseHeadersRead ...)`
  - no more `SseParser.Create(stream)`
  - instead expose methods to start/stop JS-managed streams
- Change `Pages/Consults.razor`:
  - consume callbacks/events from JS instead of `await foreach`
  - maintain component disposal/navigation cancellation by telling JS to close the `EventSource`
  - still track attempt diagnostics, latest event ID/type, and fallback state
- Change authentication model for SSE:
  - Option A: move SSE auth to secure cookies so `EventSource` can authenticate automatically
  - Option B: put a short-lived opaque stream token in the query string
  - Option C: use same-origin proxy/session endpoint that sets a cookie before opening SSE
- Adjust diagnostics:
  - `EventSource` reconnects automatically, so one logical stream may contain several browser-managed physical connections.
  - Server `attemptId` becomes harder unless the JS wrapper closes and reopens manually with a new `attemptId`.
  - If native auto-reconnect is allowed to happen invisibly, server attempt correlation becomes less precise.
- CORS:
  - `Last-Event-ID` would be handled by the browser automatically.
  - Bearer auth headers are gone unless auth moves to cookies or tokenized URLs.

Tradeoff:

- Native `EventSource` gives automatic reconnect and native `Last-Event-ID`.
- But it forces a nontrivial auth redesign and JS interop event bridge.
- The current `HttpClient` approach keeps bearer auth simple and already fits the Blazor code; it just requires manual reconnect.

For this codebase, I would stay with `HttpClient` streaming unless we explicitly want to redesign SSE auth around cookies or short-lived stream tokens.

## Design Scores Without Reimplementation Cost

Taking implementation cost out, I would score two designs differently depending on whether we are willing to change auth.

Overall:

| Design | Score | Best Fit |
|---|---:|---|
| Native `EventSource` with cookie/BFF or short-lived stream token | 8.3/10 | Best pure browser-SSE design |
| Current `HttpClient` streaming with manual reconnect | 7.8/10 | Best fit for current bearer-token SPA |
| Native `EventSource` while keeping bearer auth | 5.5/10 | Awkward, because `EventSource` cannot set `Authorization` |

Matrix:

| Criterion | `HttpClient` Manual | Native `EventSource` |
|---|---:|---:|
| Standards-native SSE behavior | 7 | 10 |
| Built-in reconnect / `Last-Event-ID` | 6 | 10 |
| Bearer-token auth fit | 10 | 3 |
| Cookie/BFF auth fit | 7 | 10 |
| Security clarity | 9 | 8 if cookie/BFF, 5 if query token |
| Attempt-level diagnostics | 9 | 6 |
| Error visibility | 8 | 5 |
| CORS/header control | 9 | 6 |
| Blazor/.NET integration | 9 | 6 |
| Long-term SSE simplicity | 7 | 9 |
| Fine-grained retry policy | 9 | 6 |
| Testability from terminal | 9 | 7 |

Recommendation for this codebase: stay with `HttpClient` manual reconnect.

Reason: the app already uses bearer auth, and the diagnostics model cares about explicit attempt IDs, exact exit reasons, and client-side fallback decisions. `HttpClient` gives header control, token control, response status visibility, and deterministic attempt correlation.

If this were redesigned from scratch with a same-origin BFF/cookie session, I would pick native `EventSource`. It is the cleaner browser-native SSE abstraction. But for a bearer-token Blazor WASM app, native `EventSource` creates an auth workaround that is less clean than manual reconnect.

## Profile And Audit Timeline Vision

For the long-term profile/audit timeline, the SSE client choice matters less than the server-side event model.

The right long-term architecture is:

```text
Durable workflow/state
  -> append-only semantic job event log
  -> profile job index
  -> profile timeline UI

SSE live stream / resume reads from that same event log
```

So the Profile page should not depend on whether the browser used `HttpClient` or native `EventSource`. It should read persisted server events.

What this means:

The event log becomes more than an SSE resume cache. It becomes the durable job timeline.

Good timeline events:

- job created
- analysis started
- concepts extracted
- problem identified
- typical trajectory created
- patient trajectory created
- section generation started
- section prose step completed
- section completed
- section failed
- job completed
- job failed

Separate from that, keep stream diagnostics:

- SSE attempt opened
- request aborted
- timeout
- reconnect
- polling fallback started/completed
- client visibility/network/service worker state

Keep these as two related timelines:

- Job timeline: what happened in the generation workflow.
- Delivery timeline: how the browser received or failed to receive updates.

For auditability, the job timeline should be server-authored. Client `SseExit` diagnostics are useful evidence, but not the source of truth.

Design impact:

This makes `HttpClient` manual reconnect slightly more attractive for the current system because it gives better delivery diagnostics:

- explicit `attemptId`
- exact fallback reason
- precise latest event ID/type
- controlled retry/fallback behavior

Native `EventSource` is still cleaner for pure streaming, but its automatic reconnect can hide physical connection boundaries unless we deliberately close and reopen it ourselves, which gives up some of its simplicity.

Score for audit/profile vision:

| Criterion | `HttpClient` Manual | Native `EventSource` |
|---|---:|---:|
| Semantic job timeline | 9 | 9 |
| Resume from event log | 9 | 10 |
| Delivery diagnostics | 9 | 6 |
| Attempt correlation | 9 | 5 |
| Browser-native reconnect | 6 | 10 |
| Auth fit with current bearer model | 10 | 3 |
| Profile/audit readiness | 9 | 8 |

For the long-term vision, keep `HttpClient` manual reconnect and invest in the server event log/job index.

One important nuance: the current event store materializes events from Durable state. That is good for replay and timeline display, but for stricter auditability we may eventually want event timestamps from when the workflow state changed, not only when the event row was materialized. For product auditability, current `CreatedAtUtc` is probably enough; for regulatory-grade audit, add explicit workflow event timestamps.

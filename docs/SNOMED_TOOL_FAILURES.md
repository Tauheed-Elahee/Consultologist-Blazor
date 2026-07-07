# SNOMED Tool Failures in Consult Generation

Diagnosis and remediation options for consult-generation job failures caused by the
`search_concepts` SNOMED tool, investigated 2026-07-06.

## Symptom

Durable consult-generation jobs fail during preprocessing with:

```
Task 'ExtractPatientConceptsActivity' (#3) failed with an unhandled exception:
HTTP 400 (invalid_request_error: tool_user_error)
An error occurred invoking 'search_concepts': An error occurred invoking 'search_concepts'.
```

## Root cause (verified end-to-end)

1. **Snowstorm rejects long search terms.** Its REST API returns HTTP 400 with
   `"Search term can not have more than 250 characters."` — confirmed directly against
   `https://snomed.consultologist.ai/snowstorm/MAIN/concepts`.
2. **The MCP server does not handle that.** The `search_concepts` tool (Azure Functions
   MCP app at `mcp.snomed.consultologist.ai`) passes the agent's term straight through to
   Snowstorm with no length validation or error handling. When Snowstorm returns 400, the
   tool handler throws, and the MCP SDK masks the exception as the generic
   `An error occurred invoking 'search_concepts'.` Reproduced live: a ~200-character term
   returns results; a ~260-character term returns the masked error.
3. **Azure AI Foundry surfaces the tool failure as `tool_user_error`** (HTTP 400), which
   `AgentSectionGenerator` receives, failing `ExtractPatientConceptsActivity` and with it
   the orchestration.

The trigger is the agent occasionally passing a whole clinical phrase or sentence as the
search term instead of a short concept — anything over 250 characters kills the run. The
Snowstorm server itself is healthy; short searches work fine. Nothing in this repository
is broken.

## Fix options

In order of value:

1. **The MCP server (root fix).** Applied in whatever repo deploys
   `mcp.snomed.consultologist.ai` (source is not in this repo): validate the term and
   return an *informative* tool error instead of throwing — e.g., "Search term must be
   250 characters or fewer; retry with a short clinical term." Agents can self-correct
   from a descriptive error; they cannot from a masked exception. Alternatively, truncate
   to 250 characters and proceed, though the explicit error usually produces better agent
   behavior. The same input guard belongs on the other tools (`get_ancestors`,
   `get_children`, `validate_concept`, `ecl_query`).
2. **The Foundry agent's instructions.** Add a line to the agent definition that
   `search_concepts` must be called with short terms — single clinical concepts, a few
   words — never sentences.
3. **This repository (mitigation).** Prompt guidance plus Durable retry policies in
   `src/Consultologist.Api/Jobs/ConsultGenerationJobs.cs` — detailed below.

## Option 3 in detail

Two independent changes in `ConsultGenerationJobs.cs`, both defensive — they reduce how
often the bad tool call happens and stop it from killing the whole job when it does.

### Part A — tool-usage guidance in the prompts (prevention)

The four preprocessing activities (`ExtractPatientConceptsActivity`,
`IdentifyProblemActivity`, `CreateTypicalTrajectoryActivity`,
`CreatePatientTrajectoryActivity`) each build a prompt and funnel it through
`ConsultGenerationPreprocessingRunner.RunConceptPromptAsync`. Add one instruction there —
prepended once, so all four get it:

> When calling SNOMED tools such as `search_concepts`, search for one short clinical term
> at a time (a few words); never pass sentences or text over 250 characters.

Prompt steering is not a guarantee, but it directly targets the observed failure mode,
and it is the only lever this repo has over the agent's tool-calling behavior since the
agent definition lives in Foundry.

### Part B — Durable retry policy on the activity calls (resilience)

The orchestrator invokes every activity bare — no `TaskOptions` — so a single thrown
exception fails that step and cascades into the job-failure path immediately. Call sites:
the four concept activities and the three section-drafting activities
(`context.CallActivityAsync(...)` around lines 1418–1508 and 1710–1738).

The change: define one shared `TaskOptions` with a `RetryPolicy` (e.g., 3 attempts, ~5 s
first retry, 2× backoff) and pass it at those call sites. This is unusually effective
here because agent calls are nondeterministic: a retry re-runs the same prompt, and the
agent almost certainly issues different tool calls the second time, so one over-length
`search_concepts` call stops being fatal.

Refinement worth including: use `TaskOptions.FromRetryHandler` to retry only on the
retryable signature (the HTTP 400 `tool_user_error` / transient failures) so genuine
configuration errors — like the missing-`AzureAI__Endpoint` `InvalidOperationException`
from `AgentSectionGenerator` — still fail fast instead of burning three agent calls.

### Trade-offs

- Retries cost extra Foundry tokens and add latency when a step does fail.
- Part A makes every run's prompts slightly longer.
- Existing failure handling is undisturbed: the orchestrator's failure-text
  categorization only engages after retries are exhausted, so the user-facing
  "concept extraction failed" path remains the final backstop.

### Scope and verification

Roughly 15–20 lines, all in `ConsultGenerationJobs.cs`. Verify by building, running the
test suite, and exercising a consult generation end-to-end.

Option 3 is mitigation, not the cure — the root fix remains the 250-character guard with
a descriptive error in the MCP server, because a masked exception leaves any agent
(including future ones) unable to self-correct.

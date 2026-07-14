# Consult Generation Workflow

> **Superseded 2026-07-14 (milestone 4)**: the pipeline described below — fixed
> analysis stages, stage-named SSE events, per-stage activities — is the pre-DAG
> generation. The engine is now a topological interpreter over a package-declared node
> DAG (normative format: [customizable-workflow/package-format-v4.md](customizable-workflow/package-format-v4.md);
> design: [customizable-workflow/dag-as-data-design.md](customizable-workflow/dag-as-data-design.md)).
> Stage event names shown here still replay from the event store for pre-DAG jobs but
> are no longer emitted. This document remains as the record of the wire mechanics
> (SSE/resume/polling), which are unchanged.

## High-Level Workflow

This is the pre-milestone-4 workflow, kept for the transport-mechanics record.

1. The user enters a draft consult and clicks **Create consult**.

2. The browser gathers the draft plus the active section standards.

3. The browser starts one consult generation job by calling:

   ```text
   POST /api/ConsultGenerationJobs
   ```

4. The API creates a job ID, initializes durable job state, and starts one Durable orchestration for the consult.

5. The orchestration starts one generation activity per consult section.

6. Each section activity calls the AI section generator with:

   - the full consult draft
   - the section name
   - the section-specific writing standard

7. As section activities finish, the durable job state is updated with either:

   - generated section text
   - a section failure error

8. The browser opens the job event stream:

   ```text
   GET /api/ConsultGenerationJobs/{jobId}/events
   ```

9. The event stream sends live progress events to the browser:

   - `snapshot`
   - `section-completed`
   - `section-failed`
   - `heartbeat`
   - `done`
   - `error`

10. The browser updates the consult note as sections complete.

11. When all sections have finished, the orchestration finalizes the job as `Completed` or `Failed`.

12. The event stream sends `done`, and the browser stops the loading state.

13. If the live event stream fails or times out, the browser falls back to polling:

   ```text
   GET /api/ConsultGenerationJobs/{jobId}
   ```
## Proposed Changes

Before the sections are made I would like the following things to happen.

Ask the AI to do the following tasks:

---

starting with the draft consult

---

patient information:

```text
Draft consult content
```

 write a bullet point list of SNOMED concepts

---

The AI agent will return a bullet point list of SNOMED concepts. With the list, do the following task:

---

Given the following list of SNOMED concepts of the patient:

```text
List of SNOMED terms of the patient
```

What is the disease, as a single SNOMED concept term?

---

The AI agent will return a single bullet point of the SNOMED concept representing the disease. With the disease SNOMED concept, do the following task:

---

With the disease `disease SNOMED concept term`, list the SNOMED concepts of a typical history of a patient of typical trajectory.

---

The AI agent will return a bullet point list of SNOMED concepts outlining a typical history of a patient of typical trajectory

---

For each concept in the following typical history list of SNOMED concepts:

```text
A list of SNOMED CT concepts (preferred terms) that represent a **typical clinical trajectory** for a patient with **disease**
```

Update the relevant SNOMED term with the term from the list of SNOMED terms of the patient history:

```text
the relevant SNOMED CT concepts (preferred terms) are Based on the provided patient information
```

---

The AI agent will return a bullet point list of SNOMED concepts outlining the trajectory of the patient through the disease. With this new list, do the following for each section (HPI, Personal Medical History, Medications, etc.) in parallel jobs:

---

With the following list of SNOMED concepts:

```text
Updated bullet point list of SNOMED concepts
```

Write the (HPI, Personal Medical History, Medications) section of a standard consult note

---

The AI agent will return prose content of a standard consult note, with this content do the following:

---

Update draft consult note:

```text
standard section of the standard consult
```

With the patient information:

```text
patient information
```

---

The AI agent will produce prose content that is a standard draft of the section, with this standard draft do the following:

---

Update draft consult note:

```text
standard draft from the last section
```

With the following changes:

```text
section specific instructions from the user's browser
```

---

For each section return the final prose back to the browser

## Validation Formats And App Data Flow

The AI agent can continue to use its current SNOMED-facing instruction format:

```text
- term (type) - id number
- term [not SNOMED concept]
- term (type) - id number [not active SNOMED concept]
```

That bullet list format should be treated as an agent input/output format, not as the app's internal data model.

Inside the app, each SNOMED-producing agent response should be parsed into structured data before it is used by later workflow steps.

Recommended internal concept shape:

```json
{
  "term": "Type 2 diabetes mellitus",
  "type": "disorder",
  "id": "44054006",
  "isSnomedConcept": true,
  "isActive": true
}
```

Name this internal record `ClinicalConcept`.

Recommended internal non-SNOMED shape:

```json
{
  "term": "Lives alone",
  "type": null,
  "id": null,
  "isSnomedConcept": false,
  "isActive": null
}
```

Recommended inactive SNOMED shape:

```json
{
  "term": "Example inactive concept",
  "type": "finding",
  "id": "123456",
  "isSnomedConcept": true,
  "isActive": false
}
```

### Validation Rules

Each SNOMED bullet returned by the agent should match one of these accepted forms:

```text
- term (type) - id number
- term [not SNOMED concept]
- term (type) - id number [not active SNOMED concept]
```

The app should drop malformed items before continuing to the next AI step.

Validation should check:

- every bullet has a non-empty term
- every bullet starts with `- `
- SNOMED concepts include a non-empty type
- SNOMED concepts include a numeric id
- inactive concepts are explicitly marked with `[not active SNOMED concept]`
- non-SNOMED findings are explicitly marked with `[not SNOMED concept]`
- no extra prose or formatting appears around the bullet list

Only `- ` bullets should be accepted.

Accepted:

```text
- Diabetes mellitus (disorder) - 73211009
```

Rejected:

```text
Diabetes mellitus (disorder) - 73211009
* Diabetes mellitus (disorder) - 73211009
```

Leave a parser comment explaining that bare lines and `*` bullets are intentionally rejected so malformed agent output is visible during testing.

### Data Flow

1. The browser sends the draft consult and section standards to the API.

2. The API starts a durable consult generation job.

3. The orchestration asks the AI agent to extract SNOMED concepts from the draft consult.

4. The app parses the agent's bullet list into structured concept records.

5. The app validates the structured concept records.

6. The orchestration asks the AI agent to identify the disease or primary clinical problem from the validated patient concepts.

7. The app parses and validates that disease/problem concept.

8. The orchestration asks the AI agent for a typical disease trajectory as SNOMED concepts.

9. The app parses and validates the typical trajectory concepts.

10. The orchestration asks the AI agent to reconcile the typical trajectory with the patient-specific concept list.

11. The app parses and validates the patient-specific trajectory concepts.

12. The orchestration starts section generation activities in parallel.

13. Each section activity receives structured context from the preprocessing stage:

   - original draft consult
   - validated patient concepts
   - validated disease/problem concept
   - validated typical trajectory concepts
   - validated patient-specific trajectory concepts
   - section name
   - section standard
   - section-specific user instructions

   The original draft remains the source of truth. The structured patient trajectory is organizing context. Section prompts should explicitly say not to add typical trajectory details unless they are supported by the original draft or the validated patient trajectory.

14. When prompting the AI agent, the app renders structured concepts back into the required bullet list format.

15. The AI agent writes prose for each section using the prose rules:

   - no markdown formatting
   - paragraph separation only where appropriate
   - placeholders where appropriate

16. The final prose sections are stored in the durable job state.

17. The event stream sends completed sections back to the browser as they finish.

### Proposed Progress Events

To make the preprocessing workflow visible to the browser, the event stream should add stage-level progress events before section generation begins.

Recommended new event names:

```text
analysis-started
concepts-extracted
problem-identified
typical-trajectory-created
patient-trajectory-created
section-generation-started
```

These events would be emitted in addition to the existing section events:

```text
snapshot
section-completed
section-failed
heartbeat
done
error
```

Recommended event flow:

1. `snapshot`

   Initial job state after the browser connects to the stream.

2. `analysis-started`

   The orchestration has started preprocessing the draft consult before generating sections.

3. `concepts-extracted`

   The AI agent has extracted patient SNOMED concepts from the draft, and the app has parsed and validated them.

4. `problem-identified`

   The AI agent has identified the primary disease or clinical problem concept, and the app has parsed and validated it.

5. `typical-trajectory-created`

   The AI agent has produced the typical disease trajectory concepts, and the app has parsed and validated them.

6. `patient-trajectory-created`

   The AI agent has reconciled the typical trajectory against the patient-specific concepts, and the app has parsed and validated the resulting patient trajectory.

7. `section-generation-started`

   Preprocessing is complete and parallel section generation has started.

8. `section-completed`

   One section has completed and can be rendered in the consult note.

9. `section-failed`

   One section failed and the browser can show the section-level error.

10. `heartbeat`

   The stream is still connected while no new stage or section event is available.

11. `done`

   The job reached a terminal state.

12. `error`

   The stream or job encountered an error that should trigger polling fallback or an error state.

Recommended error event payload:

```json
{
  "jobId": "abc123",
  "stage": "concept-extraction-failed",
  "error": "No valid clinical concepts were extracted."
}
```

Recommended stage event payload:

```json
{
  "jobId": "abc123",
  "stage": "concepts-extracted",
  "message": "Clinical concepts extracted.",
  "completedStageCount": 2,
  "totalStageCount": 6
}
```

Do not send concept data in stage events for now. The browser only needs progress labels and stage counts. Keep full clinical concept data in durable state.

Use `totalStageCount: 6` for the preprocessing and section-start progress stages:

```text
analyzing
concepts-extracted
problem-identified
typical-trajectory-created
patient-trajectory-created
section-generation-started
```

### Boundary Between Agent Format And App Format

The agent-facing format should remain optimized for prompting:

```text
- Type 2 diabetes mellitus (disorder) - 44054006
```

The app-facing format should remain optimized for validation, storage, retries, and later transformations:

```json
{
  "term": "Type 2 diabetes mellitus",
  "type": "disorder",
  "id": "44054006",
  "isSnomedConcept": true,
  "isActive": true
}
```

The app should only use bullet lists at the boundary where it sends text to, or receives text from, the AI agent. Durable state, validation, orchestration decisions, retries, and section generation inputs should use structured records.

## Additional Implementation Considerations

### Malformed Concept Handling

Define what happens when the agent returns malformed SNOMED bullets.

Recommended behavior:

1. Parse and validate the agent response.
2. Keep valid concepts.
3. Drop malformed concepts.
4. Record a validation warning with the number of dropped concepts.
5. Continue the preprocessing workflow with the remaining valid concepts.

Malformed concepts should not block consult generation when enough valid concepts remain.

If a required preprocessing stage produces no usable concepts after dropping malformed items, mark the job as `Failed` and tell the user that the app cannot process the consult.

Minimum viable concept counts:

- continue if at least one valid patient concept exists
- fail the job if disease/problem identification returns zero valid concepts
- fail the job if trajectory generation produces no usable trajectory concepts

`[not SNOMED concept]` findings should stay in the structured concept list.

Inactive SNOMED concepts should stay in the structured concept list with `isActive: false`.

Recommended validation warning shape:

```json
{
  "stage": "concepts-extracted",
  "droppedLineCount": 2,
  "reason": "Malformed SNOMED bullet"
}
```

### Concept Provenance

Each structured concept should record where it came from.

Recommended `source` values:

```text
patient-draft
disease-identification
typical-trajectory
patient-trajectory
section-generation-context
```

Example:

```json
{
  "term": "Shortness of breath",
  "type": "finding",
  "id": "267036007",
  "isSnomedConcept": true,
  "isActive": true,
  "source": "patient-draft"
}
```

Provenance makes it easier to debug why a concept influenced a section.

### Evidence From The Draft

Deferred for now.

For patient-specific concepts, store supporting text from the draft when available.

Example:

```json
{
  "term": "Shortness of breath",
  "type": "finding",
  "id": "267036007",
  "isSnomedConcept": true,
  "isActive": true,
  "source": "patient-draft",
  "evidence": "reports progressive dyspnea"
}
```

This helps distinguish patient-supported facts from inferred context.

### Uncertainty And Multiple Problems

Deferred for now.

Do not force every consult into exactly one disease concept.

The workflow should support:

- one primary clinical problem
- multiple active problems
- possible differential diagnoses
- unknown or uncertain disease identification
- reason-for-referral concepts that are not confirmed diagnoses

Recommended internal problem shape:

```json
{
  "primaryProblem": {
    "term": "Heart failure",
    "type": "disorder",
    "id": "84114007",
    "isSnomedConcept": true,
    "isActive": true,
    "confidence": 0.82
  },
  "differentials": [],
  "uncertainty": "Primary problem inferred from symptoms and referral context."
}
```

### Typical Trajectory Guardrail

Typical trajectory concepts should be treated as reference context, not as patient facts.

The final patient trajectory should contain only:

- facts supported by the draft consult
- facts supported by validated patient concepts
- clearly marked inferences

The model should not introduce typical disease features into the patient's history unless the draft supports them.

Recommended field for patient trajectory concepts:

```json
{
  "term": "Orthopnea",
  "type": "finding",
  "id": "62744007",
  "isSnomedConcept": true,
  "isActive": true,
  "support": "inferred",
  "evidence": null
}
```

Supported `support` values:

```text
draft-supported
inferred
typical-reference-only
```

Section generation should prefer `draft-supported` concepts and be cautious with `inferred` concepts.

### Data Privacy And Logging

This workflow is currently in testing, so logging can be more permissive while the pipeline is being developed and debugged. Before production, logs should avoid storing patient text, generated prose, and full concept lists unless explicitly enabled for a controlled debugging session.

During testing, log prompt artifacts to the console so the pipeline can be inspected:

- stage name
- prompt text
- raw response text
- validation warnings
- dropped malformed concept count
- dropped malformed raw lines
- elapsed time

Remove or disable prompt artifact logging before production.

Default logs should include:

- job ID
- stage name
- section ID
- status
- concept counts
- validation error codes
- elapsed time

Default logs should avoid:

- full patient draft text
- full generated consult prose
- full SNOMED concept lists
- free-text evidence snippets

### UI Progress Labels

Map preprocessing SSE events to simple user-facing progress labels.

Recommended labels:

```text
analysis-started -> Analyzing draft
concepts-extracted -> Extracting clinical concepts
problem-identified -> Identifying primary problem
typical-trajectory-created -> Building reference trajectory
patient-trajectory-created -> Building patient trajectory
section-generation-started -> Generating sections
section-completed -> Section completed
section-failed -> Section failed
done -> Consult complete
error -> Live updates interrupted
```

The UI should keep the existing polling fallback behavior if live updates fail.

### Failure Surface

Decide how each stage failure should affect the job.

Recommended initial behavior:

- section failure: keep current behavior and return partial consult output
- SSE stream failure: keep current polling fallback
- malformed individual concepts: drop and continue
- concept extraction returns zero valid concepts: set `concept-extraction-failed`, then mark job `Failed`
- disease/problem identification returns zero valid concepts: mark job `Failed`
- trajectory generation returns zero valid concepts: mark job `Failed`
- section generation all sections fail: mark job `Failed`
- section generation some sections fail: mark job `Completed` with partial output
- unrecoverable orchestration failure: mark job `Failed`

If preprocessing fails, the app should tell the user that it cannot process the consult. It should not silently fall back to direct section generation.

User-facing preprocessing failure message:

```text
The consult could not be processed because clinical concepts could not be extracted from the draft.
```

`Completed` means the consult generation job reached a terminal state after all required steps finished, even if some section activities failed. This matches the current workflow: if at least one section succeeds and the rest fail, the job can complete with partial output. If all section activities fail, the job is `Failed`.

### Schema Versioning

Structured preprocessing data should include a schema version.

Example:

```json
{
  "schemaVersion": 1,
  "patientConcepts": [],
  "problemContext": null,
  "typicalTrajectory": [],
  "patientTrajectory": []
}
```

This makes it safer to evolve concept records, trajectory records, and event payloads later.

### Durable State Shape

The durable job state will likely need new fields for preprocessing.

Possible additions:

```json
{
  "analysisStatus": "patient-trajectory-created",
  "analysisError": null,
  "patientConcepts": [],
  "problemContext": null,
  "typicalTrajectoryConcepts": [],
  "patientTrajectoryConcepts": [],
  "completedStageCount": 5,
  "totalStageCount": 6
}
```

Keep generated section output separate from preprocessing context.

Use kebab-case for SSE event names, stage payloads, and stored `analysisStatus` values. This keeps durable state, SSE payloads, logs, and browser handling on one string format.

Recommended `analysisStatus` values:

```text
not-started
analyzing
concepts-extracted
concept-extraction-failed
problem-identified
typical-trajectory-created
patient-trajectory-created
section-generation-started
completed
failed
```

C# code can still expose readable constants for these values:

```csharp
public static class ConsultGenerationAnalysisStatuses
{
    public const string ConceptsExtracted = "concepts-extracted";
}
```

### Event Idempotency

SSE event generation should avoid re-emitting the same stage event repeatedly.

The stream producer should track emitted stages the same way it currently tracks emitted completed and failed sections.

For example:

```text
seenStages = { concepts-extracted, problem-identified }
```

This matters because the SSE endpoint polls durable job state and emits changes as it sees them.

### Prompt Boundaries

Each AI call should have a narrow responsibility.

Recommended boundaries:

- extract concepts from the patient draft
- identify problem context from patient concepts
- generate typical trajectory from problem context
- reconcile patient trajectory from patient concepts and typical trajectory
- generate a standard section draft
- apply section-specific user instructions

Avoid prompts that ask the agent to both reason about SNOMED concepts and write final prose in the same call.

Final section-generation prompts should include:

- the original draft consult as the source of truth
- the structured patient trajectory as organizing context
- the section standard
- section-specific user instructions

The prompt should explicitly instruct the agent to use the trajectory to organize and standardize the section, but not to add typical trajectory details unless they are supported by the original draft or the validated patient trajectory.

### Manual Review And Clinical Safety

The generated consult should remain a draft for physician review.

The workflow should avoid presenting inferred concepts as confirmed facts. Any placeholders, uncertainty, or missing information should remain visible in the prose output.

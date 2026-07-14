# DAG-as-Data Design (Milestone 4, Phases B–D)

> **Status 2026-07-14 — gate lifted by decision; executing.** The normative format now
> lives in [package-format-v4.md](package-format-v4.md); this document remains the
> design record. Five reconciliations against post-phase-A reality supersede details
> below:
> **R1** — "sends with the node's schema" is dead: Foundry rejects request-level text
> options (spike #39). `output.schema` selects the attested structured-output agent
> (`concept-extraction`) and the `ConceptOutputContract` deserializer; the engine sends
> no schema per request. A second output shape means a second structured agent.
> **R2** — the activity deserializes (retryable at the activity boundary), the
> orchestrator renders: deserialized concepts ride the recorded activity result, so
> orchestrator-side binding rendering is pure over recorded data — replay-safe.
> **R3** — concept `source:` stamps survive via an engine-pinned well-known map (the
> four canonical node ids → their legacy sources; custom ids stamp the node id) —
> load-bearing for byte parity of the trajectory-context rendering.
> **R4** — v4 map bodies *lower* to the v3 section-step shape, so `run-prose-step` and
> `ProseStepVariableBuilder` execute them unchanged; the store returns both `Nodes`
> and `SectionSteps` for every spec version ≥ 2. New closure: map steps bind at most
> one upstream node, rendered `concept-context`.
> **R5** — the B1 job-start guard is removed in B2 (not phase D), since phase C flips
> the pin to v4 before D.

Recorded 2026-07-13 at implementation resolution, then **shelved by decision**: the
roadmap's gate ("only build DAG-as-data when a real specialty demands a different
pipeline shape", workflow-packages.md) holds. Milestone 4's executing slice is phases
0 and A — the file split (#38) and structured outputs replacing the concept parser
(#39/#40) — which deliver standalone value and are prerequisites here. Phases B–D live
in gated sub-issues #41 (B1), #42 (B2), #43 (C), #44 (D); pull them when the gate
fires, starting from this document.

Decisions locked with the plan (2026-07-13): eval-gated publishing is deferred to the
content-repos work (#16) — only static publish-time validation ships here; **no
tool-allowlist or budget fields in v4.0** — the format stays minimal (adding manifest
surface later is cheap, removing it is not).

## What changes and what doesn't

**Becomes package data**: the analysis pipeline's shape (today four hardcoded
sequential activities in `ConsultGenerationEngine.cs`), the per-node output contracts
(JSON Schemas), per-node failure policy (`failIfEmpty` messages), progress labels, and
the section fan-out (a declarative map node).

**Stays engine, correctly**: the input model (consult draft + sections with
id/name/standard), the two concept text renderers (byte-pinned by
`ConceptFormatContractTests`), `previous_step_output` threading, retry policy, the
trust/policy layer, and the interpreter itself.

## specVersion 4 manifest

`sectionSteps` is replaced by `nodes`; a `schemas` file-ref table is added (same
pattern as prompts). The complete `packages/general` v4 manifest — the verbatim
current pipeline:

```json
{
  "name": "general",
  "version": "v2026.MM.N",
  "specVersion": 4,
  "templating": { "engine": "scriban", "engineVersion": "7.2.5" },
  "preludes": { "snomed-tool-guidance": "prompts/_snomed-tool-guidance.md" },
  "schemas": { "concept-list": "schemas/concept-list.json" },
  "prompts": [ ...the seven current prompts, unchanged... ],
  "nodes": [
    { "id": "extract-patient-concepts", "kind": "prompt",
      "label": "Extracting clinical concepts",
      "prompt": "extract-patient-concepts",
      "bindings": { "consult_draft": "input:consult_draft" },
      "output": { "schema": "concept-list",
                  "failIfEmpty": "The consult could not be processed because clinical concepts could not be extracted from the draft." } },

    { "id": "identify-problem", "kind": "prompt",
      "label": "Identifying primary problem",
      "prompt": "identify-problem",
      "bindings": { "patient_concepts": "node:extract-patient-concepts" },
      "output": { "schema": "concept-list",
                  "failIfEmpty": "No valid disease or problem concept was identified." } },

    { "id": "create-typical-trajectory", "kind": "prompt",
      "label": "Building reference trajectory",
      "prompt": "create-typical-trajectory",
      "bindings": { "problem_concepts": "node:identify-problem" },
      "output": { "schema": "concept-list",
                  "failIfEmpty": "No valid typical trajectory concepts were generated." } },

    { "id": "create-patient-trajectory", "kind": "prompt",
      "label": "Building patient trajectory",
      "prompt": "create-patient-trajectory",
      "bindings": { "problem_concepts": "node:identify-problem",
                    "patient_concepts": "node:extract-patient-concepts",
                    "typical_trajectory_concepts": "node:create-typical-trajectory" },
      "output": { "schema": "concept-list",
                  "failIfEmpty": "No valid patient trajectory concepts were generated." } },

    { "id": "sections", "kind": "map",
      "label": "Generating sections",
      "over": "input:sections",
      "steps": [
        { "prompt": "standard-section-draft", "label": "Drafting section",
          "bindings": { "section_name": "item:name",
                        "patient_trajectory_concepts": { "from": "node:create-patient-trajectory", "as": "concept-context" } } },
        { "prompt": "patient-section-draft", "label": "Applying patient information",
          "bindings": { "standard_section_draft": "previous_step_output",
                        "consult_draft": "input:consult_draft",
                        "section_name": "item:name" } },
        { "prompt": "section-instructions", "label": "Applying section instructions",
          "bindings": { "patient_section_draft": "previous_step_output",
                        "section_name": "item:name",
                        "section_standard": "item:standard" } }
      ] }
  ]
}
```

### Binding references (edges are implicit — no separate edges array)

| Source | Meaning | Where valid |
|---|---|---|
| `input:consult_draft` | engine input model | anywhere |
| `input:sections` | the section collection | only as a map node's `over` |
| `node:<id>` | another node's output (defines an edge) | anywhere |
| `item:name` / `item:standard` / `item:id` | current map item fields | map steps only |
| `previous_step_output` | prior step in the same map body | map steps, not the first |

A binding value is a plain string (default rendering) or
`{ "from": "...", "as": "<renderer>" }`. The renderer vocabulary is engine-pinned and
closed: `concept-bullets` (byte-identical to `ConsultGenerationConceptFormatter.Format`,
default for `concept-list`-typed outputs) and `concept-context` (byte-identical to
`AgentSectionGenerator.FormatConcepts`). Smallest syntax that preserves the fact that
the same concept list renders two ways today.

### Output declaration

`output` optional; absent means text (map steps are always text — they thread as
`previous_step_output`). `{ "schema": "<id>", "failIfEmpty": "<message>" }` for JSON
nodes; `failIfEmpty` is only legal when the schema's root has a single array property
(validator-checked) — the compiled empty-result gates generalized into node data,
messages moving verbatim from the engine.

### The v4.0 closure (the gate, applied fractally)

Schemas are file refs (diffable, hashable, consistent with prompts), but v4.0 requires
any schema consumed by a concept renderer or `failIfEmpty` to be **structurally
identical to the engine's canonical concept-list schema** (modulo title/description) —
the same honest-closure posture `AnalysisPromptIds` had in v3. The file ref establishes
the *syntax* for package-declared schemas; the *semantics* (real producer/consumer
compatibility checking) open when a second output shape exists. Also closed in v4.0:
exactly one map node, `over: input:sections`, terminal (its aggregate output is not
bindable — reject `node:<map-id>` references with a clear error).

**Deferred tightening for the canonical schema** (noted 2026-07-14, phase A review):
the concept `id` is deliberately a string, never a JSON number — SCTIDs run to 18
digits and overflow IEEE-754 doubles past 2^53−1 (the phase-A verification job itself
extracted `12240181000119103`, 17 digits), the last digit is a Verhoeff check digit,
and Snowstorm/FHIR both treat concept ids as strings. When the schema becomes
package-declared here, add `"pattern": "^[0-9]{6,18}$"` to the string branch of `id`
so non-digit ids are rejected at generation time — the strict structured-outputs
subset supports `pattern` on strings. Held out of the phase-A agent definition to
avoid churning `concept-extraction` for a tightening no observed output has violated.

### Validator additions (dispatch on specVersion 4)

- `nodes` non-empty; unique ids; every `node:` reference resolves; graph acyclic (Kahn
  topological sort over `node:` edges).
- Binding sources parse against the closed namespaced vocabulary; renderer names valid;
  renderer input schema-compatible; bindings set-equal prompt variables (v3 rule
  reused); first map step can't bind `previous_step_output`.
- Schema files present, parse, conform to the structured-outputs subset
  (`additionalProperties:false`, all properties required, nullability via type arrays),
  and match the canonical concept schema where consumed (v4.0 rule).
- Every prompt referenced by exactly one node or map step (orphan rule generalized);
  `AnalysisPromptIds` closure lifts — v4 prompt ids are free-form.
- `sectionSteps` present ⇒ error ("replaced by nodes in specVersion 4"); `nodes` on
  specVersion ≤ 3 ⇒ error.

## Interpreter (phase B2)

**Job start**: resolve pin → validate → snapshot a `List<ConsultNodeDescriptor>`
(`Id, Kind, Label, PromptId?, Bindings, HasJsonOutput, FailIfEmpty?, Steps?`) into the
orchestration input, replacing `SectionSteps`.

**Scheduling** (replaces the sequential analysis block + section fan-out wholesale):

```
outputs = {}                          // nodeId -> raw output
pending = {}                          // Task -> nodeId
start every node whose node: deps ⊆ outputs.Keys, in manifest order
while pending nonempty:
    t = await Task.WhenAny(pending.Keys)   // replay-safe; DTFx records completion order
    nodeId = pending.remove(t); result = await t
    if failIfEmpty and result.Concepts.Count == 0:
        FailNodeAsync(nodeId, message); mark unreached nodes + sections skipped; return
    outputs[nodeId] = result.RawOutput
    entity.MarkNodeCompleted(nodeId, label, concepts, inputHash, outputHash)
    SetCustomStatus(...); start newly-ready nodes in manifest order
finalize as today
```

Determinism: node start order derives only from manifest order + recorded completions;
renderers are pure static functions so the orchestrator resolves bindings itself; the
map node's per-item tasks are the current `GenerateSectionPipelineAsync` bodies nearly
verbatim — its internal WhenAny drain preserves incremental `CompleteSection` /
`FailSection` entity calls and `section-completed` SSE exactly. For the current graph
the waves degenerate to today's sequence — a strong parity check.

**Activities**: keep `run-prose-step` untouched (map steps ride it; no drain on that
path); add exactly one new activity `run-prompt-node` with input
`(NodeId, WorkflowPackage, Dictionary<string,string> Variables)` — the activity
re-resolves the pinned package for the template, renders, sends with the node's schema,
validates/deserializes, computes input/output hashes, returns
`NodeRunResult(RawOutput, Concepts?, InputHash, OutputHash)`. The four named analysis
activities are deleted, including their branches in the transport's
`GetRuntimeFailureAction`. Retry policy (`AgentActivityRetryOptions`) reused unchanged.

**Failure semantics**: `FailPreprocessingAsync` generalizes to `FailNodeAsync`; the
analysis status becomes `<nodeId>-failed` — keeping the `-failed` suffix convention the
transport's `IsAnalysisFailureStatus` already keys on. Skipped-history for unreached
nodes is computed by the orchestrator (it holds the graph; the entity does not),
replacing the entity's reliance on `OrderedStages`.

## State migration (SchemaVersion 3)

- `ConsultGenerationJobState` gains `Dictionary<string, ConsultNodeOutputState>
  NodeOutputs`: `{ NodeId, Label, Status, Concepts?, InputHash?, OutputHash?,
  CompletedAtUtc?, Error? }`. **Per-node provenance (dag-improvements #6) lands here
  nearly free** — the activity computes the hashes anyway; the verification chain
  (pinpoint the exact step where two runs diverge) falls out.
- `SchemaVersion = 3` set by the node-update entity methods (replacing the hardcoded 2
  in `ApplyAnalysisUpdate`). The four named concept fields stay as tolerated legacy so
  v2-schema snapshots deserialize; `ToResponse` sources them from `NodeOutputs` by
  well-known id when present. `CompletedStageCount`/`TotalStageCount` generalize to
  node counts (map node counts as one).
- **SSE catalog after B2**: `snapshot`, `node-completed` (new generic event:
  jobId/nodeId/label/message/completedNodeCount/totalNodeCount — the milestone-3
  `section-prose-step` precedent applied to nodes), `section-prose-step`,
  `section-completed`, `section-failed`, `heartbeat`, `done`, `error`. The six legacy
  stage event names stop being emitted for new jobs but keep replaying from the event
  store for old ones; Consults.razor keeps its legacy cases until phase D and gains the
  `node-completed` case (labels from payload — the hardcoded
  `GetConsultGenerationProgressLabel`/`GetAnalysisStageLabel` switches die). History
  labels come from node labels; the synthesized v3 DAG carries today's labels so v3
  jobs' history stays coherent.

## Compatibility

`WorkflowNodeDefaults.V3SynthesizedDag(manifest)` builds exactly the manifest above
from a v3 package's four analysis prompts + declared `sectionSteps` (binding mapping:
`section_name`→`item:name`, `section_standard`→`item:standard`,
`patient_trajectory_concepts`→`{from: node:create-patient-trajectory, as:
concept-context}`, `consult_draft`→`input:consult_draft`, `previous_step_output`
unchanged). v2 chains through the existing v2→v3 step synthesis. One interpreter code
path for every spec version; v2/v3 packages stay valid forever.

## PR slicing

- **B1** (#41, no engine change, independently mergeable): manifest models
  (`WorkflowNodeSpec`, binding parser), validator graph rules,
  `SupportedSpecVersion = 4`, synthesis, `package-format-v4.md` spec doc, tests. Job
  start guards: v4 packages rejected with "engine does not yet interpret v4" until B2.
- **B2** (#42, the big one, **strict drain window** — activity names change, replay
  logic rewritten, entity schema bumped): interpreter, `run-prompt-node`, entity
  SchemaVersion 3 + `NodeOutputs`, `node-completed` SSE, Consults.razor/History
  genericization, deletion of the four named activities and engine-side stage
  vocabulary. Mandatory: a byte-parity rendering test (interpreter over synthesized v3
  DAG vs recorded fixtures) and the production verification job runs against the **v3**
  package first — proving synthesis fidelity before any v4 package exists.
- **C** (#43, no drain): `packages/general/schemas/concept-list.json` + v4 manifest
  (verbatim pipeline; prompts/standards byte-identical, `EffectiveInputHash`
  unaffected); publish script uploads `schemas/`; `PackageSourceValidationTests`
  inherits the graph rules automatically (it already funnels through
  `WorkflowPackageValidator.Validate`) — the CI half of dag-improvements #2. Publish,
  flip pin, verify with per-node provenance populated.
- **D** (#44, one release after B2): retire `OrderedStages` + stage message/label
  switches, dead `ValidationWarnings` plumbing, legacy stage SSE cases in
  Consults.razor, the B1 guard. Keep the synthesis paths and `run-prose-step`. Update
  CONSULT_GENERATION_EVENTS.md, current-state.md, and the decoupling-roadmap boundary.

Relative sizing: B1 ≈ 0.75×, B2 ≈ 2×, C ≈ 0.5×, D ≈ 0.5× (milestone-2 units).

## Deferred beyond this design

- **Eval-gated publishing** (golden fixtures against the pinned model) → #16, where
  CI-publishes-on-tag gives it a natural home. Static validation ships in B1/C.
- **Human-in-the-loop and memoization nodes** (dag-improvements #4/#5): future `kind`
  values the deliberately dumb format accommodates without change — that is the point
  of shipping the dumb format first.
- **Per-node tool allowlists / budgets** (dag-improvements #7): omitted from v4.0 by
  decision; add the fields when engine enforcement exists.

## Risk register

1. **Structured outputs × MCP tools** — gated by the phase-A spike (#39); fallbacks:
   instruction-level JSON with engine-side validation, or a dedicated
   structured-output agent for analysis nodes.
2. **Pin+deploy atomicity** (phase A): old parser can't read JSON, new deserializer
   can't read bullets — both skew directions fail totally; the engine deploy and the
   `AzureAI__AgentVersion` flip must land together.
3. **Interpreter replay determinism**: WhenAny/WhenAll are replay-safe, but ready-set
   ordering must derive only from manifest order; descriptors snapshotted into the
   orchestration input; renderers pure. The B2 drain window removes cross-version
   replay hazards.
4. **v3 synthesis fidelity**: byte-parity test + v3-package-first production
   verification are mandatory, not optional.
5. **Schema evolution vs immutable packages**: the v4.0 structural-equality rule
   prevents silent divergence; real compatibility checking arrives with a second
   output shape.
6. **Event replay across versions**: never reuse a legacy event name with a new
   payload shape; legacy names replay from the store until phase D.

# Workflow Package Format — specVersion 4 (DAG-as-Data)

Normative specification for `specVersion: 4` packages, authored for milestone 4
(issue #6, phases B1–D, gate lifted by decision 2026-07-14). v4 replaces the ordered
`sectionSteps` list with a declared **node DAG**: the analysis pipeline's shape, its
per-node output contracts, failure policy, and progress labels all become package data.
Prompt templates, preludes, standards, templating rules, and the concept rendering
contract are unchanged from [package-format-v2.md](package-format-v2.md) /
[package-format-v3.md](package-format-v3.md). Design record:
[dag-as-data-design.md](dag-as-data-design.md).

## What changes and what doesn't

**New in v4**: a required `nodes` array (the DAG; edges implicit in `node:` binding
references) and a `schemas` file-ref table. `sectionSteps` is gone — the section
pipeline lives inside the map node's `steps`.

**Unchanged**: package layout conventions, `standards.md`, prompt files, preludes,
Scriban rules, CalVer, immutability, the two concept text renderings, and the
string→string map-step semantics.

## Package layout

```
{name}/{version}/
├── manifest.json
├── standards.md
├── schemas/
│   └── concept-list.json
└── prompts/
    └── ... (unchanged)
```

## Manifest schema

```json
{
  "name": "general",
  "version": "v2026.MM.N",
  "specVersion": 4,
  "templating": { "engine": "scriban", "engineVersion": "7.2.5" },
  "preludes": { "snomed-tool-guidance": "prompts/_snomed-tool-guidance.md" },
  "schemas": { "concept-list": "schemas/concept-list.json" },
  "prompts": [ ...unchanged shape from v2/v3... ],
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

This example is not illustrative but **normative twice over**: it is the verbatim
current pipeline, and it is exactly the DAG the engine synthesizes for v2/v3 packages
(see Compatibility).

### Node object

| Field | Required | Meaning |
|---|---|---|
| `id` | yes | Node identity; appears in progress events, per-node provenance, state, error messages |
| `kind` | yes | `prompt` (renders one template, sends it, one output) or `map` (fans a step body over a collection) |
| `label` | yes | Display label: UI progress and job history |
| `prompt` | prompt nodes | Id of the `prompts[]` entry the node renders |
| `bindings` | prompt nodes | Map of template variable → binding value (below); must set-equal the prompt's declared variables |
| `output` | optional | `{ schema, failIfEmpty? }`; absent = text output |
| `over` | map node | The collection to fan over — `input:sections` (the only value in v4.0) |
| `steps` | map node | Ordered string→string steps, the v3 step shape with v4 binding values |

### Binding values and sources

A binding value is a plain source string, or `{ "from": <source>, "as": <renderer> }`
to select a renderer:

| Source | Meaning | Where valid |
|---|---|---|
| `input:consult_draft` | the physician's draft, verbatim | anywhere |
| `input:sections` | the section collection | only as a map node's `over` |
| `node:<id>` | another node's output (defines an edge) | anywhere except a map's own id |
| `item:name` / `item:standard` | current map item fields | map steps only |
| `item:id` | reserved (not yet bindable) | — |
| `previous_step_output` | prior step in the same map body | map steps, not the first |

Renderers (closed, engine-pinned): `concept-bullets` (the analysis format —
`ConsultGenerationConceptFormatter`, default for concept-list outputs) and
`concept-context` (the trajectory-context format — `AgentSectionGenerator.FormatConcepts`).
Both remain byte-pinned by the v2 concept rendering contract.

### Output declaration and failure policy

`output: { "schema": "<id>", "failIfEmpty": "<message>" }`. A node with an `output`
produces schema-conformant JSON; its consumers receive rendered concept text (via a
renderer) rather than the raw JSON. `failIfEmpty` moves the engine's empty-result
business gates into node data: when the (single, array-valued) output property is
empty, the job fails with the declared message and unreached nodes/sections are marked
skipped. Absent `output` = text node; its raw text feeds `node:` consumers directly.

## Schemas are welded to agents

Foundry rejects request-level text-format options for agent-bound calls (spike #39), so
**the output schema is enforced by the agent definition, not per request**: a node with
`output.schema: "concept-list"` is executed against the attested structured-output
agent (`agents/concept-extraction.yaml`, pinned by `AzureAI__ConceptAgentName/Version`),
whose published manifest embeds the same schema with `strict: true`. The engine sends
no schema at request time; text nodes and map steps use the prose agent.

Consequences:
- The package's schema file **documents** the edge contract and is validated for
  structural identity with the engine/agent schema; it does not *configure* generation.
- **A second output shape requires a second attested structured-output agent**, plus
  lifting the closure below — a code-and-agent change by design, not a package edit.
- The deferred SCTID `pattern` tightening (see dag-as-data-design.md) must land in the
  engine contract and agent definition first; a package schema adding it unilaterally
  fails the identity rule.

## The v4.0 closures

Applied-fractally gate posture (each opens when a real second consumer exists):

1. **Schema identity**: every declared schema must be structurally identical to the
   engine's canonical concept-list schema, modulo `title`/`description` (canonical
   sorted-key comparison).
2. **Exactly one map node**, `over: input:sections`, terminal (its aggregate output is
   not bindable).
3. **Map-step upstream closure**: map steps may bind at most one upstream node,
   rendered as `concept-context` — which is what lets map bodies *lower* to the v3
   step shape and execute on the unchanged `run-prose-step` activity.
4. `item:id` reserved.
5. No conditionals, loops, or expression language — a model step makes decisions and
   emits them as data (dag-improvements #3).

## Validation (publish-time and load-time, fail-loud)

Kept from v2/v3: templating engine/version gates, template parse + strict-render
probes, prelude resolution, duplicate prompt ids, unused-variable warnings. Lifted in
v4: the closed prompt-id and variable-name sets (bindings govern instead; the analysis
closure of v2/v3 stays enforced for those spec versions).

New in v4 (all blocking): non-empty `nodes`; unique node ids; known kinds; non-blank
labels; every binding source parses against the vocabulary; `node:` references resolve
and never target the map node; `input:sections` only as `over`; `item:`/
`previous_step_output` only inside map steps (never the first step); renderer names
valid and only applied to concept-list-typed outputs; bindings set-equal the referenced
prompt's variables; graph acyclic (Kahn, manifest-order seeded); schemas declared,
present, parsing, and canonically identical to the engine schema; `failIfEmpty`
non-blank when present; exactly one map node with non-empty steps and unique step ids;
the map-step upstream closure; every prompt referenced by exactly one node or map step.

## Compatibility and rollout

- The engine accepts `specVersion` ≤ 4. **v2 and v3 packages remain valid forever**:
  the engine synthesizes the canonical DAG above from their (declared or synthesized)
  section steps — one interpreter path for every spec version. Conversely a v4
  package's map steps lower to the v3 step shape for the prose activity.
- **Concept sources**: the four canonical node ids carry their legacy `source:` stamps
  (`patient`, `problem`, `typical-trajectory`, `patient-trajectory`) in the
  trajectory-context rendering; custom node ids stamp the node id itself.
- v1 packages: standards display only; rejected at job start (unchanged since
  milestone 3).
- Rollout order: deploy the v4-capable engine (interpreter, #42) **before** publishing
  the first v4 package (#43). Until #42 lands, v4 packages validate and load but the
  job start rejects them (422) — guard-then-implement.
- **Input-hash semantics unchanged**: nodes, schemas, and prompts are package content,
  covered by the `workflowPackage` provenance ref, not the effective-input hash.
  Per-node provenance (input/output hashes) is recorded by the interpreter per run.

## Out of scope in v4.0

- Per-node model parameters, tool allowlists, and budgets (decision 2026-07-14: no
  manifest surface until enforcement exists).
- Human-in-the-loop and memoization node kinds (future `kind` values).
- Multiple or non-section map collections; bindable map aggregates.
- Eval-gated publishing (deferred to the content-repos work, #16).

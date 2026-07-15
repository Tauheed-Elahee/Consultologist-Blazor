# Workflow Package Format — specVersion 5 (Fork Lineage, Data Collections, One Node Kind)

Normative specification for `specVersion: 5` packages, authored for Milestone 5
(issue #59, PR 1 of 4). v5 makes three moves designed together
([package-format-v5-design.md](package-format-v5-design.md)): all customization
becomes **forking** (recorded by `derivedFrom`), package-shipped bindable content
gets a home (**`data/`** with self-describing collections), and the node calculus
collapses to **one kind with a `forEach` multiplicity property** — the map
container, its `steps`, and `previous_step_output` retire. Prompt templates,
preludes, Scriban rules, CalVer, immutability, schemas-welded-to-agents, and the
concept renderers are unchanged from
[package-format-v2.md](package-format-v2.md) / [-v3.md](package-format-v3.md) /
[-v4.md](package-format-v4.md).

## What changes and what doesn't

**New in v5**: `derivedFrom` (fork origin), the `data` table + `data/` directory +
`data:` binding namespace, `forEach` on nodes, item-aligned `node:` edges,
`result` (the named deliverable), and per-(node, item) execution and provenance.

**Gone in v5**: `kind`, the map node (`over`/`steps`), `previous_step_output`,
`standards.md` (standards become a data collection), and the account standards
override (all customization is forking).

**Unchanged**: package layout conventions otherwise, prompt files, preludes,
`schemas/` and the output-contract catalog match rule, the two concept renderers,
`failIfEmpty` (now per item on forEach nodes).

## Package layout

```
{name}/{version}/
├── manifest.json
├── prompts/
│   └── ... (unchanged)
├── schemas/
│   └── concept-list.json
└── data/
    ├── clinic-guidelines.md          # scalar: one bindable text (illustrative)
    └── standards/                    # collection: self-describing directory
        ├── index.json
        ├── hpi.md
        ├── pmh.md
        └── ...
```

There is no `standards.md`. A **collection** is a subdirectory with an
`index.json` describing its item shape and items; file paths inside `index.json`
are collection-relative:

```json
{
  "fields": ["id", "name", "content"],
  "items": [
    { "id": "hpi", "name": "History of Present Illness", "file": "hpi.md" },
    { "id": "pmh", "name": "Past Medical History", "file": "pmh.md" }
  ]
}
```

The `file`'s text becomes the item's `content` field. `fields` must include `id`
and `name`; item ids must be unique. (`index.json` over `collection.json`: the
`index.html` convention — the directory's table of contents.)

## Manifest schema

```json
{
  "name": "general",
  "version": "v2026.MM.N",
  "specVersion": 5,
  "derivedFrom": null,
  "templating": { "engine": "scriban", "engineVersion": "7.2.5" },
  "preludes": { "snomed-tool-guidance": "prompts/_snomed-tool-guidance.md" },
  "schemas": { "concept-list": "schemas/concept-list.json" },
  "data": {
    "standards": "data/standards/"
  },
  "prompts": [ "...unchanged shape from v2–v4..." ],
  "result": "node:section-instructions",
  "nodes": [
    { "id": "extract-patient-concepts",
      "label": "Extracting clinical concepts",
      "prompt": "extract-patient-concepts",
      "bindings": { "consult_draft": "input:consult_draft" },
      "output": { "schema": "concept-list",
                  "failIfEmpty": "The consult could not be processed because clinical concepts could not be extracted from the draft." } },

    { "id": "identify-problem",
      "label": "Identifying primary problem",
      "prompt": "identify-problem",
      "bindings": { "patient_concepts": "node:extract-patient-concepts" },
      "output": { "schema": "concept-list",
                  "failIfEmpty": "No valid disease or problem concept was identified." } },

    { "id": "create-typical-trajectory",
      "label": "Building reference trajectory",
      "prompt": "create-typical-trajectory",
      "bindings": { "problem_concepts": "node:identify-problem" },
      "output": { "schema": "concept-list",
                  "failIfEmpty": "No valid typical trajectory concepts were generated." } },

    { "id": "create-patient-trajectory",
      "label": "Building patient trajectory",
      "prompt": "create-patient-trajectory",
      "bindings": { "problem_concepts": "node:identify-problem",
                    "patient_concepts": "node:extract-patient-concepts",
                    "typical_trajectory_concepts": "node:create-typical-trajectory" },
      "output": { "schema": "concept-list",
                  "failIfEmpty": "No valid patient trajectory concepts were generated." } },

    { "id": "standard-section-draft", "forEach": "data:standards",
      "label": "Drafting section",
      "prompt": "standard-section-draft",
      "bindings": { "section_name": "item:name",
                    "patient_trajectory_concepts": { "from": "node:create-patient-trajectory", "as": "concept-context" } } },

    { "id": "patient-section-draft", "forEach": "data:standards",
      "label": "Applying patient information",
      "prompt": "patient-section-draft",
      "bindings": { "standard_section_draft": "node:standard-section-draft",
                    "consult_draft": "input:consult_draft",
                    "section_name": "item:name" } },

    { "id": "section-instructions", "forEach": "data:standards",
      "label": "Applying section instructions",
      "prompt": "section-instructions",
      "bindings": { "patient_section_draft": "node:patient-section-draft",
                    "section_name": "item:name",
                    "section_standard": "item:content" } }
  ]
}
```

This example is normative twice over: it is the canonical v5 respelling of the
verbatim current pipeline, and it is exactly what v2–v4 packages lower to at
snapshot time (see Compatibility). Note the step chain is plain graph structure —
`node:standard-section-draft` between two same-collection forEach nodes is an
**item-aligned** edge (instance *i* reads instance *i*).

### Top-level fields

| Field | Required | Meaning |
|---|---|---|
| `derivedFrom` | yes (nullable) | Fork origin: a single **concrete** ref (`name@vYYYY.MM.N`, never `@latest`), or `null` for root packages. Updated on rebase. The derivation chain and the root are always **derived by walking**, never stored. |
| `data` | when referenced | Table of bindable content: id → file path (scalar) or directory path ending `/` (collection with `index.json`) |
| `result` | yes | `node:<id>` of a forEach node; its per-item outputs are the workflow's deliverable (the generated sections) |
| `nodes` | yes | The DAG; one kind, no `kind` field |

### Node object

| Field | Required | Meaning |
|---|---|---|
| `id` | yes | Node identity; per-item runs surface as `<id>:<itemId>` in provenance and state |
| `label` | yes | Display label: UI progress and job history |
| `forEach` | optional | `data:<collection>` — the node runs once per collection item; absent = runs once (scalar) |
| `prompt` | yes | Id of the `prompts[]` entry the node renders |
| `bindings` | yes | Template variable → binding value; must set-equal the prompt's declared variables |
| `output` | optional | `{ schema, failIfEmpty? }`; absent = text output. On a forEach node, `failIfEmpty` gates **per item**: the failing item's section fails and its downstream item-aligned instances skip; the job continues |

### Binding values and sources

A binding value is a plain source string or `{ "from": <source>, "as": <renderer> }`.

| Source | Meaning | Where valid |
|---|---|---|
| `input:consult_draft` | the physician's draft, verbatim | anywhere |
| `data:<id>` | a scalar data entry's text | anywhere |
| `node:<id>` | another node's output (defines an edge; semantics below) | anywhere |
| `item:<field>` | the current item's field (declared in the collection's `fields`) | forEach nodes only |
| `previous_step_output` | — | **gone**: use an item-aligned `node:` edge |
| `input:sections` | — | **gone**: sections are package data (`data:standards`) |

Renderers unchanged: `concept-bullets` (default for concept-list outputs),
`concept-context`.

### Edge semantics

| Edge | Meaning | v5.0 status |
|---|---|---|
| scalar → scalar | ordinary dependency | allowed |
| scalar → forEach | broadcast: every instance reads the same value | allowed |
| forEach → forEach, same collection | **item-aligned**: instance *i* reads instance *i* | allowed |
| forEach → scalar | aggregate fan-in | **closed** (opens when a workflow needs a summary-over-items node) |
| forEach → forEach, different collections | cross-product ambiguity | closed |

## Execution and provenance

Scheduling is per (node, item): a scalar node starts when its `node:` dependencies
complete; a forEach instance (n, *i*) starts when the scalar dependencies complete
and each same-collection dependency's instance *i* completes — section *i*'s second
step starts the moment *its* first step finishes. Every instance runs on the same
executor as scalar nodes (`run-prompt-node`, agent selected by the output-contract
catalog), so **per-item input/output hashes fall out by construction**, keyed
`<nodeId>:<itemId>`.

**Input-hash semantics change (explicit, versioned)**: with sections
package-determined, the effective-input hash covers **the draft only** for v5
jobs. The provenance record carries `effectiveInputHashVersion: 2` (absent/1 =
the v2–v4 draft+sections definition). Section content is covered by the
`workflowPackage` ref, like all package content.

## The v5.0 closures

1. **Schema catalog match** (from the milestone-5 catalog): every declared schema
   must canonically match a catalog output contract.
2. **Aggregate closed**: a forEach node's output is bindable only by
   same-collection forEach nodes.
3. **Cross-collection closed**: no edges between forEach nodes over different
   collections.
4. **One deliverable**: exactly one `result`, referencing a forEach node.
5. No conditionals, loops, or expression language — decisions are model outputs
   (dag-improvements #3).

## Validation (publish-time and load-time, fail-loud)

Kept from v2–v4: templating gates, template parse + strict-render probes, prelude
resolution, duplicate prompt ids, unused-variable warnings, bindings set-equal
prompt variables, renderer rules, acyclicity (Kahn, manifest-order seeded),
non-blank labels/failIfEmpty, schema declaration/presence/parse/catalog-match,
every prompt referenced by exactly one node.

New in v5 (all blocking): `kind`/`over`/`steps`/`sectionSteps` rejected;
`previous_step_output` and `input:sections` rejected; `derivedFrom` present
(nullable) and, when set, a concrete parseable ref; `result` present, parseable as
`node:<id>`, referencing a forEach node; `forEach` values name declared `data`
collections; `item:` only on forEach nodes and only fields in the collection's
declared `fields`; `data:` references resolve to declared **scalar** entries;
the edge-semantics table above (aggregate and cross-collection edges rejected);
data-table integrity — scalar files exist; collection directories contain a
parseable `index.json` whose `fields` include `id` and `name`, with unique item
ids and every item file present.

## Compatibility and rollout

- The engine accepts `specVersion` ≤ 5. **v2–v4 packages remain valid forever.**
  At snapshot time every spec version lowers to the one-kind form: v2/v3
  synthesize the canonical v4 DAG as before, and a v4 map node lowers
  mechanically to a forEach chain (`steps` → nodes with the step ids;
  `previous_step_output` → item-aligned `node:` edges; the collection is the
  request-supplied section list, fields `id`/`name`/`standard`). One interpreter
  path for every spec version, as always.
- **Sections move server-side**: for v5 packages the job resolves sections from
  the `result` node's collection; the request's `Sections` are not required (and
  are ignored). v2–v4 packages keep the request-supplied sections.
- **Concept sources**: unchanged (the four canonical ids keep their legacy
  stamps; custom ids stamp the node id).
- Rollout order: format layer (this PR, load/validate only) → one-kind
  interpreter (strict drain) → v5 job start + frontend → publish the first v5
  package. Until the interpreter lands, v5 packages validate and load but job
  start rejects them — guard-then-implement, as with v4.

## Out of scope in v5.0

- Aggregate (forEach → scalar) edges; cross-collection edges; multiple `result`s.
- Package sharing / cross-account `derivedFrom` (the `acct-*` owner-only rule
  stands); assisted re-fork UX (#57 territory).
- Per-node model parameters, tool allowlists, budgets; HITL/function kinds
  (executor-axis growth only).
- Eval-gated publishing (#16).

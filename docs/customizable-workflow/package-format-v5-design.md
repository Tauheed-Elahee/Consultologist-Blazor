# specVersion 5 Design: Fork Model, Data Collections, One-Kind Nodes (Not Implemented)

Recorded 2026-07-15 at the close of the post-milestone-4 exploration. Status:
**design capture; implementation deliberately deferred** — tracked by the
"specVersion 5" issue. Four exploration threads converged into one format generation;
designed together they carry none of the compromises each would carry alone:

1. forks kill the account override that forced standards into the input hash;
2. `data/` collections make per-item bindings statically validatable;
3. statically-known collections make `forEach` nodes checkable, killing the map
   container;
4. one-kind execution completes the per-item provenance chain by construction.

Supersedes in part: the standards sections of
[in-app-editing.md](in-app-editing.md) (the override layer retires; the editor's
publish contract becomes v5-shaped) and the two-kind decision in
[output-contract-catalog.md](output-contract-catalog.md) (whose catalog itself stays
orthogonal and valid). Subsumes issue #53 — the prose-step unification happens
through one-kind execution rather than as separate surgery.

## 1. Fork-everything customization + `parent` lineage

All customization — standards included — is authoring a **new immutable package
version** (the in-app-editing model, extended to everything). The account standards
override (`consult.sectionStandardsMarkdown`) and its client-side markdown
parse/merge retire entirely: one editing model, one Publish button, one provenance
regime.

**The manifest gains `parent`** — a single **concrete** ref (never `@latest`)
recording the *fork origin*:

- `parent: null` for Consultologist-provided roots (`general`).
- `acct-x@v2`'s parent stays `general@v2026.07.5` across that account's own version
  bumps; a *rebase* updates it (e.g. to `general@v2026.08.1`). The within-name
  predecessor is already implicit in the CalVer sequence.
- The full derivation chain (`acct-b@v1 → acct-a@v4 → general@v2026.07.5 → null`)
  is **reconstructed by walking parents, never stored as an array** — derivable data
  stored redundantly can lie (the same rule that rejected the authored edge list).

**The fork model's one real cost, named**: overrides layered on a moving default
(`@latest`); forks freeze at their parent, so upstream improvements stop flowing the
moment anything is customized. `parent` is the metadata that makes this tractable —
the editor can detect "your package derives from `general@v2026.07.5`;
`v2026.08.1` is available" and offer the v1 answer: **assisted re-fork** (re-fork
from the new upstream, replay the parent→fork diff, surface conflicts). A true
three-way merge is a later luxury. This cost only bites standards (the only layer
that ever floated); prompts and steps never layered.

Cross-account derivation (user forking another user's package) implies a
**package-sharing design** — visibility, discoverability, trust in someone else's
prompts. Explicit non-goal here; the `acct-*` owner-only access rule
(in-app-editing.md) stands, and `parent` is forward-compatible with sharing.

## 2. `data/` folder + `data:` binding namespace

Package-shipped content that binds into prompts without being a prompt — the prelude
mechanism's honest successor. Manifest gains a `data` table (like `schemas`);
files live under `data/`. The namespace rule that keeps provenance coherent:

| Source | Provenance regime |
|---|---|
| `input:…` | effective-input hash (what the physician supplied) |
| `item:…` | the current collection item (regime follows the collection's source) |
| `node:…` | per-node output hashes |
| `data:…` | the package ref (versioned workflow content) |

**Standards become a data collection**: `data/standards.json` as an index —
`[{ "id": "hpi", "name": "History of Present Illness", "file":
"data/standards/hpi.md" }]` — with per-standard `.md` files (the prompts-pattern
spelling: declarations + prose files; JSON-inline prose was rejected as
authoring-hostile). With the override layer gone, standards are fully
package-determined: the input/data straddle that milestone-era provenance carried
(standards hashed as input *because* the unversioned override made them vary outside
any artifact) **dissolves**. Collections declare their item shape, so `item:` fields
become statically validatable instead of engine-hardcoded.

Other data enters the same way: clinic guideline excerpts, specialty glossaries,
letter scaffolds — any static reference text, versioned and diffed independently of
the templates that consume it.

**Input-model consequence (must be explicit, never silent)**: with sections
package-determined, the effective-input hash can shrink to the draft — the sections'
content is redundant with the package ref. That is a provenance-semantics version
change; the hash definition gets versioned (or the old coverage is retained for
continuity) as an explicit implementation decision. Per-consult section *selection*,
if ever wanted, returns to the input side as an id-filter against the package
collection.

## 3. One node kind with `forEach`

The node calculus (output-contract-catalog.md) established that executor and
multiplicity are a node's only real dimensions and the floor is one kind; v5 is the
moment the one-kind spelling becomes the *simpler* design, because `data:`
collections make it validatable. The map container, its `steps` array,
`previous_step_output`, the v4→v3 lowering, and the `run-prose-step` activity all
retire. The canonical section pipeline respelled:

```json
{ "id": "draft-section", "forEach": "data:standards",
  "prompt": "standard-section-draft", "label": "Drafting section",
  "bindings": { "section_name": "item:name",
                "patient_trajectory_concepts": { "from": "node:create-patient-trajectory", "as": "concept-context" } } },

{ "id": "apply-patient-info", "forEach": "data:standards",
  "prompt": "patient-section-draft", "label": "Applying patient information",
  "bindings": { "standard_section_draft": "node:draft-section",
                "consult_draft": "input:consult_draft",
                "section_name": "item:name" } },

{ "id": "apply-instructions", "forEach": "data:standards",
  "prompt": "section-instructions", "label": "Applying section instructions",
  "bindings": { "patient_section_draft": "node:apply-patient-info",
                "section_name": "item:name",
                "section_standard": "item:content" } }
```

`previous_step_output` is gone: a `node:` edge between two nodes sharing the same
`forEach` collection is **item-aligned** — instance *i* reads instance *i*. The step
chain is plain graph structure.

**Edge semantics** (relational rules replacing the container's structural closures):

| Edge | Meaning | v5.0 status |
|---|---|---|
| forEach → forEach, same collection | item-aligned | allowed (the step chain) |
| scalar → forEach | broadcast (every instance reads the same value) | allowed (concept-context into every section) |
| forEach → scalar | **aggregate** (collection fans into one prompt) | **closed** — the old "map is terminal" closure as a rule; opens when a workflow needs e.g. a summary-over-all-sections node |
| forEach → forEach, different collections | cross-product ambiguity | closed — no honest semantics yet |

**Scheduling** generalizes to per-(node, item) readiness: section *i*'s second step
starts the moment *its* first step completes; the milestone-4 "map runs after all
prompt nodes" conservatism disappears naturally. Execution unifies on
`run-prompt-node` — **per-item, per-node provenance hashes fall out by
construction**, completing the #53 goal.

**Workflow-result contract**: with no container to define "the section outputs", the
manifest names the deliverable — `result: "node:apply-instructions"` — the per-item
outputs that section-completed events, entity section states, and the generated
consult hang off.

### Why a property and not a container (decision rationale, 2026-07-15)

The question "should `forEach` be a container loop or a node property?" was asked
directly and deserves its answer recorded, because someone will re-ask it.

Steelmanned, the container's advantages are real: lexical locality (the per-item
program reads top-to-bottom in one box), free closures (aggregate-not-bindable
because the container has no output id; `item:` scoping is lexical; one-map-only is
counting), an implicit result contract (the last step is the deliverable), and
author-proofing (chain membership is positional, so "forgot `forEach` on one node"
and "mismatched collections" cannot happen). The property's advantages: one node
kind (no second, lesser step species with parallel machinery for ids, bindings,
execution, SSE, and provenance), `previous_step_output` reduced to an ordinary
item-aligned edge, per-item *graphs* rather than only linear chains (a `steps` array
is structurally a list forever), and a natural opening for aggregation (an edge rule
to relax, versus inventing an aggregate output for a box).

**The sorting observation that decides it**: every container advantage is
*presentational* — locality, groupedness, implicit ordering, a friendly mental
model. Every property advantage is *semantic* — uniformity of execution, provenance,
vocabulary, and growth room. Presentation can always be derived from semantics;
semantics can never be retrofitted onto presentation. (The same asymmetry decided
the authored-edge-file question: store the minimal truth, generate the human view.)

Therefore:

- **The manifest stores the property spelling** — one kind, `forEach`, flat list,
  with the authoring convention of keeping a chain's nodes contiguous.
- **Every human surface renders the container**: the derived DAG diagram draws
  same-collection forEach chains as a grouped subgraph ("per section" box), and the
  in-app editor's steps tab *is* a container UI — an ordered list that reads and
  writes `forEach` nodes underneath. Authors in the editor never perceive the flat
  spelling.
- **The closures survive as validator rules** (aggregate closed, cross-collection
  closed, `item:` requires `forEach`, chained nodes must share a collection) — more
  rules than the container needed, each a one-liner, and relaxable one at a time
  where a container must be demolished all at once.
- **Both spellings are NOT accepted as input dialects** — two ways to write the same
  semantics is the keep-format-dumb violation. v4 containers remain valid forever
  via synthesis (they lower mechanically to forEach chains); v5 packages write only
  the property spelling.
- The container's one irreplaceable feature — the implicit result — costs a single
  explicit `result:` field, which is an improvement anyway: a workflow's deliverable
  deserves to be named, not inferred from which box something happened to be last in.

In short: the container was the right training wheels for v4 (it made the closures
trivially sound while the interpreter was new); the property is the right skeleton
for v5, with the container living on as the derived view and the editor metaphor.

## 4. Migration sketch (for the eventual implementation plan; not planned here)

- v4→v5 synthesis is mechanical: the container map lowers to forEach nodes
  (steps → nodes, `previous_step_output` → item-aligned `node:` edges,
  `item:standard` → `item:content`); `standards.md` parses once into the collection
  spelling. One interpreter path for all spec versions, as always.
- Entity/SSE generalize to per-item node status (SchemaVersion bump;
  `section-prose-step` folds into per-item `node-completed`); strict drain window.
- The in-app editor (#57) is the UX that makes fork-everything one-click
  (fork-on-first-edit; the "My standards" tab becomes per-section editing of the
  fork's `data/standards/*`); its content/publish contract carries `data/` and
  `parent`.
- The output-contract catalog (#55 PR 1) is orthogonal and can land before or after.

## 5. Relationship to shelved work

| Item | Effect |
|---|---|
| #53 (prose-step unification) | **Subsumed** — achieved by one-kind execution |
| #55 PR 2 (unification via catalog) | Subsumed with it |
| #55 PR 1 (output-contract catalog) | Orthogonal; unaffected; still the enabler for new output shapes |
| #57 (in-app editor) | Reframed onto the fork model; standards tab and publish contract become v5-shaped; the editor is v5's UX half |
| Deferred SCTID pattern, HITL/`function` kinds, eval gating (#16) | Unchanged; kinds still grow only along the executor axis |

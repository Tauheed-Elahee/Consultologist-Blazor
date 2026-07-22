# Workflow Package Format — specVersion 6 (Multi-Collection, Aggregators, One Document)

Normative specification for `specVersion: 6` packages, implemented for
Milestone 9 (#116; design and rationale:
[package-format-v6-design.md](package-format-v6-design.md)). v6 is v5 plus
three coupled moves: **multiple collections** (independent for-loops),
**aggregator nodes** (the only aggregation), and **one assembled document**
as the deliverable. Everything not stated here is unchanged from
[package-format-v5.md](package-format-v5.md) — prompts, preludes, Scriban
rules, `data/` collections and scalars, bindings, renderers, `derivedFrom`,
CalVer, immutability, schema-catalog welding.

A manifest declares the rule set it was validated under: `specVersion: 5`
packages keep validating and executing under the frozen v5 rules; the engine
accepts exactly {5, 6}. A package needs 6 only when it uses what 6 opens.

## Aggregator nodes

One new node property, in the one-kind-with-properties spirit (`forEach`
makes a node multiple; `aggregate` makes it deterministic):

```json
{ "id": "assemble-note",
  "label": "Assembling note",
  "aggregate": ["node:section-instructions"] }
```

- `aggregate` is a non-empty **ordered list** of `node:` refs. An aggregator
  declares **no** `prompt`, `bindings`, `output`, or `forEach`.
- Execution is deterministic and engine-side — no agent call, no retries.
  The output is a first-class node output: input hash = SHA-256 of the
  canonical JSON array of source output hashes in aggregation order; output
  hash = SHA-256 of the rendered text.

### Rendering (normative bytes)

Sources render in declared order, joined by blank lines:

- A **forEach source** renders as labeled blocks in collection index order:
  `## ` + the item's `name`, blank line, the instance's output text
  (trimmed), blank line before the next heading.
- A **scalar source** (prompt node or aggregator) renders as its output
  text (trimmed), verbatim — no heading.

No prologue, no epilogue, no trailing newline. The bytes feed hashes and
downstream prompt inputs, so they are spec (pinned by
`AggregateRendererTests`).

### Failure (normative)

Aggregators **fail loud**: if any contributing instance failed or was
skipped, the aggregator fails naming the item(s) and never composes a
partial document. Failure cascades downstream; a v6 job whose result
aggregator cannot compose is a **Failed job** carrying the aggregator's
error — per-block outputs still record.

## Edge semantics

| Edge | Rule |
|---|---|
| forEach → same-collection forEach (binding) | item-aligned (unchanged) |
| forEach → different-collection forEach | rejected (unchanged) |
| forEach → scalar prompt node | **rejected** — aggregation is never implicit in a binding |
| forEach → aggregator (listed source) | the aggregate edge |
| scalar prompt node / aggregator → aggregator (listed source) | its output, verbatim |
| aggregator → anything (bound `node:<id>`) | broadcasts like a scalar |
| `as` renderer on any aggregate-related edge | rejected |

## The deliverable

`result` **must reference an aggregator**; its rendered output is the
assembled document — the job's one deliverable. **Blocks** replace sections
as the unit model: the result aggregator's expansion in source order — one
block per (forEach source, item) under composite `nodeId:itemId` ids, one
block per scalar source under the node id. Setup listing, streaming, and
progress enumerate blocks; the job's `workflowOutputHash` uses **definition
v2** (SHA-256 of the document's UTF-8 bytes; v1 remains for v5 jobs).

## The v6 closure set

Kept from v5: schema-catalog match; cross-collection item edges closed; one
`result`; no conditionals/loops/expressions; orphan prompts are errors —
each prompt must be referenced by **at least one** node (v6 relaxes v5's
exactly-one rule: a prompt may be shared by several nodes, each binding the
prompt's variables itself); acyclicity (aggregate edges included);
binding/variable set equality; per-item renderer rules; templating gates.

New in v6:

- **Reachability** — every node must transitively feed the result through
  binding or aggregate edges; a disconnected node or chain is a publish
  error. The result must transitively include at least one forEach source.
- **Aggregator well-formedness** — non-empty existing `node:` sources; no
  prompt-family fields.
- **Aggregation is explicit** — only through aggregator nodes.

Relaxed from v5: the single-collection rule (forEach nodes may iterate any
declared collection — independent parallel loops) and the aggregate
closure (via aggregator nodes, above).

## Out of scope in v6

forEach nodes consuming aggregates (staged chains); multiple `result`s;
renderers on aggregate edges (including a JSON aggregate form); declared
inputs beyond `input:consult_draft`; cross-collection item-aligned
semantics. Sketches: design doc § 11.

## The canonical package

`packages/general` is the canonical pipeline in v6 form since v2026.07.7:
the v5 nodes byte-stable plus the `assemble-note` result aggregator. The
consult ships as one assembled document; `packages/general/dag.mmd` is the
generated diagram (snapshot-pinned).

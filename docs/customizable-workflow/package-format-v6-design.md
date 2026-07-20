# specVersion 6 Design: Multi-Collection Fan and Aggregate Edges (Not Implemented)

Recorded 2026-07-20 at the opening of Milestone 9 (#152; design issue #153,
implementation #116). Status: **design settled, not implemented**. v6 is one
move: relax the v5.0 single-collection closure — and take the one further step
that relaxation forces. Working alias during Milestone 9 planning was "5.1";
the manifest field is an integer, so the step ships as `specVersion: 6`.

Decisions fixed in the 2026-07-20 design round (recorded on #152/#153):
integer version bump to 6 with the engine accepting {5, 6}; general-purpose
scope (minimal correct semantics, no single driving vertical); **aggregates
fail loud**; **labeled-blocks aggregate rendering**.

## 1. Motivation: why relaxing one rule requires opening another

v5.0 closes three things at once (package-format-v5.md § closures): aggregate
edges (a forEach node's output is bindable only by same-collection forEach
nodes), cross-collection edges, and — because of those two — multiple
collections themselves. The validator's own comment states the dependency:
with aggregate and cross-collection edges closed, a second collection's
forEach chain would be *disconnected* — publishable but unreachable, since
nothing outside it could ever consume its outputs. Deleting the
single-collection check alone would produce packages that validate but cannot
matter.

So v6 is two coupled relaxations, the second licensing the first:

1. **Multiple collections**: forEach nodes may iterate different declared
   collections; each collection with at least one forEach node gets its own
   item fan.
2. **Aggregate edges**: a **scalar** (non-forEach) node may bind a forEach
   node's output, meaning *the aggregate of all its instances*. This is the
   bridge that makes a secondary chain consumable: collection B's chain →
   aggregate into a scalar node → broadcast (unchanged v5 semantics) into
   collection A's chain.

The scope is deliberately general (design decision): per-item chains plus
aggregate-into-scalar is the minimal correct connective tissue. Concrete
verticals — reference corpora summarized into drafting context, structured
patient lists reconciled into a summary variable — are expressible with it,
but none of them shapes the format.

## 2. Vocabulary: no new syntax

v6 adds **no manifest fields and no binding forms**. The whole step is a
change in what existing forms are allowed to mean:

| Binding | v5.0 | v6 |
|---|---|---|
| forEach node → same-collection forEach node (`node:<id>`) | item-aligned edge | unchanged |
| forEach node → different-collection forEach node | rejected | **still rejected** |
| forEach node → scalar node (`node:<id>` bound by a scalar) | rejected ("aggregate closed") | **the aggregate edge** |
| scalar → anything | broadcast | unchanged |
| `forEach:` values | all name ONE collection | may name different collections |
| `as` renderer on an aggregate binding | n/a | **rejected** (concept renderers stay per-item; future work) |

`result` still names exactly one forEach node; that node's collection remains
the section source ("sections are server-resolved" is untouched).

## 3. Aggregate semantics

### Rendering (normative)

An aggregate binding renders the source node's instance outputs as **labeled
blocks**, in **collection order** (the order of `index.json` items — the same
order the fan uses):

```
## {item name}

{instance output text}

## {next item name}

{instance output text}
```

Exactly: for each item, a line `## ` + the item's `name` field, a blank line,
the instance's output text, a blank line before the next heading. No prologue,
no epilogue, no trailing newline beyond the final output's own. This format is
**normative, not advisory**: the rendered text is a prompt input, so it flows
into the consuming node's input hash — reproducibility requires the bytes to
be pinned by the spec, exactly as Scriban rendering is.

Rationale (design round): labeled blocks are readable by the model *and* by a
human inspecting a job's prompt inputs in History; item names give the model
stable referents. Plain concatenation and structured JSON were considered and
declined — JSON remains available later behind a renderer once aggregate
renderers open.

### Failure (normative)

**Aggregates fail loud.** If any instance of the source node failed (or was
skipped by upstream failure), the aggregate binding's consumer fails with an
error naming the failed item id(s); it does not run with a partial aggregate.
Failure then cascades downstream of the consumer through the existing skip
propagation. Per-item independence *within* a chain is unchanged — one
section's failure still leaves sibling sections completing; what it can no
longer do is silently thin out an aggregate someone downstream consumed.

Rationale (design round): a consult built on a silently-reduced aggregate is a
provenance hazard — the record would look complete while resting on missing
context. Fail-loud matches the validator philosophy and is the clinically
conservative default. A "survivors + marker" mode was considered and declined
for v6.

## 4. The fan: per-collection item sets

The orchestration input's single `Items` snapshot (the result collection)
becomes **one item set per fanned collection**, all snapshotted at job start
(the operational-snapshot species — Durable replay determinism, unchanged
taxonomy). Shape note for #116: the v5 single-set shape must keep
deserializing (records and in-flight jobs), e.g. by keying sets per
collection with the v5 shape mapping to the result collection's key.

Instance keying is already collection-agnostic: `nodeId:itemId` composite
keys work unchanged because a node belongs to exactly one collection.
Sections remain the result node's collection — the job's deliverable count
and identity do not change meaning.

## 5. Readiness

- **forEach instance** (unchanged): every same-collection `node:` dependency
  item-aligned complete, every scalar dependency complete.
- **Scalar node**: every dependency settled — where a dependency on a forEach
  node (the aggregate edge) means **all instances of that node completed**;
  any failed/skipped instance makes the scalar fail per § 3 rather than run.

Acyclicity over the node graph (edges = bindings, aggregate edges included)
is still required and still checked with the same Kahn pass; an aggregate
edge from a chain back into a node that feeds the same chain is a cycle and
rejected like any other.

## 6. The v6 closure set

Kept from v5.0:

1. Schema catalog match — every declared schema canonically matches a catalog
   output contract.
2. Cross-collection item edges closed — no forEach → forEach edges across
   collections.
3. One deliverable — exactly one `result`, referencing a forEach node.
4. No conditionals, loops, or expression language.
5. Orphan prompts are errors; a prompt is referenced by exactly one node.
6. Acyclicity, binding/variable set-equality, renderer rules (per-item),
   templating gates — all unchanged.

Relaxed:

7. ~~All forEach nodes share one collection~~ → forEach nodes may iterate
   any declared collection.
8. ~~Aggregate closed~~ → scalar nodes may bind forEach nodes (aggregate
   edges, § 3 semantics).

Still out of scope in v6 (future steps, not promises):

- forEach nodes consuming aggregates (staged chains).
- Multiple `result`s / multiple deliverables.
- Renderers (`as`) on aggregate bindings, including a JSON aggregate form.
- Cross-collection item-aligned semantics (zip/cartesian) — no known need.

## 7. Progress and provenance

- **Run rail / SectionSteps**: the per-section step list stays the *result
  collection's* chain — sections are the deliverable and their progress
  model is untouched. Secondary chains surface as node-level progress
  (node-completed when all instances settle), exactly how non-forEach nodes
  present today.
- **History grid**: generalizes without a format change — NodeOutputs are
  already keyed `nodeId:itemId`, so a secondary chain renders as a chevroned
  per-item group under its node, identical to the main chain's groups. The
  Setup/Done framing, hashes, and convergence markers apply as-is.
- **Hashes**: an aggregate is a prompt input like any other — the rendered
  labeled-blocks text feeds the consumer's input hash. Because the rendering
  is normative (§ 3), the hash is reproducible from the record's refs. No new
  record species: refs / operational snapshots / derived projections cover
  everything (provenance.md taxonomy unchanged).

## 8. Versioning mechanics

- A manifest declares the rule set it was validated under: `specVersion: 5`
  packages validate under the v5.0 closures (frozen), `specVersion: 6` under
  § 6. A package needs 6 only when it uses what 6 opens; `general` stays 5
  until it actually goes multi-collection.
- **The engine accepts exactly {5, 6}**; the store's acceptance and the
  package selector's integer spec gating (#137) extend naturally.
- The publisher stamps the declared version it validated; it never upgrades
  a manifest's declaration.
- package-format-v5.md remains the frozen v5 normative spec and gains a
  header note pointing here (the v2–v4 archive pattern); the v6 normative
  spec is written when #116 implements.

## 9. Editor implications (forward pointers)

- **#154 "+ data folder"** becomes meaningful: a new collection is
  publishable *and consumable* once a chain iterates it and an aggregate
  bridges it. The #115 membership machinery (added-items model, index
  composition, per-collection paths) generalizes; the single-collection
  assumption in the editor's compose step is the main seam.
- **#117 node editing** speaks this vocabulary: forEach assignment offers
  any declared collection; binding sources offer aggregate edges exactly
  where § 2 allows them (scalar consumers only); the graph diff preview's
  existing removed/added coloring carries over.
- The binding source dropdowns' option builder (BindingSourceEditor) gains
  the aggregate case: `node:<forEach-id>` options appear for scalar nodes,
  labeled as aggregates.

# Workflow Package Format — specVersion 7 (Declared Inputs, Result Set)

Normative specification for `specVersion: 7` packages — the **format
closure**, as implemented by the v7 format-and-validator work (design and
rationale: [package-format-v7-design.md](package-format-v7-design.md)).
Everything not stated here is unchanged from
[package-format-v6.md](package-format-v6.md): aggregator nodes and their
normative rendering bytes, edge semantics, multi-collection fans, prompt
sharing, schema-catalog welding, `derivedFrom`, CalVer, immutability.

A manifest declares the rule set it was validated under; the engine accepts
exactly **{5, 6, 7}**. v5 and v6 packages keep validating and executing
under their frozen rules — including the fixed `input:consult_draft`
vocabulary and unchanged block ids. A package needs 7 only when it uses
what 7 opens.

> **Executability**: while the v7 engine work is in flight, the format
> layer (validation, publish, load) accepts specVersion 7 but job start
> rejects it with `specVersion 7 execution is not yet enabled` (HTTP 422).
> The engine sub-issue removes that guard; this note goes with it.

## Declared ids (shared grammar)

Input ids and result ids share one grammar: `^[a-z][a-z0-9_]*$` —
snake_case, letter-first (`WorkflowDeclaredIds`). Result ids additionally
feed delivery filenames, where the absence of `-` keeps
`{resultId}-{jobId8}` unambiguous. Node ids keep their existing convention
untouched.

## `inputs` — declared input slots

```json
"inputs": [
  { "id": "consult_draft", "label": "Consult draft" },
  { "id": "prior_notes", "label": "Prior notes", "required": false }
]
```

- **Required section, at least one slot.** A v7 package always states its
  intake form; `inputs: []` is invalid.
- Per slot: `id` (grammar above, unique), `label` (non-blank, the UI field
  label), `required` (default **true**).
- `consult_draft` is the *conventional* primary slot name. It carries no
  engine meaning: a v7 package that does not declare it rejects
  legacy-field requests like any other undeclared id.
- On v5/v6 manifests the section is an error (`inputs requires
  specVersion 7`).

### `input:` bindings

`input:<id>` parses **structurally** (any well-formed name), like `item:`
and `data:`; the vocabulary closure is a validator rule:

- v7: every `input:` binding must name a declared id — the error lists the
  declaration (`undeclared input 'x' (declared: …)`).
- v5/v6: the vocabulary stays exactly `{consult_draft}` with the original
  error text (`unknown input 'x' (expected consult_draft)`).

## `results` — the result set

```json
"results": [
  { "id": "consult_note", "node": "node:assemble-note", "label": "Consultation note" },
  { "id": "patient_letter", "node": "node:assemble-letter", "label": "Patient letter" }
]
```

- **Optional**; the string `result` form remains valid as sugar for a
  one-entry set with `id: "consult"` and the node's label — which keeps
  single-result delivery filenames identical to v6's
  (`consult-{jobId8}.pdf`). Declaring **both** `result` and `results` is
  an error; declaring **neither** is an error.
- Per entry: `id` (grammar above, unique), `label` (non-blank), `node`
  (`node:<id>` of an existing **aggregator**). Result nodes are distinct —
  two results never share one aggregator.
- On v5/v6 manifests the section is an error (`results requires
  specVersion 7`).

## Reachability (union-rooted)

v6's reachability closure, generalized to the result set:

- Every node must transitively feed **a** result node through binding or
  aggregate edges (BFS from the union of result nodes).
- **Each** result must transitively include at least one forEach source: a
  deliverable with no fan has no consult.

## Blocks (deliverable dimension)

For v7 packages, block ids carry the deliverable: each result expands in
result-set order, forEach sources contributing
`{resultId}:{sourceNodeId}:{itemId}` per item and scalar sources
`{resultId}:{sourceNodeId}` — so two deliverables sharing a source never
collide. The one-entry sugar yields the `consult:` prefix. **v5/v6 block
ids are unchanged.**

## Resolution contract

The store resolves a v7 package with a **result set** (declared entries,
or the sugar's one entry). `ResultNodeId` is populated only when the set
is single, so a consumer that is not yet set-aware fails loud on
multi-deliverable packages instead of silently picking one.

## Request contract and provenance

The job-side contract — the request `inputs` map with `consultDraft`
back-compat, per-input validation and size caps, effective-input hash v3,
per-deliverable execution state, and workflow-output hash v3 (including
the canonical-JSON byte-level rules both hashes require) — lands with the
**v7 engine and provenance work** and is specified there when it does.
Until then, [package-format-v7-design.md](package-format-v7-design.md)
§§ 3–6 records the agreed design. A hash definition is its bytes; this
spec deliberately freezes none before the code that computes them exists.

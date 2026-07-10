# Improvements for the Fully Customizable DAG

Opinions recorded 2026-07-09, assuming the DAG-as-data milestone (milestone 4 of
[workflow-packages.md](workflow-packages.md)) is implemented. Mostly about what to build
*around* the graph rather than the graph mechanics. In priority order; if only two are
built, pick #1 and #2 — structured-output edges make the DAG sound, eval-gated
publishing makes it safe for clinicians to build on.

## 1. Structured outputs as the edge type; delete the regex parser

Make every analysis step declare a JSON Schema for its output and run the model in
schema-constrained mode (DeepSeek supports structured output). The schema *is* the edge
contract; "validation warnings" become schema violations with precise paths; and the
most brittle component of the current system (`ConsultGenerationConceptParser`'s
bullet-format regex, silently dropping malformed lines) disappears. Also the
prerequisite for harness independence ([provenance.md](provenance.md)) — it pays twice.

## 2. Publish-time validation and eval-gated publishing

A package must be provably runnable before it reaches the registry. CI checks: graph is
acyclic, every edge's producer schema satisfies its consumer, every template variable is
bound, budgets are sane. Then gate publishing on **golden fixtures**: each package ships
a sample consult draft + assertions, and CI runs the workflow against the pinned model
before publishing. A clinician tuning specialty prompts learns "your change broke
concept extraction" at publish time, not from a failed production job. This is the
workflow analog of the AI-Copyright-Reproducibility harness and the highest-leverage
idea here.

## 3. Keep the format deliberately dumb

The failure mode of homegrown workflow engines is accreting conditionals, loops, and
expression languages until the package format is a bad programming language. Allow only:
nodes, typed edges, and one declarative **map/fan-out node type** — which also
un-special-cases the per-section fan-out (sections become "map this subgraph over the
section list"). If a workflow needs a decision, a *model step* makes it and emits it as
data; branching does not go in the format. Expressiveness can be added later; it can
never be removed.

## 4. A human-in-the-loop node type

The one genuinely new capability the DAG unlocks: a node that pauses the orchestration
until the physician reviews or edits an intermediate artifact (e.g. the extracted
concepts) before downstream steps consume it. Durable Functions is built for this
(external events + durable timers); the SSE/entity infrastructure already streams
intermediate state to the UI; and the product promise — "keep the physician in
control" — is currently only true at the very end of the pipeline.

## 5. Step-level memoization keyed by content hash

Steps are near-pure functions of (inputs, prompt, params, model version) — all already
hashed for provenance. Cache on that key to get partial re-runs for free: edit a
prose-step prompt and a re-run reuses the four analysis stages verbatim. For package
authors iterating on one step of a six-step workflow, this is seconds vs. minutes and
pennies vs. dollars per iteration.

## 6. Per-step provenance, not just per-job

Record each step's input/output hashes to form a verification chain: two runs that
diverge can be pinpointed to the exact step where they split, and a re-run harness can
prove agreement step-by-step rather than only end-to-end. A small extension of the
per-job record in [provenance.md](provenance.md), and what makes cross-harness
verification tractable.

## 7. Safety rails in the format

- **`spec_version`** the orchestrator refuses to exceed — the format will evolve, and
  in-flight jobs must not care.
- **Per-step tool allowlists** — the prose steps have no business calling `ecl_query`;
  least privilege per node, and the allowlist lands in provenance.
- **Per-step token/time budgets** — a bad package version must not burn a fortune
  before anyone notices.

# Customizable Workflow — Design Notes

Design discussion from 2026-07-09 on turning the consult-generation workflow and section
template system into versioned, code-independent artifacts. Nothing here is implemented
yet; these notes are the input to a future milestone plan.

## The goal

1. Update default templates and workflows completely separately from the codebase, with
   the defaults versioned the same way model weights are versioned (immutable, pinned,
   published).
2. Bundle collections of defaults tailored per specialty (e.g. breast oncology,
   cardiology) instead of a single hardcoded default workflow.
3. Record full provenance per consult so any other harness can re-run the system from
   published artifacts: open model weights, workflow definition, SNOMED edition, and the
   Apache-2.0 MCP server code — with no dependency on this web app.

## Contents

- [current-state.md](current-state.md) — what is hardcoded vs. data-driven today
  (verified against the code)
- [workflow-packages.md](workflow-packages.md) — the versioned "workflow package"
  concept, registry, pinning, specialty bundles, and the milestone sequencing
- [provenance.md](provenance.md) — the per-consult provenance record: fields, sources,
  attestation checks, and reproducibility limits
- [dag-improvements.md](dag-improvements.md) — prioritized improvements for when the
  DAG-as-data milestone is built (structured-output edges, eval-gated publishing,
  human-in-the-loop nodes, memoization, per-step provenance, safety rails)
- [product-stages.md](product-stages.md) — where this fits in the larger product: the
  workflow vision as stage 1 of four (engine → learning loop → verification layer →
  outputs and platform)
- [completeness.md](completeness.md) — what remains after all the stages (governance,
  regulatory track, evidence, the moving substrate) and the marker for "truly done
  building"
- [package-format-v2.md](package-format-v2.md) — normative specVersion-2 format:
  prompt templates (Scriban), variable contract, strictness rules, rollout
- [package-format-v3.md](package-format-v3.md) — normative specVersion-3 format:
  package-defined section steps, declarative bindings, fallback retirement
- [content-repos.md](content-repos.md) — splitting workflow and agent content into
  dedicated GitOps repos publishing to the registry (CI-only writes, full agent GitOps,
  migration checklist)
- [registry-operations.md](registry-operations.md) — browsing and inspecting the
  package registry (az CLI, portal, Storage Explorer) and the publish flow
- [decoupling-roadmap.md](decoupling-roadmap.md) — post-milestone-2 forecast: phase
  plan for milestones 3–4, the refactoring bill, risks, and the precise end-state
  boundary of the "generic engine" claim
- [dag-as-data-design.md](dag-as-data-design.md) — milestone 4 phases B–D at
  implementation resolution (specVersion-4 nodes/schemas, interpreter, migration),
  shelved behind the roadmap gate; phases 0/A (file split, structured outputs) execute

## Key terms

- **DAG** (directed acyclic graph): the workflow expressed as steps (nodes) and
  data-dependency arrows (edges), no cycles. Today's pipeline is a DAG expressed in C#
  control flow; "DAG customizable" means promoting that structure to data.
- **Workflow package**: a named, versioned artifact bundling section standards, prompt
  templates, per-step model parameters, and (eventually) the step/DAG definition.
- **Attestation**: a runtime check that a deployed, mutable thing (the Foundry agent,
  the MCP app) matches its git-tracked source of truth; fail or warn loudly on drift.

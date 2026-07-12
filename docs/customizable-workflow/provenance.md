# Per-Consult Provenance Record

> **Status (2026-07-09)**: milestone 1 records the first three fields on every job —
> `WorkflowPackage`, `EffectiveInputHash` (SHA-256 over canonical draft+sections JSON,
> computed server-side at job start), and `AgentVersion` — snapshotted into the
> orchestration input and exposed on the job response (`SchemaVersion` 2). The remaining
> fields land with later milestones.

Every consult generation records the versioned artifacts that produced it. Conceptually:

```
model_weights_version - agent_version - SNOMED_version - mcp_version - DAG_workflow_version - input_hash
```

Store it as a **structured JSON object on the job** (version labels will eventually
contain hyphens; derive a string key from the object when needed).

## Fields and their sources

| Field | Content | Source of truth |
|---|---|---|
| `model_weights_version` | Exact checkpoint identity of the model (DeepSeek V4 Pro at a pinned revision, e.g. HF repo + revision hash), not just the family name | Published weights registry |
| `model_parameters` | Sampling params and the reasoning toggle (see below) | Git-tracked agent config / per-step package params |
| `backend_fingerprint` | Serving-stack fingerprint per run, if the API exposes one | Response metadata |
| `agent_version` | Foundry agent version (currently `test-json@45`) | Foundry + git attestation (below) |
| `snomed_version` | Terminology edition + version + import date | MCP `get_terminology_info` (returns exactly this) |
| `mcp_version` | Release (git tag) of the Apache-2.0 `snomed-snowstorm-mcp` repo | Git tag; deployed app should attest its build (e.g. info endpoint returning the commit) |
| `workflow_package` | `name@version` of the pinned workflow package (CalVer `vYYYY.MM.N`, e.g. `general@v2026.07.1`) | Package registry |
| `input_hash` | Hash of the **effective** input | Computed at job start |
| `inference_stack` (optional) | Inference engine + version, for bit-exact re-run attempts | Deployment config |

## Attestation: git as source of truth for mutable deployments

> **Implemented 2026-07-10** for the agent: `AgentAttestationService` (hosted service in
> the Api) loads the bundled `agents/{name}.yaml` at startup, fetches the deployed agent
> version from the Foundry API with the app's managed identity, and compares model,
> instructions, reasoning effort, tool choice, and tools. Drift logs as an error;
> `AgentAttestation__Enforce=true` fails the host instead. The MCP-deployment
> attestation remains future work.

The Foundry agent (system prompt, parameters, tool wiring) is edited in a portal and is
therefore mutable state. Rule: **track the agent config in git; at startup or job start,
fetch the deployed agent version and verify it matches the tracked manifest** — fail or
warn loudly on drift. This makes `agent_version` and the git content redundant by
construction, which is the point: the check keeps them honest. The same pattern applies
to the MCP deployment.

Planned evolution ([content-repos.md](content-repos.md)): the attested manifest moves
from the app package to a registry written **only by CI** from a dedicated agents repo
(GitHub→Azure OIDC; humans read-only). The CI-only-write restriction is the
precondition — a registry humans can write would make the attestation tautological.

## The effective-input hash

The input is not just the consult draft. The request reaching the orchestrator is
draft + section list + standards, and account-level overrides change the standards.
Hash the **fully resolved content** (draft + resolved sections/standards after
overrides). Cleanest rule: overrides produce an ephemeral package-content hash recorded
alongside the pinned package version — otherwise two different runs can claim the same
input.

**Prompts are not part of the input hash.** Since specVersion 2
([package-format-v2.md](package-format-v2.md)) the prompts are package content, covered
by the `workflowPackage` provenance field; the effective-input hash continues to cover
only draft + resolved sections/standards. Two jobs with the same hash but different
package versions ran the same clinical input through different prompts — which is
exactly what the two fields, read together, are designed to express.

## Model parameters: reasoning toggle

DeepSeek V4 Pro supports disabling reasoning via parameters. Implications:

- The toggle materially changes output distribution → it is provenance-critical config
  and belongs in the git-tracked agent parameters (under attestation), never an implicit
  deployment default.
- Reasoning **off** gives cleaner hashing (recorded output = full model output, no
  trace-exclusion rule), tighter format compliance for the strict concept-parser
  contract, lower latency and cost.
- Consider a **per-step parameter in the workflow package** rather than one global
  setting: the four analysis stages are where reasoning could improve clinical quality;
  the three prose steps are format-sensitive transformations where reasoning-off is
  likely strictly better. Per-step params then land in provenance via the package
  version, keeping the record complete without extra fields.

## What the record guarantees — and what it does not

- **Auditability everywhere**: the record fully identifies what produced the output.
- **Statistical reproducibility across harnesses**: same record ⇒ same configuration ⇒
  statistically similar outputs. Sampling is stochastic; identical records do not imply
  identical outputs.
- **Bit-exact reproduction** additionally requires: open weights (satisfied — DeepSeek),
  pinned seed, temperature 0 (or equivalent), and a matching inference stack. Hosted
  Foundry inference and self-hosted inference of the same weights can differ
  (quantization, engine, batching). The AI-Copyright-Reproducibility harness's
  content-hashing methodology is the verification tool: run N times per stack and
  compare hash distributions.

## Harness independence: the one structural prerequisite

The claim "the web app version is unnecessary — any harness can run the system from
published artifacts" holds **only after the inter-step contracts move out of the app**.
Today `ConsultGenerationConceptParser` (which output lines parse vs. drop) and the
concept formatter (how concept lists serialize into downstream prompts) are workflow
semantics living in C#. Two harnesses running the same DAG + prompts but parsing or
formatting differently produce different outputs. Until the workflow package format
specifies these contracts (per-step output schemas, or a published versioned "workflow
spec" the DAG version implies), the app version is silently part of the provenance.
This lands in milestone 4 of workflow-packages.md.

Once that is done, the published artifact set — open weights (DeepSeek), workflow
package, SNOMED edition, MCP server code (Apache 2.0) — is sufficient to re-run the
system with no dependency on this application or its Azure tenant.

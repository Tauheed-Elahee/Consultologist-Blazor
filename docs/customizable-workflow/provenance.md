# Per-Consult Provenance Record

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
| `workflow_package` | `name@version` of the pinned workflow package | Package registry |
| `input_hash` | Hash of the **effective** input | Computed at job start |
| `inference_stack` (optional) | Inference engine + version, for bit-exact re-run attempts | Deployment config |

## Attestation: git as source of truth for mutable deployments

The Foundry agent (system prompt, parameters, tool wiring) is edited in a portal and is
therefore mutable state. Rule: **track the agent config in git; at startup or job start,
fetch the deployed agent version and verify it matches the tracked manifest** — fail or
warn loudly on drift. This makes `agent_version` and the git content redundant by
construction, which is the point: the check keeps them honest. The same pattern applies
to the MCP deployment.

## The effective-input hash

The input is not just the consult draft. The request reaching the orchestrator is
draft + section list + standards, and account-level overrides change the standards.
Hash the **fully resolved content** (draft + resolved sections/standards after
overrides). Cleanest rule: overrides produce an ephemeral package-content hash recorded
alongside the pinned package version — otherwise two different runs can claim the same
input.

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

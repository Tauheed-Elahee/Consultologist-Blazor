# Workflow Packages: Versioned Content, Decoupled from Code

The goal is not primarily "users editing workflows" — it is **content as versioned
artifacts, decoupled from code deploys**. The pattern is already running in production
for the agent: the app pins `test-json` version 45 via the `AzureAI__AgentVersion` app
setting, and new agent behavior ships by publishing a new version and flipping one
setting, with no redeploy. Workflow content gets the same lifecycle.

## The package concept

A **workflow package** is one named, versioned artifact bundling everything content-ish:

- section standards (what `consult_section_standards.md` holds today)
- prompt templates for each stage/step (Scriban-style, with declared variables)
- per-step model parameters (reasoning on/off, temperature, seed — see provenance.md)
- eventually: the step/DAG definition itself, including per-step output contracts

## Versioning: CalVer `vYYYY.MM.N` (decided 2026-07-09)

Package versions use **CalVer**: `vYYYY.MM.N` — zero-padded month, within-month release
counter starting at 1 (not zero-padded). Rationale: SemVer earns its keep by signaling
interface compatibility, and content packages have no interface — runtime compatibility
is carried separately by the integer `specVersion` in the manifest. A date-bearing
version matches the model-weights analogy (weights and SNOMED editions are date-stamped),
makes staleness visible (a clinical package from 2024 deserves scrutiny), and answers
"which standards were in force last November?" from the identifier alone.

**Comparison is numeric, never lexicographic**: `v2026.07.10` sorts after `v2026.07.2`
numerically but before it as a string (and in blob listings). The registry client parses
and compares numerically (`CalVerVersion` in
`src/Consultologist.Api/Workflow/WorkflowPackageModels.cs`). The provenance record mixes
schemes (agent `45`, package `v2026.07.2`, SNOMED `20260401`) — expected; each artifact
keeps its native scheme.

## Registry, pinning, bundles

> **Milestone 1 implemented 2026-07-09**: blob registry (`workflow-packages` container,
> `{name}/{version}/manifest.json` + `standards.md`, mutable `{name}/latest.json`
> pointer), `WorkflowPackageStore` + authenticated `WorkflowPackages/Current` endpoint,
> account pin setting `consult.workflowPackage` with `WorkflowPackages__Default` app
> setting fallback (`general@latest`), frontend loads standards from the endpoint, and
> jobs record `WorkflowPackage`/`EffectiveInputHash`/`AgentVersion`
> (job `SchemaVersion` 2). Seed package source lives in `packages/general/`; publish
> with `scripts/publish-workflow-package.sh`.
> Registry auth is Entra-first: the store uses the app's managed identity against
> `WorkflowPackages__BlobServiceUri` (Storage Blob Data Reader on the account); the
> publish script uses `az --auth-mode login` (Storage Blob Data Contributor for the
> publisher). Connection strings remain only as the local-dev/Azurite fallback.

- **Registry**: packages live in storage — Azure Blob is the natural fit
  (`workflow-packages` container, `{name}/{version}/...`), published **immutably**: a new version never
  mutates an old one, exactly like model weights or agent versions.
- **Authoring**: keep package sources in their own git repo with CI publishing to blob
  on tag — code-style review and history, weights-style pinning at runtime. (Mirrors
  how `snomed-snowstorm-mcp` is a separate repo with its own deployment.) Full design
  for this split — including the agents repo and the CI-only trust model — in
  [content-repos.md](content-repos.md).
- **Pinning**: an account (or an individual consult run) references `name@version`. The
  current hardcoded behavior becomes the seed package (`general@v2026.07.1`) so nothing in the
  app is special-cased.
- **Specialty bundles fall out for free**: `breast-oncology@v2026.09.2`, `cardiology@v2026.05.1` are just
  different packages in the registry; the Consults page grows a package picker.
- **Account customization becomes thin overrides layered on a pinned package** (like
  today's `consult.sectionStandardsMarkdown` overriding the bundled file), not
  free-floating documents. Overrides change the effective-input hash (provenance.md).

## Two rules imposed by Durable Functions and by versioning

1. **Snapshot the package (name, version, resolved content hash) into the orchestration
   input at job start.** Durable replay requires the definition to be immutable per
   instance — never re-read settings or the registry mid-run.
2. **Published versions are immutable.** Corrections are new versions. This is what
   makes the provenance record meaningful.

## Important scoping observation

**Specialty bundles do not require the customizable DAG.** If specialties share the
pipeline shape (concept extraction → problem → trajectories → sectioned prose) and
differ only in prompts, section lists, and standards, then content-only packages deliver
the entire stated goal while the DAG stays compiled. DAG-as-data becomes necessary only
when a specialty needs a structurally different pipeline.

## Milestone sequencing

1. **Package + registry + pinning layer**, wrapping today's exact behavior as
   `general@v2026.07.1`. Defaults leave the codebase; versioning and provenance exist from day
   one.
2. **Prompts into packages** — the seven hardcoded prompts become package content
   (templates with declared variables).
3. **Section prose steps as package-defined ordered lists** (N steps, string→string).
4. **DAG in the package format** — including per-step output contracts (schemas), which
   is what finally severs harness dependence. Only build this when a real specialty
   demands a different shape; by then the registry/versioning/provenance rails exist.

UI consequences: the Templates page grows from "edit section standards" toward a package
view with per-account overrides; progress displays become generic step lists rather than
hardcoded stage names.

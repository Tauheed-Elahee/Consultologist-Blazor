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

## Registry, pinning, bundles

- **Registry**: packages live in storage — Azure Blob is the natural fit
  (`packages/<name>/<version>/...`), published **immutably**: a new version never
  mutates an old one, exactly like model weights or agent versions.
- **Authoring**: keep package sources in their own git repo with CI publishing to blob
  on tag — code-style review and history, weights-style pinning at runtime. (Mirrors
  how `snomed-snowstorm-mcp` is a separate repo with its own deployment.)
- **Pinning**: an account (or an individual consult run) references `name@version`. The
  current hardcoded behavior becomes the seed package (`general@1`) so nothing in the
  app is special-cased.
- **Specialty bundles fall out for free**: `breast-oncology@12`, `cardiology@3` are just
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
   `general@1`. Defaults leave the codebase; versioning and provenance exist from day
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

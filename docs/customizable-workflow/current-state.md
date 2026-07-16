# Current State: What Is Hardcoded vs. Data-Driven

> **Milestone 5 note (2026-07-15)**: everything below describing hardcoded section
> handling, the map container, `run-prose-step`, or the account standards override
> is historical — see package-format-v5.md and the Milestone 5 block in
> workflow-packages.md. The v5-only rebase (#77) then deleted pre-v5 engine
> support entirely: the engine accepts exactly specVersion 5.

Verified against the code on 2026-07-09 — **historical**: milestones 1–4 have since
shipped (registry/pinning/provenance, prompts as content, section steps as content,
DAG-as-data with per-node output contracts). The current boundary is recorded in
[decoupling-roadmap.md](decoupling-roadmap.md) and
[package-format-v4.md](package-format-v4.md); everything listed as "hardcoded" below
is now package data except the input model, the binding vocabulary, the concept
renderers, and the policy/trust layer.

## Already data-driven

- **The section list.** `ConsultGenerationRequest` carries `Sections[]` of
  `(Id, Name, Standard)` and the orchestrator fans out over whatever arrives; nothing
  server-side hardcodes which sections exist.
- **Section standards, with hardcoded defaults.** Defaults ship in
  `src/Consultologist.Web/wwwroot/templates/consult_section_standards.md`; per-account
  customizations are stored via the account-settings store under the key
  `consult.sectionStandardsMarkdown`. The Consults and Templates pages resolve
  setting-over-default. This override-on-default pattern is the blueprint for the rest.
- **Partially generalized already:** `ConsultGenerationSectionProseProgress` tracks
  `CompletedProseStepCount`/`TotalProseStepCount` rather than hardcoded step names, and
  job responses carry a `SchemaVersion` — the data model half-anticipates variable step
  lists and state migration.

## Hardcoded (compiled into the app)

All in `src/Consultologist.Api/`:

- **The analysis DAG.** Four fixed stages in `Jobs/ConsultGenerationJobs.cs`
  (`ConsultGenerationOrchestrator`): extract patient concepts → identify problem →
  typical trajectory → patient trajectory. The dependencies are the hardcoded activity
  argument lists (each stage consumes specific outputs of earlier stages).
- **The per-section prose pipeline.** Exactly three steps: standard draft → patient
  info applied → section instructions applied.
- **The prompts.** Seven prompt builders compiled into the activity classes and
  `Agents/AgentSectionGenerator.cs` (plus the SNOMED tool-usage guidance prepended in
  `ConsultGenerationPreprocessingRunner`).
- **The inter-step contracts.** `ConsultGenerationConceptParser` (strict bullet-format
  regex deciding which output lines parse vs. drop, feeding validation warnings) and
  `ConsultGenerationConceptFormatter`/`FormatConcepts` (how concept lists are serialized
  into downstream prompts). These are workflow *semantics* living in C# — the key
  obstacle to harness independence (see provenance.md).
- **Progress/state shape.** Entity state, SSE event kinds, stage names
  (`ConsultGenerationAnalysisStatuses`), and the History UI all assume the fixed
  pipeline shape.

## Difficulty ordering for making the hardcoded parts customizable

1. **Prompts as content** — same override-on-default pattern as standards; zero
   structural risk. (The formerly scaffolded `Extensions/Scriban/` dirs suggest Scriban
   templating with variables like `{{consult_draft}}`, `{{concepts}}` was already the
   intended direction.)
2. **Prose steps as an ordered list** — N string→string steps instead of exactly 3; no
   parsing contract between them, and progress tracking is already count-based.
3. **The analysis DAG as data** — hardest, because each edge carries a typed contract
   (`ClinicalConcept` lists via the parser). Requires per-step output schemas (JSON-mode
   against declared schemas; cf. `wwwroot/schemas/`) or giving up typed intermediates
   and the SNOMED validation machinery.

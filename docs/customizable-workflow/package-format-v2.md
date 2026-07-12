# Workflow Package Format — specVersion 2 (Prompt Templates)

Normative specification for `specVersion: 2` packages, authored for milestone 2
(issue #4, sub-issue #17). v2 is a strict superset of v1: it adds **prompt templates**
to the package; the standards content is unchanged. Decisions recorded here were made
2026-07-12: Scriban templating, file-per-prompt layout with manifest declarations,
strict variable errors, legacy direct-SSE endpoint excluded.

## Package layout

```
{name}/{version}/
├── manifest.json
├── standards.md                        # unchanged from v1
└── prompts/
    ├── _snomed-tool-guidance.md        # shared prelude (underscore prefix = fragment)
    ├── extract-patient-concepts.md
    ├── identify-problem.md
    ├── create-typical-trajectory.md
    ├── create-patient-trajectory.md
    ├── standard-section-draft.md
    ├── patient-section-draft.md
    └── section-instructions.md
```

## Manifest schema

```json
{
  "name": "general",
  "version": "v2026.08.1",
  "specVersion": 2,
  "templating": { "engine": "scriban", "engineVersion": "7.2.5" },
  "preludes": {
    "snomed-tool-guidance": "prompts/_snomed-tool-guidance.md"
  },
  "prompts": [
    { "id": "extract-patient-concepts",  "file": "prompts/extract-patient-concepts.md",
      "variables": ["consult_draft"], "prelude": "snomed-tool-guidance" },
    { "id": "identify-problem",          "file": "prompts/identify-problem.md",
      "variables": ["patient_concepts"], "prelude": "snomed-tool-guidance" },
    { "id": "create-typical-trajectory", "file": "prompts/create-typical-trajectory.md",
      "variables": ["problem_concepts"], "prelude": "snomed-tool-guidance" },
    { "id": "create-patient-trajectory", "file": "prompts/create-patient-trajectory.md",
      "variables": ["problem_concepts", "patient_concepts", "typical_trajectory_concepts"],
      "prelude": "snomed-tool-guidance" },
    { "id": "standard-section-draft",    "file": "prompts/standard-section-draft.md",
      "variables": ["section_name", "patient_trajectory_concepts"] },
    { "id": "patient-section-draft",     "file": "prompts/patient-section-draft.md",
      "variables": ["standard_section_draft", "consult_draft", "section_name"] },
    { "id": "section-instructions",      "file": "prompts/section-instructions.md",
      "variables": ["patient_section_draft", "section_name", "section_standard"] }
  ]
}
```

- All seven prompt ids are **required** in a v2 package; the set is closed in v2
  (unknown ids are a validation error). Later specVersions may open it (milestone 3+).
- `prelude` (optional, one per prompt): the named fragment's rendered content plus one
  blank line is prepended to the rendered prompt.
- `templating.engineVersion` records the Scriban version the package was authored and
  validated against (see Templating).

## Variable contract (normative)

The engine supplies **exactly** the declared variables for each prompt — no more, no
fewer. All values are strings, pre-rendered by the engine:

| Variable | Content |
|---|---|
| `consult_draft` | The physician's draft consult note, verbatim |
| `section_name` | Display name of the section being generated |
| `section_standard` | Resolved standard for the section (package default + account override), may be empty |
| `standard_section_draft` | Output of the standard-section-draft step |
| `patient_section_draft` | Output of the patient-section-draft step |
| `patient_concepts`, `problem_concepts`, `typical_trajectory_concepts` | Concept lists in the **analysis format** of the concept rendering contract below |
| `patient_trajectory_concepts` | Concept list in the **trajectory-context format** of the concept rendering contract below |

### Concept rendering contract

Concept-list variables are pre-rendered by the engine (formatting stays engine-side in
v2; templates receive finished text). There are **two distinct formats**, matching the
two formatters the compiled prompts have always used — a verbatim port must preserve
both. Any harness reimplementing this format must reproduce these renderings
byte-for-byte: they feed the model and therefore affect output.

**Analysis format** — used for `patient_concepts`, `problem_concepts`, and
`typical_trajectory_concepts` (the analysis-stage prompts; engine formatter:
`ConsultGenerationConceptFormatter`). One bullet per line, newline-separated:

```
- {term} ({type}) - {id}                                  # active SNOMED concept
- {term} ({type}) - {id} [not active SNOMED concept]      # inactive SNOMED concept
- {term} [not SNOMED concept]                             # unmapped finding
```

- A bullet may carry a trailing ` -- support: {phrase}` suffix.
- An empty list renders as the literal string `(none)`.

**Trajectory-context format** — used for `patient_trajectory_concepts` (the
`standard-section-draft` prose prompt; engine formatter:
`AgentSectionGenerator.FormatConcepts`). One bullet per line, newline-separated:

```
- {term} ({type}) - {id}; active: {True|False}; source: {source}
```

- `active` renders the boolean with .NET capitalization (`True`/`False`).
- A bullet with a support phrase carries a trailing ` support: {phrase}` suffix
  (single space, no `--` separator — this differs from the analysis format).
- An empty list renders as the literal string `(none)`.

## Templating: Scriban

- Engine: **Scriban**, pinned at the `engineVersion` in the manifest (initially
  `7.2.5`); the app records its own Scriban version and **rejects packages whose
  `engineVersion` is newer than the engine's** (same posture as `specVersion`).
- **Reproducibility consequence (accepted trade-off)**: template semantics are defined
  by Scriban at the pinned version. A harness reimplementing this workflow needs a
  Scriban-compatible renderer; the pinned version is part of the workflow-format
  contract and belongs in any cross-harness provenance comparison.
- **Authoring convention**: prefer plain `{{ variable }}` interpolation. Logic
  (conditionals, loops, functions) is permitted, but every construct used widens the
  package's reproducibility surface; publish-time validation does not restrict
  constructs in v2.

### Strictness (fail-loud)

Publish-time validation (blocking):
- Template uses a variable not declared for its prompt → **error**.
- Manifest declares a variable the contract doesn't define → **error**.
- Missing prompt id, missing file, unresolvable prelude → **error**.
- Declared variable never used by the template → **warning** (non-blocking).
- Template must parse under the pinned Scriban version → **error** otherwise.

Runtime (engine):
- Templates render in strict mode: any access to an unsupplied variable throws; the
  step fails loudly and the Durable retry policy applies. No silent empty substitution.

## Compatibility and rollout

- The engine accepts `specVersion` ≤ 2. For **v1 packages, prompts fall back to the
  compiled defaults** (today's behavior, unchanged) — v1 packages remain fully valid.
- Rollout order: deploy the v2-capable app **before** publishing the first v2 package.
  The `specVersion` gate makes the wrong order loud (503 with a clear log), not silent.
- **Input-hash semantics**: prompts are package content. They are covered by the
  `workflowPackage` provenance field (package version), **not** by the effective-input
  hash, which continues to cover draft + resolved sections/standards only.

## Out of scope in v2

- The legacy direct-SSE endpoint (`Agents/ConsultGeneration.cs`) keeps its two compiled
  prompts (`BuildUserMessage`, `BuildTrajectorySectionMessage`); it is a deprecation
  candidate, not a package consumer.
- No per-account prompt overrides (standards overrides are unaffected).
- No custom prompt ids or step definitions (milestone 3), no output schemas (milestone 4).

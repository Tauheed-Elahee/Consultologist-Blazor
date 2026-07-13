# Workflow Package Format — specVersion 3 (Section Steps)

Normative specification for `specVersion: 3` packages, authored for milestone 3
(issue #5). v3 is a strict superset of v2: it adds the **section step sequence** to the
package — the per-section prose pipeline becomes N ordered string→string steps declared
in the manifest instead of three steps compiled into the engine. Prompt templates,
preludes, standards, and the templating rules are unchanged from
[package-format-v2.md](package-format-v2.md). Decisions recorded here were made
2026-07-13: bindings as a name→source map, step ids defaulting to prompt ids, the four
analysis prompts staying closed (they generalize in milestone 4), compiled fallbacks
retired.

## What changes and what doesn't

**New in v3**: a required, ordered `sectionSteps` array in the manifest. Each section of
a consult runs these steps in order; each step renders one prompt template and sends it
to the agent; the output of each step can feed the next; the final step's output is the
section text.

**Unchanged from v2**: package layout, `standards.md`, prompt template files, preludes,
the Scriban templating rules and strictness posture, the concept rendering contract,
CalVer versioning, immutability, and the four analysis prompts
(`extract-patient-concepts`, `identify-problem`, `create-typical-trajectory`,
`create-patient-trajectory`) — the analysis DAG's shape stays compiled until
milestone 4.

## Manifest schema

Everything from v2, plus `sectionSteps`:

```json
{
  "name": "general",
  "version": "v2026.08.1",
  "specVersion": 3,
  "templating": { "engine": "scriban", "engineVersion": "7.2.5" },
  "preludes": { "snomed-tool-guidance": "prompts/_snomed-tool-guidance.md" },
  "prompts": [
    ...the four analysis prompts, unchanged from v2...,
    { "id": "standard-section-draft",    "file": "prompts/standard-section-draft.md",
      "variables": ["section_name", "patient_trajectory_concepts"] },
    { "id": "patient-section-draft",     "file": "prompts/patient-section-draft.md",
      "variables": ["standard_section_draft", "consult_draft", "section_name"] },
    { "id": "section-instructions",      "file": "prompts/section-instructions.md",
      "variables": ["patient_section_draft", "section_name", "section_standard"] }
  ],
  "sectionSteps": [
    { "prompt": "standard-section-draft", "label": "Drafting section",
      "bindings": { "section_name": "section_name",
                    "patient_trajectory_concepts": "patient_trajectory_concepts" } },
    { "prompt": "patient-section-draft", "label": "Applying patient information",
      "bindings": { "standard_section_draft": "previous_step_output",
                    "consult_draft": "consult_draft",
                    "section_name": "section_name" } },
    { "prompt": "section-instructions", "label": "Applying section instructions",
      "bindings": { "patient_section_draft": "previous_step_output",
                    "section_name": "section_name",
                    "section_standard": "section_standard" } }
  ]
}
```

### Step object

| Field | Required | Meaning |
|---|---|---|
| `prompt` | yes | Id of a `prompts[]` entry rendered by this step |
| `label` | yes | Human-readable display label, shown as generation progress in the UI |
| `bindings` | yes | Map of template variable name → engine binding source (see below) |
| `id` | no | Step identity; defaults to the prompt id. Required only when the same prompt is used by more than one step (step ids must be unique) |

Step ids appear in progress events, job state, and error messages; they are part of the
package's observable surface, so treat renames as meaningful changes.

## The binding contract (normative)

v2 fixed each prompt's inputs in a per-prompt variable table compiled into the engine.
v3 generalizes that table into **declarative bindings**: each step maps every variable
its template declares to one of a closed vocabulary of **engine binding sources**. The
vocabulary is engine code and versioned spec — the engine knows how to *supply* values;
every *use* is package-defined.

| Binding source | Value supplied |
|---|---|
| `consult_draft` | The physician's draft consult note, verbatim |
| `section_name` | Display name of the section being generated |
| `section_standard` | Resolved standard for the section (package default + account override), may be empty |
| `patient_trajectory_concepts` | The reconciled patient-trajectory concept list, rendered in the **trajectory-context format** of the v2 concept rendering contract |
| `previous_step_output` | The trimmed output string of the immediately preceding step |

Rules:

- Binding keys must **set-equal** the referenced prompt's declared `variables` — every
  declared variable bound, no undeclared bindings. (This is the same exact-set rule the
  renderer enforces at runtime, surfaced at publish time.)
- Binding values must be members of the vocabulary above.
- The **first step must not bind `previous_step_output`** (there is none).
- Steps are string→string: one rendered prompt in, one prose string out. Only
  `previous_step_output` carries data between steps; there is no access to outputs of
  steps before the previous one, and no fan-out within the section pipeline
  (milestone 4's DAG nodes subsume both).

Because template variable names are free-form in v3 and bound explicitly, every
existing v2 template works verbatim: `standard_section_draft` is just a variable name
that happens to bind to `previous_step_output` in the canonical second step.

## Prompt set rules

- The four **analysis prompt ids remain required and closed**, with their variables
  restricted to the v2 contract's known set. They are rendered by the compiled analysis
  stages, which do not read `sectionSteps`.
- **Prose prompt ids and their variable names are free-form** — any id, any variables,
  as long as the bindings cover them.
- Every `prompts[]` entry must be either an analysis prompt or referenced by at least
  one section step. **Orphan prompts are a validation error** — a package cannot carry
  prompt content that nothing renders.

## Validation (fail-loud, publish-time and load-time)

Kept from v2 (blocking unless noted): templating engine must be `scriban` with
`engineVersion` ≤ the engine's own; templates must parse and strict-render against
their declared variables; undeclared variable used in a template → error; declared
variable never used → warning; missing files, unresolvable preludes, duplicate prompt
ids → error.

New in v3 (all blocking):

- `sectionSteps` missing or empty → error.
- Step `prompt` does not resolve to a `prompts[]` entry → error.
- Step `label` missing or blank → error.
- Binding keys ≠ referenced prompt's declared variables (either direction) → error.
- Binding value outside the binding vocabulary → error.
- First step binds `previous_step_output` → error.
- Duplicate step ids (including the same prompt used twice without explicit `id`s)
  → error.
- Orphan prompt entry (not analysis, not referenced by any step) → error.
- Analysis prompt missing, or declaring a variable outside the v2 known set → error.

Runtime posture is unchanged: strict-mode rendering, unsupplied variable access throws,
the step fails loudly, the Durable retry policy applies.

## Compatibility and rollout

- The engine accepts `specVersion` ≤ 3.
- **v2 packages remain fully valid.** The engine synthesizes the canonical three-step
  list for them — exactly the `sectionSteps` block shown in the manifest schema above,
  including labels; that block is **normative** as the v2 synthesis. Existing pins
  (e.g. `general@v2026.07.3`) keep working unchanged, byte-for-byte.
- **v1 packages are rejected at job start.** Milestone 3 retires the compiled fallback
  prompts: a consult generation job requires a resolvable specVersion ≥ 2 package. The
  job-start endpoint resolves the package pin server-side (account setting →
  `WorkflowPackages__Default` → `general@latest`) to a **concrete immutable version**
  and snapshots it into the job; if the registry is unreachable the start fails (503)
  rather than silently running different prompts. v1 packages still load for standards
  display.
- Rollout order: deploy the v3-capable app **before** publishing the first v3 package.
  The `specVersion` gate makes the wrong order loud, not silent.
- **Deploy discipline**: the orchestrator's section pipeline changes shape in
  milestone 3 (three named activities → one generic step activity), so the deploy needs
  a drain window — no running jobs at deploy time. The old activity names remain as
  shims for one release to absorb queued messages.
- **Input-hash semantics are unchanged**: steps, like prompts, are package content —
  covered by the `workflowPackage` provenance field, not the effective-input hash.
  Because the job start now resolves `@latest` refs to concrete versions before
  hashing and recording, the provenance ref is always immutable.

## Out of scope in v3

- **Per-step model parameters** (temperature, reasoning effort, agent selection) — all
  steps call the same pinned agent identically; parameters are a milestone 4 concern
  alongside per-node output contracts.
- **Non-linear step shapes** — no branching, no fan-out, no access to outputs earlier
  than the previous step. The section pipeline is an ordered list; the DAG arrives in
  milestone 4.
- **Custom analysis stages** — the four analysis prompts stay closed and the analysis
  DAG stays compiled (milestone 4).
- **Per-account step or prompt overrides** (standards overrides are unaffected).

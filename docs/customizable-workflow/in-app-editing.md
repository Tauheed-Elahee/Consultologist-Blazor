# In-App Package Editing (Design — Not Yet Implemented)

Recorded 2026-07-15 during post-milestone-4 exploration. Status: **design capture;
exploration continues** — tracked by the "in-app workflow-package editor" issue.
Sibling design: [output-contract-catalog.md](output-contract-catalog.md) (#55);
the two are independent (no shared files; either can land first).

> **Superseded in part 2026-07-15** by
> [package-format-v5-design.md](package-format-v5-design.md): under the v5 fork
> model the account standards override retires (all customization forks the
> package), so this document's two-layer standards story and the "My standards" tab
> become per-section editing of the fork's data collection; the publish contract
> carries `data/` and `derivedFrom`. The publish pipeline, naming, access rule, and
> trust posture here remain authoritative.

## The decision that shapes everything

In-app editing = **authoring a new immutable package version**, published to the
existing registry and activated by flipping the account pin — never account-level
overlays of package content. A job's `workflowPackage` ref keeps naming every prompt
and step that ran, and the per-node hash chain shows an edit's exact blast radius
(edit one prompt → only that node's InputHash and its downstream change). Overlays
would fragment provenance and demand a parallel override mechanism.

The existing account standards override (`consult.sectionStandardsMarkdown`) stays
untouched: it is per-section *input data* (covered by the effective-input hash), a
different species from package content. Post-editor there are deliberately two
standards layers — the package's `standards.md` (ships with the version, editable in
the package editor) and the account override (fast per-section personalization that
wins at consult time).

## Security finding — must ship with the API slice

The pin setting (`consult.workflowPackage`) is writable via the generic settings PUT,
and job start accepts a client-supplied package ref. With only repo-owned packages
that is harmless; the moment per-account packages exist, either path lets account A
read or run account B's prompts. **Access rule**: `acct-*` package names are usable
only by their owning account — enforced in `WorkflowPackagePinResolver` (foreign pin
→ warn + fall through to default, matching malformed-pin behavior) and at job start
before resolution. Repo-owned names (`general`) remain open to all.

## Design

- **Naming**: account package = `acct-` + first 12 hex of `AppUserId` (already a
  32-hex GUID string; fits the `^[a-z0-9][a-z0-9-]*$` name rule with no hashing).
  Helpers `WorkflowPackageNaming.ForAccount/IsAccountPackage/CanAccess`.
- **Server assigns name AND version** on publish; the client's manifest name/version
  are ignored. Publishing to `general` or a foreign package is impossible by
  construction, not by validation. Version = next CalVer for that name
  (`AssignNext(latest, nowUtc)`, pure, `TimeProvider`-injected).
- **Pin flips server-side** after a successful publish, to the **concrete** version
  (`acct-…@vN`) — the store's immutable forever-cache serves it instantly, and the
  60s latest-pointer cache never delays activation; `@latest` semantics remain for
  `general` only. "Revert to default" deletes the pin setting (resolver falls back
  to `general@latest`); account versions are never deleted.
- **Manifest as commit marker**: standards/prompts/schemas upload first; manifest
  last with a conditional create (`If-None-Match: *`) as the atomic immutability
  guard (the store resolves manifest-first, so partial uploads are invisible);
  409 → re-read latest, bump, bounded retry.
- **v1 editing scope**: prompt texts (variables read-only on existing prompts;
  simple list on new prompts), map steps (reorder/relabel/add/remove; prompt picker;
  binding pickers constrained to the closed vocabulary and the v4.0 closures),
  package `standards.md`. Analysis nodes: read-only summary (the DAG visualization
  generator is a separate concern). Schemas: carried through verbatim (the
  canonical-schema closure makes them fixed). specVersion locked to 4. The editor
  edits *within* the format; it never extends it.
- **RBAC**: one-time grant of Storage Blob Data **Contributor** to the Function
  App's managed identity (currently Reader).

## API surface

- `GET WorkflowPackages/Current/Content` — new endpoint (keeping the hot `Current`
  lean): the pin-resolved package's full editable content as
  `(Name, Version, SpecVersion, Manifest (typed; the binding-value converter
  round-trips), StandardsMarkdown, Files (path→content incl. preludes/schemas))`.
  Prerequisite store change: `WorkflowPackage` retains the downloaded file dict as
  `SourceFiles` (today `LoadPromptsAsync` discards it after validation).
- `POST WorkflowPackages/Publish` — request mirrors the content response (manifest +
  standards + files), making load→edit→publish one round-tripping contract.
  Pipeline: authorize → structural checks (specVersion 4; standards non-blank;
  file-path hygiene `^(prompts|schemas)/[A-Za-z0-9._-]+$`; manifest↔files closure in
  both directions; total-size guard) → `WorkflowPackageValidator.Validate` (pure
  over in-memory strings — reused verbatim; errors → 400 list for inline UI) →
  upload via a thin `IWorkflowPackageRegistryWriter` (unit-testable with an
  in-memory fake; shares the store's container factory) → latest pointer → pin
  write → `{name, version, ref, warnings}`.

## UI surface

`Templates.razor` becomes two tabs:

1. **My standards** — the existing account-override editor, extracted verbatim into
   a component; behavior unchanged.
2. **Workflow package (advanced)** — the editor: banner ("Editing
   `general@v2026.07.5` → will publish as `acct-…@next`" + Publish/Discard/Revert +
   validation-error list), per-prompt cards (textarea + live markdown preview,
   variables as read-only chips, prelude collapsed), the map-step list editor, the
   read-only node summary, and the package-standards section editor (reusing the
   extracted `ParseStandards`/`SerializeStandards`/preview helpers). Draft state is
   client-side in-memory; the publish endpoint's error list is the authoritative
   validation (client-side Scriban rendering is a deferred extension).

## Slice plan (when pulled)

1. **PR 1 — API**: store `SourceFiles` + content endpoint; naming + access rule;
   registry writer + shared container factory; publish endpoint + version
   assignment; tests (AssignNext cases, name/version overwrite, validator wiring,
   structural guards incl. path traversal, pin write, CanAccess matrix, foreign-pin
   fallback, content→publish round-trip); CONFIGURATION.md/registry-operations.md
   notes. Mergeable alone; no drain window.
2. **PR 2 — Web**: client service + config URLs; shared standards-markdown helper
   extraction; tabbed page + `Shared/WorkflowEditor/` components; polish.
   Verification is the provenance showcase: edit `identify-problem` → publish → run
   a consult → only that node's InputHash (and downstream) changes.
3. **PR 3 — Docs**: this file graduates from design to record; updates to
   workflow-packages.md, provenance.md, registry-operations.md, product-stages.md
   (this editor is the seed of stage 2's learning loop).

## Trust posture

An account can affect only its own package and its own pin. `general` is unwritable
by construction. All content passes the same validator as repo publishes; file paths
are hygiene-checked; agents, tools, and schemas are untouchable from the editor —
edited prompts steer the same attested agents through the same closed output
contracts.

## Decisions (settled 2026-07-15; were open questions)

- **v1 edits prompt texts only** — node bindings and graph shape stay read-only
  until the editor earns graph-editing UX. Smaller blast radius for the first
  fork-publishing release.
- **Draft persistence: localStorage** — drafts survive refresh/navigation on the
  same browser, cleared on publish/discard. No server-side draft store (that would
  invent a mutable content store beside the immutable registry) and no multi-device
  claims in v1.
- **Publish activates, always** — no "publish without activating" checkbox in v1.
  One mental model: publish = "start using this"; "Revert to default" is the escape
  hatch. An inactive version is only useful with test-before-switch or eval-gating
  machinery (#16), and the endpoint can grow a `SetPin=false` flag then with zero
  format or provenance change.
- **Account version history: never delete in v1** (unchanged from the design above —
  immutability is what keeps provenance refs permanent); cleanup UX is revisited
  post-release only if storage ever matters.

Remaining note, not a question — relationship to #16 (GitOps content repos): the
editor is the in-app authoring path; #16 is the CI authoring path — both publish to
the same registry, and the eval-gated publishing idea applies to both eventually.

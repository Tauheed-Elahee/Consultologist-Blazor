# In-App Package Editing (Design — Not Yet Implemented)

Recorded 2026-07-15 during post-milestone-4 exploration; **reshaped to
specVersion 5 on 2026-07-16** after the v5 engine (#59), the account-override
retirement (#71), and the v5-only rebase (#77) — the engine now accepts exactly
specVersion 5, so the editor is v5-native by construction. Status: **design
capture** — tracked by #57, Milestone 5's last slice. Sibling design:
[output-contract-catalog.md](output-contract-catalog.md) (#55, since
implemented). The pre-v5 draft of this document (two-layer standards,
`standards.md`, map-step editing, specVersion 4) is in git history; nothing in
it beyond what is restated here remains authoritative.

## The decision that shapes everything

In-app editing = **authoring a new immutable package version**, published to the
existing registry and activated by flipping the account pin — never account-level
overlays of package content. A job's `workflowPackage` ref keeps naming every
prompt, node, and data item that ran, and the per-(node, item) hash chain shows an
edit's exact blast radius (edit one prompt → only that node's InputHashes and
their downstream change). Overlays would fragment provenance and demand a
parallel override mechanism.

v5 sharpened this into a single sentence: **all customization forks the
package.** The one pre-existing overlay — the account standards override
(`consult.sectionStandardsMarkdown`) — retired with the v5 input model (#71,
PR #76). Standards are ordinary package data (the `data/standards/` collection,
one file per section), so "edit my standards" and "edit my prompts" are the same
operation with the same provenance story. The fork's manifest records
`derivedFrom` — the concrete ref it was edited from — and the server stamps it
at publish (lineage is asserted by the registry writer, never by the client).

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
- **Server assigns name, version, AND lineage** on publish; the client's manifest
  name/version/`derivedFrom` are ignored. Publishing to `general` or a foreign
  package is impossible by construction, not by validation. Version = next CalVer
  for that name (`AssignNext(latest, nowUtc)`, pure, `TimeProvider`-injected).
  `derivedFrom` = the concrete ref the content endpoint resolved (first publish
  forks the pinned package, e.g. `general@v2026.07.6`; a re-publish forks the
  account's own prior version), so walking the chain always lands on the
  Consultologist root — the derived-root rule from package-format-v5.md.
- **Pin flips server-side** after a successful publish, to the **concrete** version
  (`acct-…@vN`) — the store's immutable forever-cache serves it instantly, and the
  60s latest-pointer cache never delays activation; `@latest` semantics remain for
  `general` only. "Revert to default" deletes the pin setting (resolver falls back
  to `general@latest`); account versions are never deleted.
- **Manifest as commit marker**: prompt, schema, and data files upload first;
  manifest last with a conditional create (`If-None-Match: *`) as the atomic
  immutability guard (the store resolves manifest-first, so partial uploads are
  invisible); 409 → re-read latest, bump, bounded retry. `dag.mmd` is not part of
  v1 account publishes — it is a derived diagram, and the API's
  `WorkflowDagDiagram` can render one server-side if a later slice wants it.
- **v1 editing scope — texts only** (settled 2026-07-15, restated in v5 terms):
  prompt texts (variables read-only on existing prompts; simple list on new
  prompts) and **data-item contents** (each `data/standards/` item is its own
  file — edited whole, no parsing layer). Everything structural stays read-only:
  graph shape, bindings, `forEach`, `result`, schemas (canonical closure makes
  them fixed), and **collection membership** — adding/removing/renaming items
  would rewrite `index.json` and change the deliverable's section list, which is
  graph-shaped blast radius, not text. specVersion locked to 5. The editor edits
  *within* the format; it never extends it.
- **RBAC**: one-time grant of Storage Blob Data **Contributor** to the Function
  App's managed identity (currently Reader).

## API surface

- `GET WorkflowPackages/Current/Content` — new endpoint (keeping the hot `Current`
  lean): the pin-resolved package's full editable content as
  `(Name, Version, SpecVersion, Manifest (typed; the binding-value converter
  round-trips), Files (path→content: prompts incl. preludes, schemas, and data
  files incl. each collection's index.json))`. Prerequisite store change:
  `WorkflowPackage` retains the downloaded file dict as `SourceFiles` (today
  `LoadPromptsAsync` discards it after validation).
- `POST WorkflowPackages/Publish` — request mirrors the content response
  (manifest + files), making load→edit→publish one round-tripping contract.
  Pipeline: authorize → structural checks (specVersion 5; file-path hygiene
  `^(prompts|schemas)/[A-Za-z0-9._-]+$` or `^data/[a-z0-9-]+/[A-Za-z0-9._-]+$`;
  manifest↔files closure in both directions, including each declared collection's
  `index.json` items; total-size guard) → server stamps name/version/`derivedFrom`
  → `WorkflowPackageValidator.Validate` (pure over in-memory strings — the same
  relational v5 rules as repo publishes, reused verbatim; errors → 400 list for
  inline UI) → upload via a thin `IWorkflowPackageRegistryWriter` (unit-testable
  with an in-memory fake; shares the store's container factory) → latest pointer →
  pin write → `{name, version, ref, warnings}`.

## UI surface

`Templates.razor` is already read-only (the override editor retired with v5; the
page shows the pinned package's sections with a notice pointing at this work). It
becomes the editor — one surface, no tabs, since the "My standards" layer no
longer exists:

- Banner: "Editing `general@v2026.07.6` → will publish as `acct-…@next`" +
  Publish/Discard/Revert-to-default + the validation-error list.
- Per-prompt cards: textarea + live markdown preview, variables as read-only
  chips, prelude collapsed.
- Per-item cards for the `data/standards/` collection: textarea + preview per
  item; item `id`/`name` read-only in v1. The old
  `ParseStandards`/`SerializeStandards` helpers died with `standards.md` (#77) —
  per-item files need no split/join layer, which deletes the "my heading broke
  the parser" failure mode outright.
- Read-only graph summary: nodes with their prompts, bindings, `forEach`, and
  `result` (rendering `dag.mmd` in-app is a separate concern).
- Draft state: **localStorage** (settled) — drafts survive refresh/navigation on
  the same browser, cleared on publish/discard; the publish endpoint's error list
  is the authoritative validation (client-side Scriban rendering is a deferred
  extension).

## Slice plan (when pulled)

1. **PR 1 — API**: store `SourceFiles` + content endpoint; naming + access rule;
   registry writer + shared container factory; publish endpoint + version/lineage
   assignment; tests (AssignNext cases, name/version/`derivedFrom` overwrite,
   validator wiring, structural guards incl. path traversal and data-path
   hygiene, index.json closure, pin write, CanAccess matrix, foreign-pin
   fallback, content→publish round-trip); CONFIGURATION.md/registry-operations.md
   notes. Mergeable alone; no drain window.
2. **PR 2 — Web**: client service + config URLs; the editor page +
   `Shared/WorkflowEditor/` components; polish. Verification is the provenance
   showcase, now two-sided: edit `identify-problem` → publish → run a consult →
   only that node's InputHash (and downstream) changes; edit one standards item →
   only that item's per-(node, item) chain moves while the other items' hashes
   hold byte-identical.
3. **PR 3 — Docs**: this file graduates from design to record; updates to
   workflow-packages.md, provenance.md, registry-operations.md, product-stages.md
   (this editor is the seed of stage 2's learning loop).

## Trust posture

An account can affect only its own package and its own pin. `general` is unwritable
by construction. All content passes the same validator as repo publishes; file paths
are hygiene-checked; agents, tools, and schemas are untouchable from the editor —
edited prompts steer the same attested agents through the same closed output
contracts. Lineage is server-stamped, so `derivedFrom` chains are trustworthy by
construction.

## Decisions (settled 2026-07-15; were open questions)

- **v1 edits texts only** — prompt texts and data-item contents; node bindings,
  graph shape, and collection membership stay read-only until the editor earns
  structural-editing UX. Smaller blast radius for the first fork-publishing
  release.
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

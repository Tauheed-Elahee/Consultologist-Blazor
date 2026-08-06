# Package format v8: candidates and the trigger — design sketch

**Status: no v8 is cut.** Nothing in this document is scheduled, designed, or
promised. The engine accepts exactly **{5, 6, 7}**
(`WorkflowPackageStore.SupportedSpecVersions`), and every candidate below is
recorded because it *would* need a version bump, not because one is planned.

This is a holding record, not a design. When a v8 is actually cut, this file
gets replaced by a real design doc in the shape of
[package-format-v7-design.md](package-format-v7-design.md) — motivation, the
closure set, versioning mechanics, editor implications, rollout — written when
the work is real, the way v5, v6 and v7 each were.

## 1. Why this exists

The candidate territory was scattered across four places and labelled in none
of them: [package-format-v7-design.md](package-format-v7-design.md) § 11
(*Future steps beyond v7*), that document's § 7 *"still out of scope"* list, a
separate "input model as content" section in
[decoupling-roadmap.md](decoupling-roadmap.md), and the analysis on #227 —
which is closed, so it was reachable only through one § 11 bullet.

A version bump is expensive (§ 5). Holding the candidates in one place is what
lets the next one be judged against the others rather than in isolation.

## 2. The trigger

**A demanding consumer, not an accumulation of nice-to-haves.**

The bar is a workflow somebody actually wants to build that the format cannot
express — most plausibly typed inputs (§ 3.1) or conditional deliverables
(§ 3.2). When that arrives, the other candidates are re-read and whichever
have matured ride along, exactly as v6 paired three moves and v7 paired two
because they touched the same manifest, validator and hash definitions.
Designing them apart risks two conflicting revisions; v7's design doc says so
in as many words.

**What is not a trigger:** a rule that could be looser. § 3.5 is the live
example — one engine exists today, so the practical risk of the current rule
is nil, and as the v7 design doc puts it, *"the discipline is the point."*

## 3. Candidates

### 3.1 Typed inputs

*Recorded: v7 design § 11.*

Inputs are all text today. Typing them — dates, structured patient fields —
changes prompt templating (a date is rendered, not interpolated) and the
intake form (a date picker is not a textarea).

**Needs a bump**: yes, it changes the manifest's `inputs` schema, which the
validator closes over.

**Waits on**: a workflow whose prompt genuinely cannot do its job with a
string. Overlaps § 3.6 — typing is most of what "input model as content" has
left after v7.

### 3.2 Conditional deliverables

*Recorded: v7 design § 7, "still out of scope".*

A deliverable produced only when some condition holds — a billing summary only
for billable encounters, say. Unstarted; there is no sketch beyond the name.

**Needs a bump**: yes. `results` becomes conditional, and every place assuming
a deliverable set is fixed at pin time moves with it — blocks, `TotalBlockCount`,
the job-outcome rule that Completed requires *every* declared deliverable.

**Waits on**: a product decision about what a job promises. This is the
candidate most likely to redefine "Completed", which is worth naming before it
is designed.

### 3.3 Cross-package composition

*Recorded: v7 design § 7, "still out of scope".*

One package referencing another's nodes or prompts. Unstarted, and the largest
of these by some distance — it introduces a dependency graph *between*
packages on top of the node graph inside one, with its own versioning,
resolution and immutability story.

**Needs a bump**: yes, and probably more than one.

**Waits on**: evidence that authors are copying between packages enough to
justify it. The fork model (`derivedFrom`) covers the common case today.

### 3.4 Per-deliverable delivery routing

*Recorded: v7 design § 7 and § 11 — the only candidate in both.*

The patient letter goes somewhere the consult note does not. § 11's own
verdict: *"a product decision wearing a delivery change's clothes."*

**Needs a bump**: yes, if routing is declared per result in the manifest. Not
if routing turns out to belong to the account rather than the package — which
is the product question to settle first, and the reason this has sat in two
lists without moving.

**Waits on**: that question.

### 3.5 Relaxing forEach reachability to the package (#227)

*Recorded: v7 design § 11, added 2026-08-05 when #227 closed.*

The validator requires **each** result to transitively include at least one
forEach source. The justification is about the **package** — *a package with no
fan has no consult* ([package-format-v6-design.md](package-format-v6-design.md)
§ 7) — and the enforcement is per deliverable.

The lineage explains the gap. v5 required `result` to be a forEach node because
the deliverable **was** the fan; v6 restated that as an aggregator including a
forEach source; #214 generalized it per-result by symmetry with the other v6
closures, not because multiple deliverables made it more necessary. A package
whose note fans over section standards and whose letter is a single summarizing
prompt satisfies the justification completely and still fails the rule.

**Both sides, because they matter equally.** It is **weak as a guarantee** — an
author satisfies it with an aggregator over the fan bound to a barely-used
variable, since the unused-variable check is only a warning. But it worked as a
**nudge**: in `example-two-documents` it pushed the patient letter to read from
an aggregator over the assembled sections rather than generate independently
from trajectory concepts, so the letter can only summarize what the note
actually says. That is a real gain and the reason not to relax it casually.

**Needs a bump**: yes — or an erratum (§ 6). The relaxation is *at least one*
result reaches a fan.

**Waits on**: a package that legitimately needs it. None exists.

### 3.6 The input model as content

*Recorded: [decoupling-roadmap.md](decoupling-roadmap.md), "A conceivable
future milestone", deliberately unplanned.*

A manifest-declared **input schema** (JSON Schema) and a Consults page that
renders whatever form the pinned package declares.

**v7 already annexed part of this.** Declared input slots exist, so what
remains is typing (§ 3.1) plus the schema-driven frontend. The roadmap is
candid that most of the cost lives in the frontend and that the idea "still
waits for a demanding consumer" — the same trigger as § 2, recorded
independently before v7 shipped.

**Needs a bump**: yes, and it would likely subsume § 3.1 rather than sit beside
it.

## 4. Not v8

Two things look like format changes and are not. Stating them keeps the list
from growing by association.

- **Attachment-shaped inputs.** v7 design § 11's own note: extraction "feeds
  text into slots and stays outside the format". #210's named-input binding for
  email attachments builds directly on v7 § 3 and needs no new version.
- **Non-text inputs.** v7 design § 7: files bind **through extraction** (#208,
  #210), never as binary inputs. That is a settled boundary, not a deferral.

## 5. What a bump costs

This is the section that makes "wait for a consumer" the right default.

- The engine's accepted set is stated in two places —
  `WorkflowPackageStore.cs` (`SupportedSpecVersions`) and
  `WorkflowPackageValidator.cs` (the spec gate and its error message).
- There are **17 version-dispatch points** across `Workflow/` and `Jobs/`
  (`SpecVersion ==`, `>= 6`, `>= 7`, `v6OrLater`). Each needs an individual
  disposition, not a global find-and-replace.
- v7's § 8 records the sharp edge that made this real: two `== 6` comparisons
  would otherwise have routed a v7 package through **v5** rules silently. A
  v8 audit has the same shape and the same failure mode.
- A **proving migration** — v7 § 10 migrated `general@vNext` to a minimal v7
  whose output was byte-identical, so the format step was provable
  independently of any behaviour change.
- The cheap part: the content repo's CI validator is structural only (parse,
  CalVer, immutability, file closure), so v7 needed **no CI change** there.
  This repo's validator is the sole well-formedness gate.

## 6. The v7-erratum alternative

Manifests declare the rule set they validated under. A package authored under
relaxed rules would fail an older engine — so a relaxation is either a version
bump or an **explicitly documented erratum**, never a quiet loosening.

The erratum route exists for § 3.5 specifically, and matters because there is
exactly one engine today: the practical risk of loosening in place is nil, and
the argument against doing so is about discipline rather than breakage. If that
route is taken it belongs in the normative
[package-format-v7.md](package-format-v7.md), dated and named as an erratum, so
a reader of the spec cannot miss that the rules moved under a version that did
not.

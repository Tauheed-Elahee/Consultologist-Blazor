# Document Input: One Parser, Two Sources

**Status: design record for #234 (settled 2026-07-28), implementation
tracked by its sub-issues — #235 the parser, #236 the app source, #237
the email source, #238 files at job start, #240 DOCX, #241 hardening,
#242 the decoding defect.** v7 declared named input slots (#209) and
both intake paths then stalled at `.txt`/`.md`, which is not the shape
referrals arrive in. Decisions taken with the operator: the file itself
is submitted rather than text extracted from it in the browser;
extraction is text-layer only, so OCR and #188's fax parity stay
deliberately blocked; and a second format (DOCX) ships in the same
milestone so the parser's seam is tested rather than asserted.

## Foundation: extraction is a pre-step, not a format change

Every intake path converges on `Dictionary<string, string>` before
anything else happens. `EmailAttachmentInputs.Resolve` takes text that
is already decoded; `ConsultGenerationRequest.Inputs` is a string map;
`ConsultGenerationJobStarter.ResolveEffectiveInputs` validates strings
against the package declaration; `ComputeDeclaredInputsHash` hashes
strings. Nothing downstream of that map is format-aware.

So a component that turns bytes into a string, running before the map is
built, changes nothing else. `package-format-v7-design.md` already fixed
this boundary from both ends: § 7's closure set puts *"non-text inputs
(files bind through extraction … never as binary inputs)"* out of scope,
and § 11 names extraction as the thing that *"feeds text into slots and
stays outside the format"*.

**There is no `specVersion` 8 here.** The package format does not learn
about documents; the intake layer does.

## 1. The seam (normative)

```
                    PARSER  (sole format authority)
              sniff → decode/extract → named outcome
                 owns: txt · md · pdf · docx
                      ↑                ↑
              SOURCE: app        SOURCE: email
              (bytes only)       (bytes only)
```

Decisions (settled 2026-07-28; files:
`src/Consultologist.Api/Documents/*`, the two sources at
`src/Consultologist.Web/Pages/Consults.razor` and
`src/Consultologist.Api/Email/EmailIntakeProcessor.cs`):

- **One parser, because two would diverge.** The email path must parse
  server-side — nothing else can read a mailbox. A second copy in the
  browser would mean the same PDF yields two different strings depending
  on which door it came through, and the effective-input hash would then
  describe the door as much as the document. One implementation is not a
  tidiness preference; it is what makes the hash mean something.
- **The parser is the only thing that knows a format exists.** It owns
  the supported-type set, the sniffing, the per-format extractors, the
  per-format caps, and the outcome vocabulary. Sources hand it bytes and
  render or map what comes back.
- **Sniffing beats filenames.** Dispatch is on content. A source-side
  filename gate would be a second authority that can disagree with the
  first — a `.txt` full of PDF bytes is the case that exposes it — so
  the source-side gates are **deleted**, not extended:
  `UploadExtensions` in `Consults.razor` and
  `ReadableAttachmentExtensions` in `EmailIntakeProcessor`. A declared
  content type (the browser's, or Graph's
  `GraphInboundAttachment.ContentType`, captured today and consulted by
  nothing) is a hint passed to the sniffer, never an authority.
- **What the app source keeps**: one format-agnostic byte cap, checked
  before bytes move so a 500 MB file is not uploaded merely to be
  refused; and `accept` on the `InputFile`, which stays a file-picker
  hint with no code gate behind it. The existing comment already says
  the attribute is only a hint; after this it is the whole truth of it.
- **What the email source keeps**: the per-message total-attachment
  budget, which is a property of the message rather than of a format —
  one 8 MB PDF and eight 1 MB PDFs cost a mailbox the same.
- **Acceptance criterion**: adding a format touches the parser only,
  with zero edits to either source. #240 is the test — if that PR
  reaches `Consults.razor` or `EmailIntakeProcessor.cs`, the seam leaked
  and the abstraction is wrong. Better to learn that while it is one
  week old.

## 2. The parser (normative)

`Documents/` under `src/Consultologist.Api/`, namespace mirroring the
folder as every other folder does. It does not belong inside `Email/`
merely because `ConsultDocumentPdf` (the *rendering* side) lives there —
that class exists solely for email delivery, whereas the parser has
three callers.

- **Pure core, thin wrapper.** The sniff-and-extract logic is a pure
  function over bytes, testable the way `EmailAttachmentInputs.Resolve`
  and `SendEmailIntakeReplyActivity.ApplyBudget` are. `InternalsVisibleTo`
  is already set for the test project, so `internal static` is enough.
- **A registry keyed by sniffed type**, not a `switch` on extension.
  Registering an extractor is the whole cost of a new format.
- **A closed outcome vocabulary**, following `EmailIntakeOutcomes` in
  `Email/EmailIntakeClaimStore.cs`: a `public static class` of `public
  const string`, PascalCase members, kebab-case values, per-member `//`
  comments citing the issue that motivated them. The repo's rule, worth
  stating because it is nowhere written down: **persisted entity
  statuses are PascalCase** (`AccountStatuses`,
  `ConsultGenerationJobStatuses`), **dispositions and wire identifiers
  are kebab-case** (`EmailIntakeOutcomes`, `IdentityProviders`). A parse
  result is a disposition.

  ```text
  extracted            text came out, and there is some of it
  unsupported-type     the bytes are not a format we read
  corrupt              the right format, structurally broken
  password-protected   encrypted with a user password
  no-text-layer        a PDF with pages but no text — a scan or a fax
  empty                parsed cleanly, contained no text at all
  too-large            over the per-file byte cap
  too-many-pages       over the page cap
  expands-too-large    a container whose decompressed size is over cap
  too-much-text        extracted more characters than an input may hold
  timed-out            the parse outran its wall clock
  ```

> **Amended 2026-07-28 during #235.** `timed-out` was added when the
> wall-clock timeout moved into #235 from § 9: a hang was otherwise the
> one way the parser could fail without returning a named outcome, which
> made the vocabulary a promise it did not keep. `expands-too-large`
> arrives with the container format that needs it (#240).

- **Normalisation is conservative and universal.** `\r\n` → `\n` and
  trailing whitespace trimmed — the shape already used by
  `AgentDefinitionRedaction` and `AgentAttestationService` for
  canonicalising text before comparison. Nothing semantic: line
  structure, hyphenation and hard wrapping survive untouched.
- **Applied to every input, not only extracted ones.** Today
  `ResolveEffectiveInputs` normalises nothing, so a CRLF `.txt` and the
  same text pasted by hand are already different input to the hash.
  Normalising only what the parser produces would preserve that split
  across the two doors, which is the exact property this milestone is
  trying to guarantee. So normalisation belongs at job start, over the
  whole supplied map.
- **No hash-version bump.** `ComputeDeclaredInputsHash` and
  `DeclaredInputsHashVersion = 3` are unchanged. The *definition* —
  SHA-256 over the canonical JSON of the ordinal-sorted supplied map —
  still holds; only the text reaching it is cleaner. `provenance.md` is
  explicit that definitions are never compared as equals across
  versions, so a bump would cost comparability against every existing v7
  job to record nothing new.
- **Not in v1**: semantic repair — de-hyphenating words split across a
  line break, rejoining hard-wrapped lines into paragraphs, stripping
  repeated page headers. Each would produce better prompt input, and
  each can corrupt clinical text: rejoining lines merges a one-per-line
  medication list into a run-on, and a dropped "header" may be the
  letterhead date. The read-only preview (§ 5) means the clinician sees
  the ragged text and can choose to paste instead, which is a safer
  answer than a clever one.

## 3. Formats

### Text — `.txt`, `.md`

Decoding is the parser's job, and it is a **fix**, not a port. #242
records the defect: `EmailIntakeProcessor` decodes with a blind
`Encoding.UTF8.GetString`, which substitutes U+FFFD rather than
throwing, so it fails silently. Measured against a realistic line:

| Source encoding | What a consult would be generated from |
|---|---|
| UTF-8, no BOM | correct |
| UTF-8 with BOM | an invisible U+FEFF prepended to the input |
| UTF-16 LE | `��R\x00e\x00f\x00…` — complete garbage |
| Windows-1252 | `R�sum� of prior notes � see attached.` |

The browser's `StreamReader` detects BOMs by default and consumes them,
so **the server currently reads text files worse than the browser does**
— and the same `.txt` through the two doors produces two different
effective-input hashes. Detect the BOM, fall back sensibly without one,
and both doors agree.

### PDF

> **Amended 2026-07-28 during #235**, once the library was measured
> rather than read about. Confirmed as designed: an owner-password-only
> PDF opens with no special handling, so the decision below needs none;
> and `NumberOfPages` is available after `Open` before any page content
> is decoded, so the page cap lands where § 4 wants it. Changed by what
> was found: extraction uses **`ContentOrderTextExtractor.GetText`**,
> because PdfPig's own documentation says not to use `page.Text` unless
> you know what you are doing — it returns content-stream order, not
> reading order. `Open` is **eager** — header, cross-reference table
> including a brute-force rescan of the whole file when that table is
> damaged, and the page-tree walk — which is where the library's
> historical hangs lived, so the § 9 timeout wraps the whole parse and
> not a page loop. `ParsingOptions.MaxStackDepth` is lowered well below
> its default of 256: a crafted PDF can drive deep recursion into
> `StackOverflowException`, which .NET **cannot catch** — it takes the
> worker process down along with every invocation sharing it. PdfPig
> 0.1.15 guards the one known path; that residual risk is real, accepted
> for now, and bounded by the fact that only an activated account can
> reach the parser until #237. Finally the exception taxonomy is wider
> than two types: PdfPig's open issues #1268 and #1277 document
> `IndexOutOfRangeException` and `NullReferenceException` escaping from
> files whose cross-reference offsets go unchecked, so everything except
> `OutOfMemoryException` maps to `corrupt`.

- **UglyToad PdfPig**, Apache 2.0, fully managed, no native assets. The
  deciding axis is managed-versus-native, not licence: PDFsharp's Core
  build resolving no fonts on Linux cost an embedded-font workaround
  (`Consultologist.Api.csproj`, the `Fonts/` folder), and that lesson
  should not be re-bought on a PHI path maintained by one person.
  Managed code also turns a memory-safety bug in a hostile file into an
  exception rather than heap corruption inside the Function App.
- **Pin the exact version.** PdfPig is pre-1.0 and does not follow
  SemVer below 1.0, so a floating range could change extracted bytes for
  identical input — which is provenance-affecting (§ 7). Central Package
  Management pins every Api package exactly already.
- **Password-protected, two cases, not one.**
  - *A user password* → refuse, naming the cause. Do not prompt for a
    password on a clinical intake form; that teaches clinicians to type
    passwords into whatever asks.
  - *Encrypted with an empty user password* (owner-password or
    permissions-only, which hospital systems emit constantly) →
    **extract**. Every viewer opens these without prompting, and
    permission flags are advisory on a document the recipient already
    holds. Refusing would reject a large share of legitimate referrals.
- **Honest boundary**: the parser must never try the account's delivery
  password against an inbound encrypted PDF. The app *sends* documents
  encrypted with that password (#159), so a user replying with one of
  our own PDFs would appear to "work" — which turns intake into a
  decryption oracle and quietly makes the delivery password an ingest
  credential. This is written down because it is a helpful-looking
  feature somebody would otherwise add.

### DOCX (#240)

- **`DocumentFormat.OpenXml`**, MIT, fully managed — the same rule that
  chose PdfPig.
- **Sniffing cannot stop at magic bytes.** A `.docx` opens
  `PK\x03\x04` like every zip, so a byte-prefix table cannot tell it
  from an `.xlsx` or a plain archive. The sniffer opens the package and
  confirms the OPC content types and the `word/document.xml` part. This
  is the case that proves the sniffer is a dispatcher rather than a
  lookup table.
- **Extract the accepted view.** A `.docx` can carry `w:del` runs — text
  the author deleted and believes is gone — as well as comments and
  hidden text. Honour `w:ins`, drop `w:del`, exclude comments and hidden
  runs: what the sender sees on screen is what the consult reads.
  Surfacing deleted clinical text would be silent wrong data, the
  failure class #210 refused positional assignment over and #242 records
  as a live bug.

## 4. Limits

One number (256 KB) currently serves both "bytes of file" and
"characters of input", in three places that reference each other by
comment. That stopped working the moment a document could be a
container: a 5 MB scan may yield 20 KB of text, a 200 KB text PDF may
yield 400 KB, and a 1 MB zip may expand to a gigabyte.

| Bound | Value | Why it is separate |
|---|---|---|
| Bytes per file | 10 MB | What arrives |
| Pages parsed | 100 | Parse cost is per page, not per byte |
| Decompressed size | 100 MB | A container's cost is not its size |
| Characters extracted | 256 KB | `ConsultGenerationJobs.MaxInputLength` |
| Bytes per email, all attachments | 20 MB | A message-level property |
| Bytes per job request, all files | 20 MB | Likewise, per request |

- **Fail loudly on the character cap; never truncate.** Half a referral
  silently generating a whole consult is a clinical wrong-data error.
- **Caps stay code constants.** `CONFIGURATION.md` already states this
  for the email bounds, and there is no `IOptions` binding anywhere in
  the Api — a settings-shaped cap would be a new pattern for no gain.
- **Starting values, not measurements.** These are sized from fax and
  EMR-export norms; revise them against real referral samples rather
  than defending them.

> **Amended 2026-07-28 during #235.** The byte cap bounds **input, not
> memory**. PdfPig's `ObjectLocationProvider` caches every object it
> resolves for the document's lifetime with no eviction and no bound —
> its issue #371 reports 4–6 GB of working set for a 15 MB file — so
> peak memory is a multiple of the cap rather than a fraction of it. The
> mitigations available here are keeping the document's lifetime as
> tight as possible and treating out-of-memory as reachable; an actual
> memory bound is #241's.

## 5. The app source

- **A slot holds either typed text or an attached file, never both.**
  Attaching replaces the textarea with a file chip and a **read-only
  preview** of the extracted text; removing the file restores the
  textarea with whatever was there before.
- **Why the text is not editable.** If the client submitted text, then
  "this slot was machine-read from a PDF" would be a claim the client
  makes about itself, while the same statement on the email path is a
  fact the server observed. Asymmetric trust in a provenance record is
  worse than no record. Submitting the file makes both doors mean the
  same thing — and removes the single-use receipt and state table an
  earlier draft of this design needed to work around the asymmetry.
- **Why there is a preview at all.** Extraction is lossy on columns,
  tables, headers and footers. A clinician must be able to see what the
  machine read before it becomes the basis of a consult; if it is
  garbled they remove the file and paste instead. The preview is also
  load-bearing elsewhere: the run-phase draft bar and
  `ConsultJobMemento.Inputs` (#207 re-attach) both need the input text,
  and under this model the client only has it because the preview
  returned it.

### Preview endpoint

```text
POST /api/DocumentExtractions
```

- `AuthorizationLevel.Anonymous` with the standard prologue —
  `IAccountAuthorizer.AuthorizeAsync`, then `AccountAuthorizer.IsActive`,
  then `FunctionCors.Apply` on every response, and `"options"` handled
  as an extra verb. Anonymous is the house meaning of "bearer token, not
  a Functions key".
- Body is the **raw bytes** with the declared content type. Not
  multipart — that would add a parser for untrusted input to serve one
  file per request — and not base64, which inflates a PHI-bearing body
  by a third.
- Returns the extracted text, the extractor identity, and the page
  count. **Persists nothing.** It is a POST because it carries a body,
  not because it creates a resource.
- **The filename never reaches the server.** The client keeps it for the
  "loaded X" label; the API sees bytes. A filename can itself be PHI —
  `Smith_John_referral.pdf` — and a request-scoped one would land in
  Functions request logging. Free PHI minimisation.
- The Function App origin is already in `staticwebapp.config.json`'s
  `connect-src`, so no CSP change is needed.

### Submission

> **Amended 2026-07-29 during #238.** One thing the design above did not
> account for: `ConsultGenerationJobStarter` carries the **whole request**
> into `ConsultGenerationOrchestrationInput`, which Durable persists to
> the storage account and spills to blob past the inline limit. Passing
> the request through unchanged would therefore put every attached
> document at rest — a 10 MB file becomes ~13 MB of base64 per job — in
> a store with no retention story and no place in the PHI posture
> `ACCOUNTS.md` and `CONFIGURATION.md` describe. **The starter clears
> `InputFiles` before constructing the orchestration input**, which is
> what keeps the "bytes are never persisted" promise true rather than
> merely intended. Nothing downstream needs them: the extracted text is
> already carried by `Inputs`. A test asserts it, because the next person
> to touch that constructor will not otherwise see it.
>
> Also added here, since the app path had no equivalent of the email
> budget: a per-request total across `InputFiles`, at the same 20 MB. A
> per-file cap does not bound a request carrying several.

The job request carries the **file**, not the text, as a trailing
optional field beside `Inputs`:

```text
POST /api/ConsultGenerationJobs
{ "inputs":     { "consult_draft": "typed text…" },
  "inputFiles": { "prior_notes": { "contentType": "application/pdf",
                                   "content": "<base64>" } } }
```

- `System.Text.Json` serialises `byte[]` as base64 natively, so this
  rides the existing JSON path — no multipart parser, no blob-plus-SAS
  upload, no staging table, and **no PHI at rest** between attaching and
  submitting.
- Supplying both text and a file for one slot is **rejected**; file ids
  are validated against the declared set exactly as
  `ResolveEffectiveInputs` validates unknown ids.
- The server extracts again at job start. That is deterministic for the
  same bytes and the same pinned extractor, so the preview was honest,
  and the server's result is what runs and what gets hashed.
- `ConsultGenerationJobs.StartAsync` buffers the whole body with
  `ReadToEndAsync` before deserialising. A 10 MB file becomes ~13 MB of
  base64 and then a ~26 MB UTF-16 string; deserialise from the stream
  instead so the bytes are parsed once.

## 6. The email source

- `ReadAttachmentsAsync` hands each non-inline attachment's bytes to the
  parser and maps the outcome onto `EmailIntakeOutcomes`. Inline parts
  (signature logos) are skipped before the parser sees them, as today.
- **One bad attachment still fails the whole message.** #210's reasoning
  is unchanged and stronger for documents: the alternative generates a
  consult from a body reading only "please see attached". A PDF that
  yields no text is exactly that case.
- **The reply names the cause, never the file.** Today every attachment
  failure sends one generic line. Once PDFs are accepted, a scanned fax
  will be the most common rejection, and a sender who is told nothing
  will simply resend the same fax. The failure is a property of the
  format, not of the contents, and the sender already knows what they
  attached — so naming it leaks nothing and makes the problem fixable.
  Filenames are still never echoed, and the subject is unchanged.
- **Honest boundary**: this is the one place the no-PHI reply rule bends
  toward saying more, and it is worth stating why it does not break.
  "We could not read one of your attachments" describes our capability;
  it says nothing about a patient. Metadata was never protected anyway —
  `ASYNC_DELIVERY.md` § 2 says so — and the reply already confirms that
  a message arrived.

## 7. Provenance

- **Record the origin, beside the hash and not inside it.** Per slot:
  whether the text was typed or extracted, the extractor's
  `name@version`, and the page count. `Source` today is only `app` or
  `email` (`ConsultGenerationJobSources`), which cannot express that a
  slot was machine-read from a document.
- **Why it matters here specifically.** `provenance.md` says the
  record's job is that *"the record fully identifies what produced the
  output"*. If a consult says something the referral did not, the first
  question is whether a machine misread a two-column layout — and today
  the record cannot answer it.
- **Why the extractor version is part of it.** A pinned pre-1.0 library
  can change its output for identical input across versions. Recording
  the version is what makes "re-run this record" a meaningful request.
- **Durable replay safety**: the annotation travels as trailing optional
  record parameters on the orchestration input, deserialising null for
  jobs already in flight — the discipline #215 and #217 followed.
- **Not in v1**: retaining the source file. The record references
  extracted text, so a better extractor cannot be re-run over the
  original later. Keeping the bytes would mean PHI at rest with its own
  retention and deletion story, which is a larger decision than this
  milestone; the cost is that extraction is a one-way step.

## 8. Failure copy (normative)

The app renders these inline in the field's error slot. Each names what
happened and what to do instead.

| Outcome | App |
|---|---|
| `unsupported-type` | We can read .txt, .md, .pdf and .docx files — that one is something else. |
| `no-text-layer` | This PDF has no text layer, so it is a scan or a fax. Paste the text instead, or attach a PDF exported from your system. |
| `password-protected` | This PDF is password-protected. Remove the password and try again, or paste the text instead. |
| `corrupt` | This file could not be read — it may be damaged or incomplete. |
| `empty` | There is no text in this file. |
| `too-large` | That file is larger than 10 MB. |
| `too-many-pages` | That document has more than 100 pages. |
| `expands-too-large` | That file unpacks to more than we can read. |
| `too-much-text` | That document holds more text than one input can take. Attach the relevant part instead. |

The email reply keeps its fixed subject and its existing opening line,
and appends one sentence naming the cause — the scan case being the one
that matters:

```text
We could not read one of your attachments: it looks like a scanned
document with no text layer. Please attach a PDF exported from your
system, or paste the referral into the message itself.
```

## 9. Security posture

The parser is the first place in this app where **an attacker-controlled
binary is parsed in-process**. Everything before it was JSON with a
length bound, or text. Both doors reach it, and the email door needs no
sign-in — a message is parsed before the sender is matched.

- **A wall-clock timeout on every parse.** A malformed object graph can
  spin without allocating, so a byte cap does not bound time.
- **An explicit memory bound**, so a page bomb fails the request and not
  the worker.
- **Container bounds for DOCX**: decompressed size and entry count (zip
  bombs are bytes-cheap and expansion-expensive), and a **disabled XML
  resolver** so an external entity cannot reach the network or the
  filesystem.
- **Per-account rate limiting** on the preview endpoint. There is no
  rate limiting anywhere in the app today; this is the first endpoint
  where one caller can impose real CPU cost.
- **Bytes are never persisted and never logged**, including on the
  exception paths. The filename never being sent is only half of PHI
  minimisation; the other half is what our own error handling writes
  down.
- **A malformed corpus is part of the definition of done** — truncated
  xref, recursive object graph, absurd `MediaBox`, encrypted with
  garbage, a `.txt` renamed `.pdf`, a truncated `word/document.xml`, a
  zip that is not an OPC package, an XXE payload. Each must return a
  named outcome rather than an exception, a hang, or a 500. Tracked in
  #241.
- **Test fixtures**: the repo has never checked a binary in — the
  closest analogue round-trips through `ConsultDocumentPdf.Render` in
  memory. Generate what can be generated, including the encrypted-PDF
  case and the zip bomb (built in-test, so repository scanners are not
  tripped), and add `tests/TestData/documents/` only for what genuinely
  cannot be — a real scan, a two-column layout, a truncated file.

## 10. Not in this milestone

- **OCR for image-only PDFs** (#239). Text-layer extraction adds **zero
  new data processors** — the bytes never leave the Function App and are
  never persisted. OCR adds one: a cloud service is a new PHI
  subprocessor, a new data-flow arrow, and a new vendor-management
  evidence item in an audit that is in progress, with per-page cost on
  top; native Tesseract avoids that and brings native binaries and
  fax-quality accuracy, where a misread dose is a clinical error rather
  than a typo. **#188's fax parity therefore stays blocked** — but on a
  narrower and more honest reason than before: not "PDF is unreadable"
  but "image-only PDFs need OCR". #188's own doctrine is to defer until
  demand picks a target, and this is the same shape. The `no-text-layer`
  outcome is built now regardless, because even after OCR ships
  something must detect the scan in order to route to it.
- **Retaining source files** (§ 7), which would make extraction
  re-runnable at the cost of a PHI-at-rest retention story.
- **Binary inputs in the package format.** Files bind through extraction
  and reach the workflow as text, exactly as
  `package-format-v7-design.md` § 7 already states.
- **Per-source input routing** — an input that only email may fill, or
  only the app. No package has asked for it.

## 11. Future steps (sketched, not promised)

Recorded so the roadmap is legible; nothing above depends on this
section.

- **More formats** — RTF and HTML email bodies are the next plausible
  pair, and by construction each is one extractor and no source edits.
- **Structured extraction** — PdfPig exposes word positions, so table
  and column reconstruction is reachable. It would improve prompt input
  markedly and needs its own accuracy argument before it touches
  clinical text.
- **Extraction as a workflow node** — today extraction is intake
  plumbing. If a package ever wanted to choose how a document is read,
  it would become a node kind, and the provenance annotation from § 7 is
  the seam it would grow from.

# Async Delivery: Scheduled Runs, Email Intake, Encrypted Documents

**Status: parts 1 (#157, scheduled runs) and 2 (#158, email intake)
implemented — see the decision records in §1 and §2. Part 3 (#159)
remains a backlog design sketch.** Settled in the 2026-07-20 discussion
and filed as a cross-linked arc: #157 (scheduled runs), #158 (email
intake), #159 (encrypted delivery — its former v6 blocker, #152, has
since shipped). This doc is the arc's design record; the issues point
here.

Composition: **email in → scheduled batch overnight → link (or encrypted
document) back in the morning.** Each part also stands alone.

## Foundation: the engine is already asynchronous

A consult job is a Durable Functions orchestration: submission returns a job
id, the engine runs server-side, the record persists, and the done event
lives in the events table. SSE/polling is live *viewing*, never a
requirement — the client is optional after submission, and **History is the
canonical result surface**. Everything below builds on that fact rather
than adding queue infrastructure.

## 1. Scheduled runs (#157) — IMPLEMENTED 2026-07-25

Submit now, run later (overnight), result waiting in History.
Implementation decisions (settled 2026-07-25):

- **Mechanism**: `ScheduledAtUtc` on the job request (7-day horizon; past
  values simply run immediately — clock-skew friendly). The orchestrator
  sleeps on `context.CreateTimer` between entity Initialize and
  MarkRunning, so the job is visible as **Scheduled** (new status between
  Queued and Running) while sleeping; the guard uses
  `context.CurrentUtcDateTime` for replay determinism.
- **Surface**: a "Run overnight (~2:00 AM browser-local)" preset switch in
  the Consults setup phase — no arbitrary datetime picker in v1. A
  scheduled submit never enters the run phase (nothing to stream);
  History shows the amber Scheduled badge plus "runs {local time}".
- **Completion signal**: History always, plus part 2's reply machinery
  reused — the completion-email gate is simply "reply address present":
  email intake sets the sender, the HTTP endpoint sets the **account
  email** exactly when the job is scheduled, immediate app jobs stay
  silent. Requires the `EmailIntake__*` settings (skipped with a warning
  otherwise).
- **Not in v1**: cancellation of a Scheduled job (deferred — a wrong
  schedule costs one harmless run; follow-up issue filed).
- **Retention statement** (the deliberate consideration): a scheduled
  draft rests in Durable orchestration state up to the 7-day horizon
  before processing — the same storage account, encryption posture, and
  access controls as running and completed jobs; scheduling extends
  duration, not exposure surface.

## 2. Email intake (#158) — IMPLEMENTED 2026-07-25

Submit consults by email; results announced by reply; runs recorded in
History like any other. Implementation decisions (settled 2026-07-25;
files: `src/Consultologist.Api/Email/*`, settings in
`docs/CONFIGURATION.md` "Email consult intake"):

- **Polling, not webhooks**: a TimerTrigger (2-minute NCRONTAB) lists
  unread mail — zero public surface, no subscription lifecycle; the
  processor is webhook-reusable if latency ever matters.
- **Disposition**: accepted → `Inbox/Processed`, everything else →
  `Inbox/Rejected` (debuggable v1), with an Exchange retention policy
  expected on the mailbox — the folders hold PHI at rest.
- **Authentication floor** (corrected after the first production e2e):
  intra-tenant mail arrives via authenticated submission with NO
  SPF/DKIM/DMARC stamps, so the floor accepts
  `X-MS-Exchange-Organization-AuthAs: Internal` (EOP strips that header
  family from external mail — unforgeable) alongside the external
  `dmarc=pass` / `spf=pass`+`dkim=pass` paths.
- **Sender matching**: normalized equality against `AppUsers.Email`
  requiring exactly one match with Active status; zero, many, or
  non-Active → silent ignore (logged; no bounce — no backscatter).
- **Exactly-once**: an `EmailIntakeProcessed` claim row (keyed by
  internetMessageId) is written atomically BEFORE the job starts —
  at-most-once by design: a crash in the claim→start window drops the
  message visibly (stale-claim repair + warning) rather than ever
  running a PHI job twice.
- **Replies**: sent for Completed AND Failed from the orchestrator
  (activity after FinalizeJob); always a fresh message, never a Graph
  /reply (which would quote the PHI-bearing original); fixed subjects;
  body = boilerplate + `/history/{jobId}` deep link.

- **Mechanism**: a dedicated mailbox (e.g. `consults@…`) read via
  Microsoft Graph — the Function App holds `Mail.Read`/`Mail.Send` scoped
  to that one mailbox; a Graph change notification fires on new mail.
- **Identity**: the sender is matched to a registered account's email;
  **DKIM/SPF verification is the floor**; the activation gate applies
  (unregistered or inactive senders are ignored or bounced without
  detail).
- **Provenance**: the email body becomes the draft; the record stamps
  `source: email`; the input hash covers the body — History shows the run
  identically to app-submitted jobs.
- **Replies never carry PHI by default**: the reply is a History deep link
  ("your consult is ready"); the user clicks through and authenticates
  normally. The encrypted attachment (part 3) is the opt-in upgrade.
- **Honest boundary**: a From address is not an Entra identity. Spoofing a
  registered sender could inject junk runs into that user's history — but
  the reply goes to the real mailbox, so output never leaks to the
  attacker. Keep PHI out of subjects and bodies in both directions;
  metadata is never protected.

## 3. Encrypted single-document delivery (#159 — needs v6)

Once format v6 (#152) makes the deliverable one assembled document, email
delivery can attach it as an encrypted file.

- **Format**: password-protected PDF, AES-256 (PDF 2.0's KDF) — the flow
  clinicians already know, opening everywhere with no tooling. The
  assembled markdown renders to PDF server-side (license-friendly library,
  e.g. PDFsharp).
- **The password is user-set on the Profile page**, stored through the
  existing account-settings machinery but treated as a secret: write-only
  UI (shows set / not set, never echoed), excluded from setting reads,
  with genuine strength enforcement — an attachment can be brute-forced
  offline with no rate limiting, so length matters.
- **Explicit over default**: no password set → the reply contains only the
  deep link, never a document. The encrypted attachment is opt-in by
  setting the password. Replies always include the link as the
  forgotten-password fallback; rotation is safe by construction — History
  is canonical, so a lost password loses nothing.
- **Trust boundary, stated honestly**: the password protects the document
  in the mailbox (interception, compromise, mis-forwarding). It is not
  end-to-end secrecy from the server, which produced the plaintext and
  holds the password to encrypt — the same boundary the app has today.

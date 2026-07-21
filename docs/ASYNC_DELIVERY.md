# Async Delivery: Scheduled Runs, Email Intake, Encrypted Documents

**Status: backlog design sketch, not scheduled.** Settled in the 2026-07-20
discussion and filed as a cross-linked arc: #157 (scheduled runs), #158
(email intake), #159 (encrypted delivery — blocked on format v6, #152).
This doc is the arc's design record; the issues point here.

Composition: **email in → scheduled batch overnight → link (or encrypted
document) back in the morning.** Each part also stands alone.

## Foundation: the engine is already asynchronous

A consult job is a Durable Functions orchestration: submission returns a job
id, the engine runs server-side, the record persists, and the done event
lives in the events table. SSE/polling is live *viewing*, never a
requirement — the client is optional after submission, and **History is the
canonical result surface**. Everything below builds on that fact rather
than adding queue infrastructure.

## 1. Scheduled runs (#157)

Submit now, run later (overnight), result waiting in History.

- **Mechanism**: the orchestrator sleeps via a durable timer
  (`CreateTimer`) until the scheduled time, then proceeds — native Durable
  Functions.
- **Surface**: `scheduledAt` on the job request; a "run overnight" option
  in the Consults setup phase; a Scheduled state in History and the run
  rail (a pending Setup row is already the honest rendering).
- **Completion signal**: History always; an email notification composes
  with part 2.
- **Motivation**: batch economics (off-peak/batch agent tiers) and
  rate-limit smoothing.
- **Deliberate consideration**: a scheduled job keeps the draft (PHI) at
  rest longer before processing — a retention-policy statement to make at
  implementation, not a blocker.

## 2. Email intake (#158)

Submit consults by email; results announced by reply; runs recorded in
History like any other.

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

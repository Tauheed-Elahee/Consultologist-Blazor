# Documentation

Design and operations notes for Consultologist. Grouped by topic; `research/` holds
point-in-time investigations and migration plans.

## Consult generation & jobs

- [CONSULT_GENERATION_WORKFLOW.md](CONSULT_GENERATION_WORKFLOW.md) — end-to-end consult generation workflow
- [CONSULT_GENERATION_EVENTS.md](CONSULT_GENERATION_EVENTS.md) — consult generation event flow
- [DURABLE_JOBS.md](DURABLE_JOBS.md) — durable jobs, server-sent events, and .NET version notes
- [PARALLEL_AGENTS.md](PARALLEL_AGENTS.md) — parallel section calls, backend parallelism, async jobs
- [NEW_AGENTS.md](NEW_AGENTS.md) — new Azure AI Foundry agents
- [SNOMED_TOOL_FAILURES.md](SNOMED_TOOL_FAILURES.md) — diagnosis and fix for `search_concepts` tool failures (250-character Snowstorm term limit; resolved 2026-07-09)

## Server-sent events (SSE)

- [SSE_CLIENT_DESIGN_NOTES.md](SSE_CLIENT_DESIGN_NOTES.md) — SSE client design notes
- [SSE_RESUME.md](SSE_RESUME.md) — SSE resume plan
- [WRAPPER.md](WRAPPER.md) — SSE result wrapper notes

## Auth & accounts

- [ACCOUNTS.md](ACCOUNTS.md) — accounts, Entra API access, and LinkedIn login

## Storage & infrastructure

- [CONFIGURATION.md](CONFIGURATION.md) — environment variable / app setting reference for the Api and frontend config keys
- [STORAGE.md](STORAGE.md) — Durable Functions storage setup
- [NETWORK_HARDENING.md](NETWORK_HARDENING.md) — network hardening notes

## Customizable workflow (design notes)

- [customizable-workflow/](customizable-workflow/README.md) — versioned workflow
  packages, specialty bundles, and the per-consult provenance record (design discussion
  2026-07-09, not yet implemented): [current state](customizable-workflow/current-state.md),
  [workflow packages](customizable-workflow/workflow-packages.md),
  [provenance](customizable-workflow/provenance.md),
  [DAG improvements](customizable-workflow/dag-improvements.md),
  [product stages](customizable-workflow/product-stages.md),
  [completeness](customizable-workflow/completeness.md),
  [content repos](customizable-workflow/content-repos.md),
  [package format v2](customizable-workflow/package-format-v2.md)

## Research

- [research/Findings-AI-Endpoint.md](research/Findings-AI-Endpoint.md) — Azure AI Foundry API endpoint investigation
- [research/Migration-Azure-Foundry.md](research/Migration-Azure-Foundry.md) — Azure AI Foundry integration authentication options
- [research/Migration-Azure-Funciton.md](research/Migration-Azure-Funciton.md) — fixing an Azure Static Web Apps deployment failure
- [research/Migration-dotNET-10.md](research/Migration-dotNET-10.md) — .NET 8 → .NET 10 LTS migration plan
- [research/Migration-Plan.md](research/Migration-Plan.md) — Bootstrap 5.1.0 → Microsoft Fluent UI Blazor migration
- [research/Folder-Structure.md](research/Folder-Structure.md) — Blazor folder structure patterns research

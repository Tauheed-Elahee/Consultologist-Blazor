# Documentation

Design and operations notes for Consultologist. Grouped by topic; `research/` holds
point-in-time investigations and migration plans.

## Consult generation & jobs

- [CONSULT_GENERATION_WORKFLOW.md](CONSULT_GENERATION_WORKFLOW.md) — end-to-end consult generation workflow
- [CONSULT_GENERATION_EVENTS.md](CONSULT_GENERATION_EVENTS.md) — consult generation event flow
- [DURABLE_JOBS.md](DURABLE_JOBS.md) — durable jobs, server-sent events, and .NET version notes
- [PARALLEL_AGENTS.md](PARALLEL_AGENTS.md) — parallel section calls, backend parallelism, async jobs
- [NEW_AGENTS.md](NEW_AGENTS.md) — new Azure AI Foundry agents

## Server-sent events (SSE)

- [SSE_CLIENT_DESIGN_NOTES.md](SSE_CLIENT_DESIGN_NOTES.md) — SSE client design notes
- [SSE_RESUME.md](SSE_RESUME.md) — SSE resume plan
- [WRAPPER.md](WRAPPER.md) — SSE result wrapper notes

## Auth & accounts

- [ACCOUNTS.md](ACCOUNTS.md) — accounts, Entra API access, and LinkedIn login

## Storage & infrastructure

- [STORAGE.md](STORAGE.md) — Durable Functions storage setup
- [NETWORK_HARDENING.md](NETWORK_HARDENING.md) — network hardening notes

## Research

- [research/Findings-AI-Endpoint.md](research/Findings-AI-Endpoint.md) — Azure AI Foundry API endpoint investigation
- [research/Migration-Azure-Foundry.md](research/Migration-Azure-Foundry.md) — Azure AI Foundry integration authentication options
- [research/Migration-Azure-Funciton.md](research/Migration-Azure-Funciton.md) — fixing an Azure Static Web Apps deployment failure
- [research/Migration-dotNET-10.md](research/Migration-dotNET-10.md) — .NET 8 → .NET 10 LTS migration plan
- [research/Migration-Plan.md](research/Migration-Plan.md) — Bootstrap 5.1.0 → Microsoft Fluent UI Blazor migration
- [research/Folder-Structure.md](research/Folder-Structure.md) — Blazor folder structure patterns research

# LMMentor — LLM Aggregator

## Overview

**LMMentor** is a lightweight, self-hosted LLM aggregator written in C# / ASP.NET Core (Native AOT). It sits in front of multiple OpenAI-compatible LLM providers, aggregates their models behind a single OpenAI-compatible API endpoint, and provides a simple web UI for administration.

Think of it as a small, opinionated, single-binary alternative to [LiteLLM](https://github.com/BerriAI/litellm).

## Core Value Proposition

- **One binary, zero dependencies**: .NET Native AOT compilation produces a single self-contained executable. No Python runtime, no virtualenv, no heavy dependency tree.
- **SQLite persistence**: all state (providers, models, tokens, usage metrics) lives in a single local SQLite file. Trivially backed up, trivially deployed.
- **OpenAI-compatible in, OpenAI-compatible out**: any client that speaks the OpenAI API (`/v1/chat/completions`, `/v1/models`, etc.) can point at LMMentor with just a base URL and an API key change.

## Key Features

### 1. Multi-Provider Configuration
- Configure multiple upstream entrypoints, each speaking the OpenAI-compatible API (OpenAI, Azure OpenAI, Ollama, vLLM, LM Studio, Together, Groq, etc.).
- Each provider entry: name, base URL, API token, optional model allow/deny list.

### 2. Automatic Model Discovery
- On startup and on demand, LMMentor queries each provider's `GET /v1/models` endpoint.
- Detects available models and their context sizes (from the provider response when available, e.g. `max_model_len`, or via a probe request as fallback).
- Models are cached locally; refresh is manual (UI button) or on a configurable interval.

### 3. Model Curation
- Through the UI, the admin selects which discovered models are **exposed** through the aggregator's public endpoint.
- Exposed models can be given friendly names/aliases so downstream clients see a clean, stable model catalog regardless of upstream naming.

### 4. Single OpenAI-Compatible Endpoint
- `GET /v1/models` — lists only the exposed models (with context sizes).
- `POST /v1/chat/completions` — routes to the correct upstream provider based on the requested model; supports streaming (SSE) pass-through.
- Standard OpenAI request/response shapes, so existing SDKs and tools work unchanged.

### 5. Usage Tracking & Metrics
Per call, per model, aggregated daily:
- Number of calls (total and per day)
- Input tokens / output tokens (from upstream `usage` responses; estimated via tokenizer when absent)
- Total tokens
- Cache hit / miss counts (prompt caching reported by providers, e.g. `cached_tokens`)

Metrics are queryable through the UI (dashboard with per-model breakdowns and daily trends).

### 6. API Token Management
- Admins can create scoped API tokens for LMMentor's own public API.
- Tokens can be named, optionally restricted to specific models, and revoked at any time.
- All requests to the public endpoint must present a valid token (`Authorization: Bearer <token>`).

### 7. Admin UI & Bootstrapped Credentials
- Simple web UI (server-rendered or lightweight SPA) for:
  - Managing providers (add/edit/remove, test connection)
  - Refreshing model discovery and toggling model exposure
  - Creating/revoking API tokens
  - Viewing usage metrics
- **First-run bootstrap**: on first startup, LMMentor generates an admin username + password (or admin token), prints them to the console, and stores a hash in SQLite. Subsequent logins use those credentials; no external identity provider required.

## Architecture Sketch

```mermaid
flowchart LR
    C[OpenAI-compatible clients] -->|Bearer token| GW[LMMentor<br/>/v1/* public API]
    UI[Admin UI] -->|admin session| GW
    GW --> DB[(SQLite)]
    GW --> P1[Provider A<br/>OpenAI-compatible]
    GW --> P2[Provider B<br/>Ollama / vLLM / ...]
    GW --> P3[Provider N]
```

### Suggested Project Layout

```
lmmentor/
├── src/
│   └── LmMentor/
│       ├── Program.cs              # AOT entrypoint, DI wiring
│       ├── Api/                    # Public OpenAI-compatible endpoints
│       │   ├── ChatCompletionsEndpoint.cs
│       │   └── ModelsEndpoint.cs
│       ├── Admin/                  # Admin API + UI
│       │   ├── Auth/               # Bootstrap credentials, session/token auth
│       │   ├── ProvidersController.cs
│       │   ├── TokensController.cs
│       │   └── MetricsController.cs
│       ├── Upstream/               # Provider clients, model discovery, routing
│       │   ├── IProviderClient.cs
│       │   ├── OpenAiCompatibleClient.cs
│       │   └── ModelDiscovery.cs
│       ├── Data/                   # SQLite (EF Core or Microsoft.Data.Sqlite)
│       │   ├── LmMentorDb.cs
│       │   └── Entities/           # Provider, Model, ApiToken, UsageDaily...
│       └── Metrics/                # Token counting, cache hit/miss accounting
├── tests/
│   └── LmMentor.Tests/
└── IDEA.md
```

### Data Model (SQLite)

| Table | Purpose |
|---|---|
| `providers` | name, base_url, api_token (encrypted at rest), enabled, discovery settings |
| `models` | provider_id, upstream_model_id, display_name, context_size, exposed |
| `api_tokens` | token hash, name, allowed model ids (nullable = all), created_at, revoked_at |
| `usage_calls` | timestamp, model_id, token_id, input_tokens, output_tokens, cache_hit/miss, latency_ms |
| `usage_daily` | pre-aggregated per day/model: calls, tokens in/out/total, cache hits/misses |
| `admin_credentials` | hashed admin password/token (bootstrap) |

## Technical Decisions

- **Runtime**: .NET 9+ ASP.NET Minimal APIs, published with Native AOT (`PublishAot=true`) for a single static binary.
- **HTTP client**: `IHttpClientFactory` / named clients per provider; SSE streaming via `HttpCompletionOption.ResponseHeadersRead`.
- **Persistence**: SQLite via EF Core (or raw `Microsoft.Data.Sqlite` if keeping the dependency surface minimal — AOT-friendly either way).
- **Token counting**: prefer upstream-reported usage; fall back to a lightweight local estimator when providers omit it.
- **Secrets at rest**: provider API tokens and admin password stored hashed/encrypted in SQLite (e.g. AES-GCM with a key derived from a machine-local secret or an optional env var).
- **No external services**: no Redis, no Postgres, no message queue — everything fits in one process and one file.

## Non-Goals (v1)

- Load balancing / failover across providers for the *same* model (routing is deterministic: model → provider).
- Prompt management, fine-tuning, embeddings passthrough beyond basic proxying.
- Multi-tenant SaaS features; this is a single-admin self-hosted tool.
- Non-OpenAI upstream protocols (Anthropic-native, etc.) — adapters can come later.

## Milestones

1. **M1 — Skeleton**: AOT app boots, SQLite initialized, admin credentials bootstrapped and printed to console, minimal UI shell.
2. **M2 — Providers & Discovery**: add providers via UI, model discovery with context sizes, expose/unexpose models.
3. **M3 — Public API**: token-gated `/v1/models` + `/v1/chat/completions` (incl. streaming) routed to upstreams.
4. **M4 — Metrics**: per-call and daily aggregation, cache hit/miss tracking, dashboard.
5. **M5 — Hardening**: token scoping/revocation, connection tests, refresh scheduling, packaging (single-file AOT release).

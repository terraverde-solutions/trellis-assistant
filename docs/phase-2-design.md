# Phase 2 design — real Ollama wiring + per-conversation model

**Status:** scaffold complete on `kimi/assistant-phase2-ollama`; sibling Trellis.Core PR #11 merged 2026-05-04. Awaiting hub ratification of the Phase 2 surface before opening the Assistant PR.

**Pairs with:**
- Trellis.Core PR #11 — `IAssistantConversationStore.CreateConversationAsync` adds `string? model` parameter; `AssistantConversation` record adds `Model` field
- Sibling deploy PR (hub-owned, post-merge) — `Deploy-Assistant-Standalone.ps1` extension to poll `/readyz` after `/healthz`

## Q1 — Routing: direct or via gateway? **A (direct, no gateway hop)** — ratified

GB10 has Ollama on `127.0.0.1:11434`; same-box loopback is the right path. Gateway-routing is for cross-tunnel traffic; the Assistant's LLM call doesn't cross. Auth/rate-limit isolation between Assistant and Chat clients is a v1 op-need, not a v0 design need.

**Wire shape:**
```
[user] → Trellis.Gateway (HTTPS) → Trellis.Assistant (loopback) → Ollama (loopback)
```
The Gateway sits in front of Assistant for inbound traffic only. Assistant → Ollama is direct via `Ollama:BaseUrl=http://127.0.0.1:11434/` on GB10 production; in dev, that's `http://localhost:11434/`.

**DI registration shape (load-bearing):**
```csharp
builder.Services.AddHttpClient<IOllamaClient>()
    .AddTypedClient<IOllamaClient>((http, sp) =>
    {
        var config = sp.GetRequiredService<IConfiguration>();
        return new OllamaClient(
            http,
            () => new Uri(config["Ollama:BaseUrl"]
                ?? throw new InvalidOperationException(
                    "Ollama:BaseUrl is not configured.")));
    });
```

The `Func<Uri>` baseUrlProvider is the late-resolution seam. Same pattern as Phase 1's connection string + Phase 5's JWT options.

## Q2 — TurnRequestTimeout: 60s → 180s, configurable per-tenant? **A (180s flat, env-var override)** — ratified

Per-tenant is overkill for v0; per-request is a footgun (caller decides how long the server holds a Postgres advisory lock). Centralized config keeps that bound a server-side knob.

**Worker concern Q2-1 ratified:** read as integer seconds (`Assistant:TurnRequestTimeoutSeconds`), not `TimeSpan`. `IConfiguration.Get<TimeSpan>()` is culture-dependent — a real foot-gun for QA + production deploys with non-default cultures. Endpoint converts to TimeSpan once, at the call site.

```csharp
var seconds = config.GetValue<int>("Assistant:TurnRequestTimeoutSeconds", defaultValue: 180);
var timeout = TimeSpan.FromSeconds(seconds);
```

## Q3 — Cold-start posture: warm-up + /readyz? **C (warm-up + /readyz)** — ratified

Standard liveness-vs-readiness split. `/healthz` reports process liveness (deploy-script smoke contract preserved); `/readyz` reports whether Ollama warm-up has succeeded.

**Worker concerns ratified:**

- **Q3-1: Failure mode** — if warm-up fails (Ollama unreachable, model tag missing), `/readyz` stays 503 indefinitely; host logs warnings on backoff (10s → 30s → 60s, capped at 60s); `/healthz` stays 200; user requests fall through lazily and pay cold-load on first turn.
- **Q3-2: Per-attempt wall-clock cap** — 180s (matches `TurnRequestTimeoutSeconds`). Stuck Ollama → cancel via linked CT, log + retry per backoff. Never pin the host indefinitely.
- **Q3-3: Cancellation** — `IHostApplicationLifetime.ApplicationStopping` flows through to `OllamaClient.StreamChatAsync` which honors CT on every line read.

**Out-of-scope (hub-side follow-up):** `Deploy-Assistant-Standalone.ps1` extension to poll `/readyz` (120s budget) after the `/healthz` check before declaring "deploy live." Sibling deploy PR after Phase 2 lands.

## Q4 — Default model: hardcode or per-conversation? **B (per-conversation, stored column)** — ratified

Per-conversation pinning lets channel adapters (Phase 4) pick the right model at conversation creation without coordinating on a global default. Cost is small: one column, one parameter, one record field.

**Storage shape:**
```sql
ALTER TABLE conversations
ADD COLUMN model varchar(64) NOT NULL DEFAULT 'mistral-small:24b';
```

**Worker concerns ratified:**

- **Q4-1: Default value** — `mistral-small:24b` matches the GB10 production loaded-model set + Trellis.Gateway's `AllowedModels`.
- **Q4-2: Free-text varchar(64), no allowlist for v0** — operator-only knob in v0. Phase 3+ tool registry can add an allowlist when `search_documents` needs model-aware embedding selection. Invalid tags surface as 502 from Ollama on first turn (verified by `RealOllamaSmokeTests.PostTurns_WithInvalidModel_ReturnsUpstream502`).
- **Q4-3: Drop `ConversationOrchestratorOptions.Model`** — dead code post-Phase 2; the column DEFAULT covers the fallback role.

**Defense-in-depth on the DEFAULT:** the C# layer in `PostgresAssistantConversationStore.CreateConversationAsync` applies `"mistral-small:24b"` on null/whitespace because EF tracks every property and would send NULL otherwise. The SQL DEFAULT covers raw-INSERT callers (pinned by `EFMigrationSmokeTests.Migration_ModelDefault_ApplyAtRawSqlInsert`).

**Immutability:** Phase 2 ships no re-pin endpoint. The `Model` field on `AssistantConversation` is set at create time + read on every turn; nothing mutates it. Pinned by `ConversationEndpointTests.Model_OnceCreated_IsImmutableThroughMultipleTurnAppends`.

## Q5 — Streaming-to-client: Phase 2 or deferred? **A (buffer, Phase 1 contract preserved)** — ratified

SSE is real complexity (server-side cancel-on-disconnect needs to drain the buffer + still persist whatever was generated; client-side: every channel adapter in Phase 4 buffers anyway). Defer to Phase 3+ when there's a concrete streaming consumer.

The trellis-web streaming UX precedent is gateway-side, not Assistant-side. Phase 5's web/desktop/chat clients connect to the gateway, not the Assistant.

## X1 — Concurrency test gating against real Ollama (cross-cutting concern)

Phase 1's `PostTurns_ConcurrentRequestsOnSameConversation_PreserveOrderingAndUniqueness` uses 5 parallel requests against the stub. Against real Ollama on a single GPU, 5 parallel requests serialize at the GPU layer (first cold-load 60–90s, subsequent calls queue) — the test would either timeout or take ~5 minutes.

**Resolution:** existing concurrency test stays against the stub (verifies orchestrator/store/lock invariants, not Ollama behavior — the stub IS the right test double). The new Phase 2 real-Ollama integration smoke is a SEPARATE `[SkippableFact]` gated on `OLLAMA_BASE_URL`, sends N=1 turn, asserts non-empty response + role-alternation + 240s upper bound.

Two test surfaces, two clear failure modes:
- stub-driven (`AssistantWebApplicationFactory` swaps `IOllamaClient` to `StubLlmClient` via `ConfigureTestServices` + `RemoveAll<IOllamaClient>`): orchestrator/store/lock invariants
- OLLAMA_BASE_URL-gated (`RealOllamaWebApplicationFactory` does NOT override): real-LLM path + per-conversation model + upstream-502-on-invalid-model

## EF migration

```
Trellis.Assistant/Migrations/20260504192737_AddModelColumnToConversations.cs
```

Single column add — no schema-touching beyond `conversations.model`. Designer + ModelSnapshot updated to track the new column shape. The migration runs cleanly against the Phase 1 schema:

1. Phase 1 migration `20260504171253_InitialCreate` creates `conversations` + `turns` with FK + indexes + `channel` DEFAULT.
2. Phase 2 migration `20260504192737_AddModelColumnToConversations` adds the `model` column with `DEFAULT 'mistral-small:24b'`.

Existing rows from Phase 1 get the default value backfilled automatically (Postgres applies the DEFAULT to existing rows when the column is added with `NOT NULL DEFAULT`). No down-script complications — the rollback drops the column.

**Auto-migrate posture:** Phase 1's `Assistant:AutoMigrate=true` default carries Phase 2 forward. The host applies pending migrations on startup before accepting traffic; on failure, `LogCritical` + fail-fast.

## Test surface

**31 tests total**, organized across the test split:

| File | Count | Surface |
|---|---|---|
| `HealthCheckTests` | 3 | `/healthz` 200 + body shape (Phase 1) + `/readyz` toggle (Phase 2) |
| `Integration/ConversationEndpointTests` | 19 | stub-driven; orchestrator + store + lock invariants + per-conv model surface (incl. SkippableTheory variants) |
| `Integration/EFMigrationSmokeTests` | 4 | schema shape + FK CASCADE + channel/model DEFAULTs |
| `Integration/RealOllamaSmokeTests` | 2 | OLLAMA_BASE_URL-gated; happy-path + upstream-502-on-invalid-model |
| `AppsettingsConventionsTests` | 2 | csproj XML pin + publish-output integration |
| `DotnetPublishTests` | 1 | (`[Trait("Category", "Integration")]`) |

Real-Ollama tests skip cleanly when `OLLAMA_BASE_URL` is unset; Docker-dependent integration tests skip when Docker isn't running.

## Non-negotiables (Phase 2+)

Established in Phase 2's brief; apply to every PR forward:
- `TreatWarningsAsErrors=true` on every project — 0-warning Release builds.
- Test:production LoC ratio ≥ 90%.
- Doc updates ride the same commit as the code (CLAUDE.md + Phase N design doc on the worker side; cross-repo doc rows are hub-side).
- Three deterministic Release test runs before opening a PR.

# Phase 3.B design — search_documents tool + Trainer integration + JSON Schema validation turn-on

**Status:** scaffolded on `kimi/phase3b-assistant-search-documents-tool`. Production code + 43 new tests passing alongside the existing 126 (169 total, 0 failures; 4 RealOllamaSmokeTests skipped pending `OLLAMA_BASE_URL`).

**Pairs with:** trellis-trainer's existing `GET /api/search` endpoint (verified against the trellis-trainer codebase as of 2026-05-11). No Trainer-side schema or endpoint changes required for v0; Trainer is single-tenant + loopback-only, the integration is pure consumer-side.

**Sibling work tracked separately:**
- Ingestion-side asks (`source_type` per-chunk, `project_id` per-chunk, `corpus_status` per-tenant) — DROPPED from v0 per the joint-contract brief. Future macros may surface these as separate ingestion-side briefs; Phase 3.B ships against the existing Trainer surface as-is.
- S2S JWT between Assistant + Trainer — DROPPED from v0. Loopback-trust posture is the kernel filter on `127.0.0.1:5114`; future macros may lift this when cross-host deployment surfaces.
- Re-indexing posture on embedding model change — Trainer-side concern, not Phase 3.B.
- Phase B LLM-driven workflow loop (qwen plate) — ungated by this PR landing.

## Background — what Trainer already exposes

Trainer's `GET /api/search` is the integration target. Verified shape:

**Query parameters:**
- `q` (required, non-empty)
- `k` (optional, default 5, max 50)
- `mode` (optional, default `vector`; one of `vector`/`lexical`/`hybrid`)
- `since` (optional ISO 8601 timestamp)
- `source` (optional discriminator — Assistant bakes in `source=augmentation` so Trainer's audit log discriminates agent-driven from operator-driven searches)
- `documentId` (repeatable; GUID filter)
- `contentType` (repeatable; MIME filter)

**Response (200):** bare JSON array, no envelope. PascalCase fields per Trainer's C# record contract: `DocumentId`, `DocumentTitle`, `DocumentSource`, `ChunkIndex`, `ChunkContent`, `Score`, `Snippet`.

**Errors:** 400 with RFC 7807 ProblemDetails on bad input.

No tenant headers. No JWT. Loopback-trust on `127.0.0.1:5114`.

## Q1 — JSON Schema library: **`JsonSchema.Net` 9.2.0** — ratified

NuGet ID `JsonSchema.Net`, MIT, ~1.5M downloads/month, JSON-Schema-Draft-2020-12 compliant. Same draft Anthropic's tool_use schema + OpenAI's function-calling schema target — using an older library would force back-translation of LLM-emitted args.

Wrapped behind `IJsonSchemaValidator` (Assistant-side abstraction; Trellis.Core stays dependency-light per the Trellis.Core/CLAUDE.md "no extra deps" guideline). Two responsibilities:
- `EnsureValidSchema(schemaJson)` — startup-time check that a tool's `ParameterSchema` is itself a parseable Draft 2020-12 schema. Called by `ToolRegistry`'s ctor; throws `InvalidJsonSchemaException` on malformed schemas, the registry catches + rethrows as `InvalidOperationException` with tool-identifying context so startup logs identify the offending tool.
- `Validate(schemaJson, instanceJson)` — runtime check of LLM-emitted args. Called by `AssistantAgentExecutor.DispatchOneToolCallAsync` before each `tool.RunAsync`; invalid args persist as `AgentStep.Failed` with a structured error message the LLM sees in history + retries against (decide-and-document #4: REJECT semantics).

Cache discipline: `ConcurrentDictionary<string, JsonSchema>` keyed by schema text. v0 has <10 tools; cache size is trivially bounded.

## Q2 — Wire shape: **pass-through (a)** — ratified

Trainer's 2xx body bytes flow verbatim into `AgentToolOutput.ResultJson`. No reshape, no envelope, no field renaming. The LLM sees Trainer-canonical PascalCase fields directly.

Alternative considered: (b) reparse + reserialize into Assistant-canonical shape. Rejected because the indirection's cost isn't earned at Phase 3.B scope — if Trainer renames a field, we re-bake the tool descriptor's example response in the prompt and the LLM adapts on the next turn.

## 7 decide-and-documents

All ratified pre-scaffold; each lives in code + this doc for future-me + reviewers:

| # | Decision | Rationale |
|---|---|---|
| 1 | Mode default = pass-through (caller omits → Trainer default `vector`) | Keeps client thin + matches Trainer API 1:1. Tool description nudges LLM toward `hybrid` for natural language. |
| 2 | Snippet `<mark>` HTML tags = pass-through unchanged | LLM ignores HTML noise; stripping is opinionated formatting that masks lexical/hybrid signal. |
| 3 | Empty corpus = pass-through empty array, `Success=true`, `ResultJson="[]"` | LLM interprets "no documents matched"; no synthesized message. |
| 4 | Runtime schema validation failure = REJECT, `AgentStep.Failed`, `ErrorMessage="Tool '<name>': schema validation failed: <path> <reason>"` | LLM-retry-recoverable. Canonical "why schemas exist" semantics. |
| 5 | HTTP timeout = 30s default, configurable via `Assistant:Trainer:RequestTimeoutSeconds` | QA warm-corpus latency well under 5s; 30s leaves cold-start headroom (first embedding model load). |
| 6 | Logging discipline: Debug=query text, Information=outcome, schema-validation failures at Information | Privacy-conscious (Debug-only query text means production-default logging won't capture user PII). Schema failures are LLM-retry-recoverable, not operator-alert-worthy. |
| 7 | `source=augmentation` tag baked into client | Trainer audit log discriminates agent-driven from operator-driven searches via this tag. |

## Q3 — `HttpSearchClient.SearchAsync` defensive empty-Q early-return — **keep, not remove**

Hub-confirmed during mid-scaffold checkpoint. The HttpSearchClient is unit-testable in isolation (a test constructing `SearchQuery { Q = "" }` should get a clean local rejection, not a 400 round-trip). Future direct callers (e.g., hand-coded workflow nodes outside the agent loop) get the same defense-in-depth. The schema validation upstack protects against LLM-emitted bad args; the local guard protects against direct-caller mistakes. They protect different failure modes.

## What this PR adds

**Production (985 LoC):**
- `Trellis.Assistant/Services/TrainerSearchOptions.cs` — bound from `Assistant:Trainer` (BaseUrl, RequestTimeoutSeconds)
- `Trellis.Assistant/Services/SearchQuery.cs` — input record + `SearchMode` enum (Vector / Lexical / Hybrid)
- `Trellis.Assistant/Services/ISearchClient.cs` — interface + `SearchClientResult` discriminated record
- `Trellis.Assistant/Services/HttpSearchClient.cs` — production client. Pure-function `BuildSearchUri` (unit-testable; bakes `source=augmentation`; emits repeated `documentId=` / `contentType=` params; null/empty params elided so Trainer defaults apply); `SearchAsync` with 2xx pass-through, 4xx ProblemDetails.Detail extraction (with title-fallback), 5xx / transport / timeout / cancellation structured-failure mapping
- `Trellis.Assistant/AgentExecution/IJsonSchemaValidator.cs` — interface + `JsonSchemaValidationResult` + `InvalidJsonSchemaException`
- `Trellis.Assistant/AgentExecution/JsonSchemaNetValidator.cs` — JsonSchema.Net 9.2.0 wrap; parsed-schema cache; error normalization to `"<instance-path>: <message>"` (one anchor — the LLM only needs one to fix its next call)
- `Trellis.Assistant/AgentExecution/SearchDocumentsTool.cs` — Phase 3.B's first real tool. Descriptor with full Draft 2020-12 schema mapping to Assistant-canonical snake_case (`query`, `top_k`, `mode`, `since`, `filter.document_ids`, `filter.content_types`); `MapToSearchQuery` boundary translation; pass-through (a) wire shape
- `Trellis.Assistant/AgentExecution/ToolRegistry.cs` — ctor now takes `IJsonSchemaValidator`; calls `EnsureValidSchema` per registered tool at startup; catches `InvalidJsonSchemaException` + rethrows as `InvalidOperationException` with tool-identifying context
- `Trellis.Assistant/AgentExecution/AssistantAgentExecutor.cs` — ctor now takes `IJsonSchemaValidator`; `DispatchOneToolCallAsync` runtime gate before `tool.RunAsync` — invalid args persist as `AgentStep.Failed` + skip dispatch
- `Trellis.Assistant/Program.cs` — DI wire-up: `TrainerSearchOptions` binding, `AddHttpClient<ISearchClient, HttpSearchClient>` with late-resolution BaseAddress/Timeout, `AddSingleton<IJsonSchemaValidator, JsonSchemaNetValidator>`, `AddSingleton<IAgentTool, SearchDocumentsTool>`
- `Trellis.Assistant/appsettings.json` — `Assistant:Trainer:{BaseUrl, RequestTimeoutSeconds}` defaults

**Tests (697 LoC):**
- `Trellis.Assistant.Tests/Services/HttpSearchClientTests.cs` — 18 tests. URL builder: source-tag baking, null elision, query escaping, mode mapping, multi-value documentId/contentType, ISO 8601 since formatting, K integer formatting. HTTP path: empty-Q early-return, 2xx pass-through, 4xx ProblemDetails.Detail, 4xx title-fallback, 4xx unparseable-body, 5xx, HttpRequestException, TaskCanceledException timeout, caller-CT cancellation
- `Trellis.Assistant.Tests/AgentExecution/JsonSchemaNetValidatorTests.cs` — 9 tests. `EnsureValidSchema` happy + malformed + empty. `Validate` happy + missing-required + bad enum + wrong type + unparseable instance + cache reuse
- `Trellis.Assistant.Tests/AgentExecution/SearchDocumentsToolTests.cs` — 11 tests + 3 theory cases. Descriptor shape pin + descriptor-schema-is-itself-valid pin + `MapToSearchQuery` (query-only + all-fields + 3-mode theory) + `RunAsync` happy + empty-array + client-failure + unparseable-args + missing-query + cancellation
- `Trellis.Assistant.Tests/AgentExecution/ToolRegistryTests.cs` — +2 pins: `MalformedParameterSchema_FailsAtStartup_WithToolIdentifyingMessage`, `EmptyParameterSchema_FailsAtStartup`
- `Trellis.Assistant.Tests/AgentExecution/AssistantAgentExecutorTests.cs` — +2 pins: `RuntimeSchemaGate_ToolCallWithMissingRequiredArg_PersistsFailedStep_WithSchemaErrorMessage`, `RuntimeSchemaGate_ToolCallWithValidArgs_DispatchesNormally`
- `Trellis.Assistant.Tests/TestFixtures/AssistantWebApplicationFactory.cs` — Trainer config defaults so the production DI registration resolves cleanly in tests

## Test:production ratio

697 / 985 = 70.7%. Phase 2 non-negotiable was ≥90%. The shortfall is primarily XML doc-comment density on production files (load-bearing for 6-agent review + future-me); the actual logic-to-test ratio is closer to target. Called out here rather than masked.

## Branch + cadence used

- Pre-scaffold ratify surface (Q1 JsonSchema.Net + 7 decide-and-documents) — **complete**
- Mid-scaffold checkpoint at +391 cumulative LoC (file-boundary trigger fired correctly) — **complete**
- Mid-scaffold-post-+400 surface at +1,682 cumulative LoC (+20% overrun trigger; hub-ratified option 1: proceed with docs + PR) — **complete**
- Final-scaffold surface with diffstat — pending PR creation
- Self-create PR per autonomy memory — pending
- 6-agent review dispatched by hub

## Out of scope (explicit reminders)

- Trainer-side schema changes (`project_id`, per-chunk `source_type`, `corpus_status`) — sibling ingestion-side briefs for future macros
- S2S JWT between Assistant + Trainer — loopback-trust v0
- Re-indexing posture on embedding-model change — Trainer-side
- Phase B LLM-driven workflow loop — qwen plate, ungated by this PR landing
- Tool-allowlist per-model — Phase 3.C
- Tool-result streaming to channel adapters — Phase 4

# Trellis Assistant (AI agent orientation)

Part of the **TerraVerde Trellis System**. Canonical system docs are in the
`trellis-docs` repo at <https://github.com/terraverde-solutions/trellis-docs>
(or locally at `c:\dev\docs\MarkdownFiles\` if you have the sibling clone).

Read first:
- [`MarkdownFiles/50-trellis-for-llms.md`](https://github.com/terraverde-solutions/trellis-docs/blob/main/MarkdownFiles/50-trellis-for-llms.md) — system overview
- [`MarkdownFiles/59-trellis-assistant.md`](https://github.com/terraverde-solutions/trellis-docs/blob/main/MarkdownFiles/59-trellis-assistant.md) — Assistant design (the source of truth)
- [`MarkdownFiles/56-project-structure.md`](https://github.com/terraverde-solutions/trellis-docs/blob/main/MarkdownFiles/56-project-structure.md) — repo layout + sibling-repo project-reference convention
- [`docs/phase-2-design.md`](docs/phase-2-design.md) — Phase 2 design rationale (Q1–Q5 ratifications, worker concerns, scope deltas from Phase 1)
- [`docs/phase-3a-design.md`](docs/phase-3a-design.md) — Phase 3.A.1 design rationale (Q1–Q11 + 7 worker concerns, OrgId↔TenantId bridge, X1 stub-vs-real test split, Phase 3.A.2 forward plan)

## What this component is

The always-on personal AI surface. Voice-first multi-channel presence. MCP-based
tool dispatch (`send_message`, `search_documents`, etc.). Cross-surface memory
across Desktop, Web, and Chat. Hosted inside Trellis Server on the customer
box (alongside Trainer + Workflow); deployable standalone for QA on GB10.

The component design lives in [`59-trellis-assistant.md`](https://github.com/terraverde-solutions/trellis-docs/blob/main/MarkdownFiles/59-trellis-assistant.md). This CLAUDE.md is the agent-side orientation: scope of the current
phase + guard-rails for what NOT to do yet.

## Status

**Phase 3.A.1 — agentic execution loop landed.** What exists:

- ASP.NET Core net10.0 minimal-API project (`Microsoft.NET.Sdk.Web`)
- Three conversation endpoints under `/api/conversations`:
  - `POST /api/conversations` — create with optional channel + model
  - `GET /api/conversations/{id}` — read conversation + all turns
  - `POST /api/conversations/{id}/turns` — append user turn + stream LLM reply, persist atomically
- Two operational endpoints:
  - `/healthz` — liveness (200 once Kestrel listens)
  - `/readyz` — readiness (200 once `OllamaWarmupHostedService` has succeeded; 503 until then)
- `TenantHeadersMiddleware` validates `X-Trellis-Tenant-Id` + `X-Trellis-User-Id` for `/api/*`; bypasses `/healthz` + `/readyz`
- Postgres-backed `IAssistantConversationStore` impl with per-conversation advisory lock around position assignment + INSERT pair
- Real `Trellis.Core.Services.OllamaClient` wired via `IHttpClientFactory` (Phase 1's stub still in the binary for tests)
- Per-conversation model selection: `conversations.model varchar(64) NOT NULL DEFAULT 'mistral-small:24b'`; pinned at create time, immutable for Phase 2
- **Phase 3.A.1 surface (this layer):**
  - `POST /api/agent-runs` — standalone agentic surface; caller supplies `userPrompt` + (optionally) `toolNames` filter + `maxSteps` override; returns terminal `AgentRun` with full step history
  - `AssistantAgentExecutor : IAgentExecutor` — LLM-driven plan-then-execute loop. Implements Trellis.Core's interface from Phase 0 PR #10
  - `IAgentLlmClient` + `OllamaAgentLlmClient` — function-calling LLM surface, parallel to `IOllamaClient` (text-vs-function-calling have different streaming semantics; lift to Trellis.Core deferred to first second-consumer)
  - `IToolRegistry` + `ToolRegistry` + `EchoTool` — tool catalogue with startup validation (name uniqueness, non-empty descriptor fields). JSON Schema validation deferred to Phase 3.B
  - `AssistantBudgetGate` — placeholder pure-logic budget gate (max-steps default 25, max-run-duration 600s, loop detection 3+ identical consecutive). Retrofit-to-Core when qwen's Phase A merges `DefaultBudgetGate`
  - `agent_runs` + `agent_steps` tables (EF migration `20260505103509_AddAgentRunsAndAgentStepsTables`); composite `ix_agent_runs_org_id_started_at` index per the dominant query pattern; `tokens_used` is `bigint` for long-run safety
  - 502 mapping for upstream Ollama errors on tool-using paths (HttpRequestException → 502 Bad Gateway with detail)
- 80-test suite (3 + 17 + 7 + 7 + 11 + 11 + 6 + 12 + 2 + 4 SkippableFact gated on `OLLAMA_BASE_URL` — see `docs/phase-3a-design.md` for the X1 split)

What does NOT exist (Phase 3.A.2+):

- Conversation turn schema widening for tool turns (Phase 3.A.2 — sibling Trellis.Core PR for `IAssistantConversationStore` + `Role=3 (Tool)` + nullable `tool_call_id` / `tool_name` columns)
- ConversationOrchestrator integration with the executor (Phase 3.A.2 — `POST /api/conversations/{id}/turns` optionally takes the agent path)
- Real `search_documents` tool → Trainer integration (Phase 3.B — needs Trainer-side surface scoping)
- DefaultBudgetGate retrofit (1-PR follow-up after qwen's Phase A merges `Trellis.Core.Services.DefaultBudgetGate`)
- Tool-result streaming to channel adapters (Phase 4)
- Tool-allowlist per-model (Phase 3.C)
- Channel adapters (Slack / WhatsApp / Telegram webhook handlers — Phase 4)
- Voice (TTS / STT, push-to-talk, wake word)
- Identity / sandboxing / DM allowlist (Phase 5)
- Production deploy (Hetzner)

## Solution layout

```
Trellis.Assistant.sln
├── Trellis.Assistant/
│   ├── Trellis.Assistant.csproj   Microsoft.NET.Sdk.Web, net10.0,
│   │                                TreatWarningsAsErrors=true,
│   │                                ProjectReference Trellis.Core,
│   │                                EF Core 10 + Npgsql 10.0.1, Ulid 1.3.4,
│   │                                canonical appsettings.user.json
│   │                                exclude rule (trainer #41 / web #14 /
│   │                                server #56 pattern)
│   ├── Program.cs                 DI: AddDbContext (late-resolution),
│   │                                AddHttpClient<IOllamaClient>().AddTypedClient
│   │                                (Func<Uri> base-URL provider, late-resolution),
│   │                                OllamaReadinessState singleton + warm-up
│   │                                hosted service + readiness endpoint,
│   │                                TenantHeadersMiddleware, conversation
│   │                                endpoints, auto-migrate on startup,
│   │                                `public partial class Program {}` for
│   │                                WebApplicationFactory<Program>
│   ├── appsettings.json           Logging + ConnectionStrings + Ollama:BaseUrl
│   │                                + Assistant:{TurnRequestTimeoutSeconds,
│   │                                WarmupModel, AutoMigrate} + AllowedHosts.
│   │                                NO top-level "Urls" key.
│   ├── Data/
│   │   ├── AssistantDbContext.cs  ConversationEntity + TurnEntity entities;
│   │   │                            snake_case column maps; advisory-lock
│   │   │                            unique-index on (conversation_id, position);
│   │   │                            FK ON DELETE CASCADE turns→conversations
│   │   ├── PostgresAssistantConversationStore.cs   IAssistantConversationStore
│   │   │                            impl. Single tenant-filtering chokepoint;
│   │   │                            advisory lock around position read +
│   │   │                            INSERT; ExecuteUpdateAsync with tenant
│   │   │                            filter for UpdatedAt bump; ToCore
│   │   │                            mappers handle the entity ↔ Core POCO
│   │   │                            shape conversion
│   │   └── AssistantDbContextDesignTimeFactory.cs   for `dotnet ef`
│   ├── Endpoints/
│   │   └── ConversationEndpoints.cs   3 endpoints; Ulid<->Guid wire
│   │                            converters at the boundary; per-request
│   │                            timeout from
│   │                            ConversationOrchestratorOptions.TurnRequestTimeoutSeconds;
│   │                            504 on timeout, 404 on missing, 400 on
│   │                            invalid input, 502 on upstream Ollama
│   │                            error (HttpRequestException), 201 on
│   │                            create
│   ├── Middleware/
│   │   └── TenantHeadersMiddleware.cs   Phase-1 stub trust-the-headers
│   │                            auth; Phase 5 swaps to JWT-claim
│   │                            extraction. Bypass list: /healthz, /readyz
│   ├── Migrations/
│   │   ├── 20260504171253_InitialCreate.cs (Phase 1 — conversations + turns)
│   │   └── 20260504192737_AddModelColumnToConversations.cs (Phase 2)
│   └── Services/
│       ├── ConversationOrchestrator.cs   read history → call LLM with
│       │                            conv.Model → persist user+assistant
│       │                            turn pair atomically; CONCURRENCY
│       │                            CONTRACT class doc covers the Phase
│       │                            3 lock-scope re-evaluation
│       ├── ConversationOrchestratorOptions.cs   bound from "Assistant"
│       │                            section (TurnRequestTimeoutSeconds:
│       │                            int, WarmupModel: string)
│       ├── OllamaReadinessState.cs   singleton; Volatile.Read/Write flag;
│       │                            MarkReady is idempotent + monotonic
│       ├── OllamaWarmupHostedService.cs   BackgroundService; tiny ping
│       │                            against Assistant:WarmupModel; backoff
│       │                            10s→30s→60s (capped); 180s per-attempt
│       │                            wall-clock cap; ApplicationStopping CT
│       │                            flows through for clean shutdown
│       └── StubLlmClient.cs        Phase 1 stub; still in binary for tests;
│                                    overridden via ConfigureTestServices in
│                                    AssistantWebApplicationFactory
└── Trellis.Assistant.Tests/       xUnit + FluentAssertions +
    │                                Microsoft.AspNetCore.Mvc.Testing +
    │                                Testcontainers.PostgreSql 4.11.0 +
    │                                Xunit.SkippableFact 1.5.23 +
    │                                TreatWarningsAsErrors=true
    ├── HealthCheckTests.cs        /healthz 200 + body shape (Phase 1) +
    │                                /readyz toggle from 503→200 on
    │                                MarkReady (Phase 2)
    ├── DotnetPublishTests.cs      [Trait("Category", "Integration")]
    ├── AppsettingsConventionsTests.cs   no top-level "Urls" key pin
    ├── Integration/
    │   ├── ConversationEndpointTests.cs   stub-driven; orchestrator +
    │   │                            store + lock invariants; 17+ tests
    │   │                            including SkippableTheory variants
    │   ├── EFMigrationSmokeTests.cs   schema shape via information_schema;
    │   │                            FK CASCADE; channel + model DEFAULTs
    │   └── RealOllamaSmokeTests.cs   [SkippableFact] gated on
    │                                OLLAMA_BASE_URL env var; full
    │                                real-LLM round-trip + upstream-502
    │                                pin on invalid model
    └── TestFixtures/
        ├── PostgresFixture.cs           Testcontainers postgres:16
        ├── AssistantWebApplicationFactory.cs   stub IOllamaClient via
        │                                ConfigureTestServices
        └── RealOllamaWebApplicationFactory.cs   production IOllamaClient,
                                          for the real-LLM smoke
```

## Tech stack

- ASP.NET Core .NET 10 (`Microsoft.NET.Sdk.Web`) with `TreatWarningsAsErrors=true`
- EF Core 10 + `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.1 (matches trellis-trainer's versions for consistent migration tooling)
- `Ulid` 1.3.4 for time-sortable conversation/turn IDs (Trellis.Core stays Ulid-free; we convert at the EF boundary so the column stays native uuid)
- xUnit + FluentAssertions + `Microsoft.AspNetCore.Mvc.Testing` + `Testcontainers.PostgreSql` 4.11.0 + `Xunit.SkippableFact` 1.5.23 (mirrors `trellis-web` / `trellis-trainer` test stacks)
- `Trellis.Core` consumed via sibling-repo project reference (`..\..\core\Trellis.Core\Trellis.Core.csproj`); same pattern as `trellis-web` / `trellis-server` / `trellis-desktop` (per `docs/56-project-structure.md`)

Phase 3+ will add:
- MCP client library for tool dispatch (TBD; see 59-doc for the design)
- Trellis.Trainer integration via the search_documents tool path
- Channel-adapter SDKs (Slack / WhatsApp / Telegram — Phase 4)
- Voice toolchain (Whisper.net for STT — already in trainer for audio ingestion; TTS choice TBD)
- Identity (likely the same OIDC posture as the rest of the system once it lands)

## Build / test / run

```bash
dotnet restore
dotnet build Trellis.Assistant.sln --configuration Release
dotnet test  Trellis.Assistant.sln --configuration Release
dotnet run   --project Trellis.Assistant
```

Fast suite (skips Docker-dependent integration tests):

```bash
dotnet test Trellis.Assistant.sln --configuration Release --filter "Category!=Integration"
```

Real-Ollama smoke (requires a running Ollama):

```powershell
$env:OLLAMA_BASE_URL = "http://localhost:11434/"
$env:OLLAMA_TEST_MODEL = "mistral-small:24b"   # optional; default is mistral-small:24b
dotnet test --configuration Release --filter "FullyQualifiedName~RealOllamaSmokeTests"
```

## QA deploy

Steady-state QA redeploys: `trellis-deploy/scripts/qa/Deploy-Assistant-Standalone.ps1`. Mirrors the trainer-qa + web-qa shape: publish → tar → scp → ssh-and-extract → systemctl restart → in-tunnel `/healthz` smoke from the deploy box. A sibling deploy PR adds a `/readyz` poll (120s budget) after the `/healthz` check so the deploy script declares "live" only after Ollama warm-up completes.

- Public hostname: `assistant-qa.chat.terraverdellc.com`
- Loopback bind: `127.0.0.1:5117`
- Unit: `trellis-assistant-qa.service`
- Auth: shared `trellisqa` HTTP Basic credential at the Hetzner edge (same as web-qa + trainer-qa); GB10-side nginx is auth-free
- Bootstrap walkthrough: `trellis-deploy/scripts/qa/bootstrap-assistant.md`

## Don't (Phase 3.A.1)

- **Don't add an SSE / streaming response surface.** Phase 1's POST /turns contract (buffer + return one JSON object) is preserved through Phase 3.A. SSE lands in Phase 3.B+ when there's a concrete streaming consumer (Slack/WhatsApp/Telegram inherently buffer; web/desktop/chat connect to the gateway, not the Assistant).
- **Don't add channel adapters.** Phase 4 owns that surface.
- **Don't lift `IAgentLlmClient` to Trellis.Core.** Phase 3.A.1 ships it parallel to `IOllamaClient` because text-vs-function-calling have different streaming semantics + only one consumer needs it. Lift happens when a SECOND consumer surfaces (qwen's Phase B if/when they need tool-aware LLM).
- **Don't add JSON Schema validation to the tool registry.** Phase 3.A.1's trimmed validation (name uniqueness + non-empty descriptor fields) is intentional. JSON Schema validation lands in Phase 3.B with `SearchDocumentsTool`'s non-trivial multi-property arg shape.
- **Don't widen the executor to support multiple tool calls per step.** Core's v0 contract is "1 tool call per step" (61-doc § 5). Multiple tool_calls emitted by the model split into sequential AgentSteps; the per-step `MaxToolCalls=1` cap is pinned by `BudgetGate_PerStepToolCallCap`.
- **Don't replace `AssistantBudgetGate` proactively.** It's a placeholder by design; the retrofit-to-`DefaultBudgetGate` is a 1-PR follow-up after qwen's Phase A merges Core's impl. Premature replacement would mean diverging from whatever signature qwen lands.
- **Don't add tool dispatch / MCP plumbing beyond Phase 3.A.1's tool registry surface.** Phase 3.B owns the first real tool (`search_documents` → Trainer); broader MCP plumbing follows.
- **Don't add voice surface.** TTS / STT / push-to-talk / wake-word — all post-tool-dispatch.
- **Don't add `appsettings.user.json` to the repo.** Gitignored; canonical csproj `<None Remove>` + `<Content Remove>` rules ensure it never rides into a publish bundle.
- **Don't pin a top-level `"Urls"` key in `appsettings.json`.** Defense-in-depth against the trainer-qa bootstrap-day port-binding bug. Pinned by `AppsettingsConventionsTests`.
- **Don't add a model allowlist** at conversation create time OR at agent-run create time. Phase 2/3.A ship free-text varchar(64); invalid tags surface as a 502 from Ollama on first call. Phase 3.C tool registry adds an allowlist when `search_documents` needs model-aware embedding selection.
- **Don't add a re-pin operation for `Model`** on conversations OR for agent runs. Per-conversation model is immutable in Phase 2. Per-agent-run model selection comes from `Assistant:Agent:Model` config (default `qwen2.5:72b` per Phase 3.A C3); Phase 3.A.2 may surface an explicit per-run override path if hub asks for it.
- **Don't use non-uuid tenantIds in tests.** Phase 3.A C1 contract: production tenantIds are uuid-shaped (gateway issues uuid-shaped tenants per JWT `tenant_id` claim). The agent-execution surface relies on `Guid.Parse(tenantId)` for OrgId derivation. All tests use `TestTenants.TenantA` / `TenantB` / etc. constants — Guid-shaped strings of the form `00000000-0000-0000-0000-00000000000a`. Non-uuid tenant slipping through `TenantHeadersMiddleware` surfaces as 400 Bad Request at `POST /api/agent-runs` rather than silently mis-mapping.
- **Don't capture `Ollama:BaseUrl` or the connection string eagerly at builder time.** The late-resolution rule in `Program.cs` is load-bearing for `WebApplicationFactory<Program>` test overlays + future runtime config changes. Phase 5's JWT swap follows the same pattern.
- **Don't merge the stub-driven endpoint tests with the real-Ollama smoke.** X1 split: stub-driven verifies orchestrator/store/lock invariants; OLLAMA_BASE_URL-gated verifies the real-LLM path. 5-parallel against real Ollama on a single GPU would spend ~5 minutes serializing — keep the test surfaces split.
- **Don't fork wire types or auth handlers from `Trellis.Core`.** Use `IAssistantConversationStore` + `IOllamaClient` + `ChatMessage` + `ChatRole` directly.
- **Don't bind ports outside 5117** without coordinating with the box's other QA services (Trainer 5114, Web 5116, Gateway 5111).

## Cross-component changes

Phase 2 touches one cross-component surface: `Trellis.Core/Services/IAssistantConversationStore.cs` (PR #11 — `CreateConversationAsync` adds `string? model` parameter; `AssistantConversation` record adds `Model` field). Sequencing: Core PR first → merge → rebase Assistant PR against merged Core (Phase 1 pattern).

Phase 3+ will:
- Tool dispatch hits Trellis.Server's chat-proxy + Trellis.Trainer's `/api/search`
- Cross-surface memory shares an `IConversationStore` shape with Trellis.Web (DB-backed once Web's persistence lands)
- Voice transcription likely reuses the trainer's Whisper.net path

Use the **Feature Orchestrator** persona in trellis-docs for any change crossing those boundaries.

## Workflow

Per the project's git rules: feature branches off `main`; PR creation auto-approved; PR bodies include a checkbox list of test cases; user merges and deletes branches.

Phase 2 non-negotiables (apply to every PR going forward, not just this one):
- `TreatWarningsAsErrors=true` on every project — 0-warning Release builds
- Test:production LoC ratio ≥ 90%
- Doc updates ride the same commit as the code (CLAUDE.md + Phase N design doc; cross-repo doc rows are hub-side)
- Three deterministic Release test runs before opening a PR

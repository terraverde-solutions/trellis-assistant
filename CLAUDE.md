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
- [`docs/phase-3b-design.md`](docs/phase-3b-design.md) — Phase 3.B design rationale (Q1 JsonSchema.Net library choice + 7 decide-and-documents, pass-through (a) wire shape, Trainer GET /api/search contract, JSON Schema validation turn-on)
- [`docs/phase-3c-design.md`](docs/phase-3c-design.md) — Phase 3.C design rationale (live agent loop: routing default per Tools=null/[]/[name], system prompt with dynamic tool catalogue, ExposeEcho exposure gate, descriptor resolution, per-tool-dispatch operator logging)
- [`docs/phase-3d-design.md`](docs/phase-3d-design.md) — Phase 3.D design rationale (ChatRecentTool: AgentToolInput.UserId threading, IServiceScopeFactory pattern for Singleton→Scoped store, ILIKE query, turn.created_at since filter, standalone-run sentinel)
- [`docs/phase-3e-design.md`](docs/phase-3e-design.md) — Phase 3.E design rationale (multi-tool dispatch per LLM turn: serial only / 1 step per tool_call / mid-iteration budget gate / synthetic BUDGET_EXHAUSTED system message / preserved Phase 3.A.2 in-loop wire encoding)
- [`docs/phase-3f-design.md`](docs/phase-3f-design.md) — Phase 3.F design rationale (per-tenant tool exposure gating: IToolExposurePolicy + AllowedTenants UUID gate + RequiredRole single-string gate; AgentToolTenancy local record; tenant_role JWT claim extraction + X-Trellis-Tenant-Role header fallback; standalone path bypasses policy; fail-closed on null role)
- [`docs/phase-3g-design.md`](docs/phase-3g-design.md) — Phase 3.G design rationale (middleware-level UUID validation: rejects non-UUID tenant_id at 401 before reaching endpoints; collapses 3× redundant `Guid.TryParse + defensive 400` branches downstream; upgrades 401 body to RFC 7807 ProblemDetails; closes the Phase 3.F untenanted-fallback bypass; pin #3 Guid-in-Items deferred to cross-repo Core widening)
- [`docs/phase-3h-design.md`](docs/phase-3h-design.md) — Phase 3.H design rationale (OpenTelemetry tool-call observability: ActivitySource + 4 custom metrics + log-scope trace correlation; OTLP HTTP exporter opt-in via `OpenTelemetry:Endpoint`; cardinality discipline — NEVER tag with user_id; budget-exhausted distinct from failure counter)
- [`docs/phase-3i-design.md`](docs/phase-3i-design.md) — Phase 3.I design rationale (cancellation-outcome OTel pin closing Phase 3.H non-blocking suggestion + WorkflowScheduleTool prototype: schedule-by-id branch only, JWT propagation via IHttpContextAccessor, LLM-readable failure envelopes — auth_failed/definition_not_found/transient/bad_request — opt-in `ExposeWorkflowSchedule` flag default false)

## What this component is

The always-on personal AI surface. Voice-first multi-channel presence. MCP-based
tool dispatch (`send_message`, `search_documents`, etc.). Cross-surface memory
across Desktop, Web, and Chat. Hosted inside Trellis Server on the customer
box (alongside Trainer + Workflow); deployable standalone for QA on GB10.

The component design lives in [`59-trellis-assistant.md`](https://github.com/terraverde-solutions/trellis-docs/blob/main/MarkdownFiles/59-trellis-assistant.md). This CLAUDE.md is the agent-side orientation: scope of the current
phase + guard-rails for what NOT to do yet.

## Status

**Phase 3.I — cancellation-outcome OTel pin + WorkflowScheduleTool prototype scaffolded** on `kimi/phase-3i-cancellation-pin-plus-workflow-schedule-tool`. Two threads bundled:

- **Thread 1 (cancellation pin):** test-only pin closing the Phase 3.H non-blocking suggestion. `ToolDispatch_CancellationToken_RecordsOutcomeCancelled` in `AgentTelemetryTests.cs` verifies `outcome=cancelled` on the dispatch counter AND `ToolDispatchFailureCount` stays empty AND Activity status is Unset. Cancellation is operator-driven, NOT a tool fault — conflating it with failure would trip failure-ratio dashboards on every user disconnect. Production-side path was already in place (Phase 3.H), what was missing was a direct wire-shape pin. Synchronization via a `HangingTool.onEntered` callback (not `CancelAfter` timer) — deterministic, avoids racing the executor's startup.

- **Thread 2 (WorkflowScheduleTool):** third production tool. LLM-callable schedule via trellis-workflow's `POST /api/workflows/runs` (qwen Phase 4.A). New surface:
  - `AgentExecution/WorkflowScheduleTool.cs` — schedule-by-id branch ONLY. Tool args `{workflow_definition_id, initial_input_json?}`. Category `AgentToolCategory.Act` (side-effecting; planner-LLM steering prefers Search/Inspect for investigation). Inline-JSON branch deliberately NOT exposed (too much surface for hallucination + injection).
  - `Services/IWorkflowClient.cs` + `HttpWorkflowClient.cs` + `WorkflowScheduleOptions.cs` — narrow client (one method: `ScheduleByIdAsync`). 10s default timeout. `BuildRequestBody` pure-function for unit-testable wire shape.
  - Structured LLM-readable failure envelopes via `WorkflowScheduleErrorCode`: `auth_failed` (401, do NOT retry), `definition_not_found` (404, ask user), `bad_request` (other 4xx, surface detail), `transient` (5xx/timeout/transport, retry-with-backoff).
  - **Auth (post fix-up):** the Assistant mints its own service token per outbound call via `IInternalTokenIssuer` (OAuth2 client-credentials grant; `aud=trellis-workflow`). User's tenant identity is forwarded out-of-band as `X-Trellis-Tenant-Id` header read from `HttpContext.User.FindFirst("tenant_id")`. The first-round shape verbatim-forwarded the inbound user JWT, which 401'd every call because the user JWT carries `aud=trellis-assistant`. The fix-up replaced that with the issuer.
  - `Services/IInternalTokenIssuer.cs` + `OpenIddictInternalTokenIssuer.cs` + `InternalTokenIssuerOptions.cs` — singleton issuer; per-resource cache + stampede-controlled refresh; refresh-skew window before `exp`. Bound from `Assistant:Auth:InternalClient` (`TokenEndpoint`, `ClientId`, `ClientSecret`, `RequestTimeoutSeconds`, `RefreshSkewSeconds`). Production overrides ClientId + ClientSecret via env var (`Assistant__Auth__InternalClient__ClientId` / `…__ClientSecret`); secrets never inlined in appsettings.json.
  - Token-issuance failures (Auth unreachable / 5xx / rejected credentials) surface to the LLM as `WorkflowScheduleErrorCode.AuthFailed` — operator-actionable, NOT a transient retry candidate.
  - `ToolCatalogueOptions.ExposeWorkflowSchedule` bool — opt-in flag, **default false** (production-safe; tool is side-effecting + untested in prod). `DefaultToolExposurePolicy` special-cases workflow_schedule against the flag; when on, falls through to PerTool gating so per-tenant rollout layers on top.
  - **Coordination note (paired qwen brief required for production functionality):** qwen's `TenantClaimsMiddleware` currently reads tenant_id ONLY from the JWT claim. The `X-Trellis-Tenant-Id` header forwarding is meaningless until qwen accepts it. Hub-drafted paired qwen brief adds header-based tenant_id support gated on the CC client_id. Until that lands, the Assistant-side fix-up is correct + green; production tenant context isn't yet plumbed at qwen's middleware.
  - 33 new pin tests (273 → 283 passing across runs): 5 OpenIddictInternalTokenIssuer + 15 HttpWorkflowClient (12 first-round + 3 new auth-shape pins + 1 BadRequest envelope on tool) + 11 WorkflowScheduleTool (9 first-round + BadRequest envelope + real-handler probe) + 1 workflow_schedule E2E + 3 exposure-gate + 1 cancellation.

**Phase 3.H — OpenTelemetry tool-call observability scaffolded** on `kimi/phase3h-otel-tool-dispatch`. Closes the operator-visibility gap left by every prior Phase 3 surface — per-tool dispatch was reachable via `journalctl` log greps but had no aggregate counter / latency / failure-ratio surface. New surface (this phase):

- `Observability/AgentTelemetry.cs` — static `Meter` + `ActivitySource` named `Trellis.Assistant.AgentExecution`. Three counters (`tool.dispatch.count`, `tool.dispatch.failure.count`, `tool.dispatch.budget_exhausted.count`) + one histogram (`tool.dispatch.duration_ms`). `Outcomes` constants holder (`success`/`failure`/`cancelled`/`budget_exhausted`).
- `Observability/OpenTelemetryOptions.cs` — bound from `OpenTelemetry:*`. `Endpoint : string?` (null = no-op exporter), `ServiceName`, `ServiceVersion`. OTLP HTTP (not gRPC) per pin #4.
- `Program.cs` — `services.AddOpenTelemetry().ConfigureResource(...).WithMetrics(...).WithTracing(...)`. Meter + ActivitySource ALWAYS register (test pins observe via `MeterListener`/`ActivityListener`); OTLP HTTP exporter conditionally registered when `Endpoint` is non-empty. ASP.NET Core + HttpClient instrumentation auto-instrument inbound `/api/*` + outbound Trainer/Ollama calls.
- `AssistantAgentExecutor.DispatchOneToolCallAsync` — opens `agent.tool.dispatch` Activity (`ActivityKind.Internal`) with tags `tool.name` + `tenant.id` + `agent.run.id` + `step.index`. Per-dispatch log scope opens with `trace_id` + `span_id` so existing `LogInformation/LogWarning` lines carry the trace correlation. Metrics emit at each terminal path (success / schema-reject / tool-not-registered / cancellation / failure). Activity status `Ok` on success, `Error` on failure (with `StatusDescription`), `Unset` on cancellation.
- `ExecuteLoopAsync` budget-exhausted mid-iteration branch — fires `ToolDispatchBudgetExhaustedCount` tagged with the SKIPPED tool's name. Distinct from failure counter per pin #1 (budget halt is operator-policy outcome, not tool fault — aggregating would mask budget-tuning signal).
- `appsettings.json` — new `OpenTelemetry` section with `Endpoint: null` default (dev/test stay in-process). QA/production overrides via `/etc/trellis-assistant-qa.env` → `OpenTelemetry__Endpoint=http://otel-collector.trellis-qa:4318`.
- Cardinality discipline (pin #3): NEVER tag metrics with `user_id` (unbounded across fleet). `tenant.id` bounded by customer count; `tool.name` bounded by registry size; `outcome` is fixed enum. `agent.run.id` + `step.index` live on activity tags only (tracing handles high cardinality; metrics never).
- 6 new SkippableFact pins under `Trellis.Assistant.Tests/Observability/AgentTelemetryTests.cs` — success/duration/failure-counter/budget-exhausted/activity-tags/activity-error-status. NEGATIVE pin asserts `user.id` is NOT in the dispatch tags.

**Phase 3.G — middleware-level UUID validation for tenantId scaffolded** on `kimi/phase3g-tenant-uuid-at-middleware`. Closes the Phase 3.F forward-flag (untenanted-fallback bypass) by moving UUID validation up to `TenantClaimsMiddleware`. New surface (this phase):

- `Middleware/TenantClaimsMiddleware` — JWT path + deprecated-header path BOTH validate `Guid.TryParse` on the tenant_id claim/header. Non-UUID → 401 reject at the boundary BEFORE the orchestrator or endpoint runs. The bespoke `{"error": "..."}` 401 body upgraded to RFC 7807 ProblemDetails (`application/problem+json`, fields `type/title/status/detail`).
- `Services/ConversationOrchestrator` — default-route `Guid.TryParse + untenanted-fallback` branch DELETED. Collapsed to single `Guid.Parse` (known-safe per middleware guarantee). `HandleAgentPathAsync`'s orgId derivation similarly collapsed; `Guid.Empty` defense preserved.
- `Endpoints/AgentRunEndpoints` — defensive `Guid.TryParse + 400-fallback` branch DELETED. Collapsed to `Guid.Parse`; the all-zeros `Guid.Empty` defense stays.
- Wire-shape change: non-UUID tenant_id now surfaces as **401 ProblemDetails** at the middleware (was: 400 from endpoint OR orchestrator throw OR silent untenanted-fallback descriptor leak). Existing 401 tests assert status code only; body upgrade back-compat-safe.
- 6 new tests: `PostConversations_NonUuidTenantId_Returns401_FromMiddleware` (4-inline theory), `PostConversations_NonUuidTenantId_ResponseShape_IsProblemDetails`, `PostConversations_NonUuidTenantId_DeprecatedHeaderPath_AlsoReturns401`. Plus 2 retrofits to existing 400-asserting tests (`PostAgentRuns_NonUuidTenantId_*` + `PostTurns_AgentPath_NonUuidTenant_*` now `_Returns401_FromMiddleware`).
- **Pin #3 (Guid in HttpContext.Items) DEFERRED** — `IAssistantConversationStore` in Trellis.Core takes `string tenantId` across every method; flipping to Guid would mean every endpoint adds a `.ToString("D")` for the store call (net more code, no real safety gain). The actual win needs cross-repo Core widening; forward-flagged.

**Phase 3.F — per-tenant tool exposure gating scaffolded** on `kimi/phase3f-per-tenant-tool-gating`. New surface (this phase):

- `AgentExecution/AgentToolTenancy.cs` — new local record `(Guid TenantId, string UserId, string? TenantRole)` passed into the exposure policy. Single-consumer rule (lives in Assistant.AgentExecution, NOT Trellis.Core); lift when Workflow/Server need the same shape.
- `AgentExecution/IToolExposurePolicy.cs` + `DefaultToolExposurePolicy.cs` — policy seam returning true/false per tool+tenancy. EchoTool special case (ExposeEcho flag, no tenant filtering); other tools check `Assistant:Tools:PerTool[name]` rule for `AllowedTenants` UUID allowlist + `RequiredRole` string. Both gates AND'd. Default exposed when no rule matches (Phase 3.F pin #1 back-compat). LogDebug on denials (gating is normal operation, not alert-worthy).
- `AgentExecution/ToolCatalogueOptions.cs` extended with `Dictionary<string, ToolExposureRule> PerTool` + the rule type. ExposeEcho preserved.
- `AgentExecution/IToolRegistry.GetExposedDescriptorsFor(AgentToolTenancy)` — new tenancy-aware getter returns descriptor list filtered by policy. Untenanted `Descriptors` property preserved unchanged for the standalone `POST /api/agent-runs` path (per Phase 3.F pin #6).
- `Middleware/TenantClaimsMiddleware` — reads `tenant_role` JWT claim + `X-Trellis-Tenant-Role` deprecated-header fallback. Stashed in `HttpContext.Items[TenantRoleKey]` as nullable string. Permissive on null — when neither source has the claim, policy fails-closed on any RequiredRole gate. Macro 2 emission status doesn't block Phase 3.F.
- `Services/ConversationOrchestrator.HandleUserTurnAsync` — new optional `tenantRole : string?` param. Default route reads `GetExposedDescriptorsFor(tenancy)`; filter route preserves Phase 3.C pre-resolution.
- `Endpoints/ConversationEndpoints` — forwards `(string?)HttpContext.Items[TenantRoleKey]` into orchestrator.
- `TestFixtures/TestAuthenticationHandler` — synthesizes `tenant_role` claim from `X-Trellis-Tenant-Role` header when present (test factory short-circuits the deprecated-header path; tests exercising role gating need the claim attached at the handler).
- 20 new tests: 13 pure-unit `DefaultToolExposurePolicyTests` + 4 `ToolRegistryTests` Phase 3.F pins (delegate-to-policy, different-tenancies-different-lists, empty-list-not-null, untenanted-bypass) + 3 Docker-gated E2E (role-gated hidden without claim, visible with matching claim, AllowedTenants tenant-mismatch hidden).

**Phase 3.E — multi-tool dispatch per LLM turn scaffolded** on `kimi/phase3e-parallel-tool-calls`. New surface (this phase):

- `AssistantAgentExecutor.ExecuteLoopAsync` — mid-iteration budget gate check added inside the foreach over `llmResponse.ToolCalls`. On halt: log `LogWarning`, append synthetic `ChatRole.System` `BUDGET_EXHAUSTED: dispatched N/M ...` message, set terminal status from `MapBudgetDecisionToRunStatus`, break foreach + while. Pre-Phase-3.E the foreach already iterated multi-call (`OllamaAgentLlmClient` parses all `tool_calls[]`) — no test ever drove it past 1 tool. Phase 3.E adds the missing gate + the test coverage.
- 6 pinned decisions: serial only (no `Task.WhenAll`); 1 step per tool_call (not per LLM call); cancellation propagates immediately; tool result append order matches `tool_calls[]` order; schema validation independent per tool_call; budget exhaust mid-iteration dispatches partial + surfaces synthetic system message.
- `PostgresAgentRunStore.ToCore(AgentRunEntity, ...)` mapper — pre-existing bug fix. Core PR #14 added `AgentRun.ErrorMessage`; entity + persist were correct but the read mapper dropped it. Surfaced by the budget-cap mid-iteration test (first consumer that inspected ErrorMessage on a budget-halt path).
- 5 new pure-unit pins in `AssistantAgentExecutorTests` (multi-tool happy, mixed success/fail, budget exhaust mid-iter, cancellation mid-iter, single-call regression-pin).
- 1 new Docker-gated E2E in `ConversationEndpointTests` — stub LLM emits `search_documents` + `chat_recent` in one turn → 4-turn persistence `[user, tool A, tool B, assistant]` → GET returns full chain in position order.
- `StubAgentLlmClient.EnqueueMultipleToolCalls(params (string, string)[])` helper for scripting multi-tool LLM responses in tests.
- In-loop tool-result wire shape preserved from Phase 3.A.2: `ChatRole.System` with `TOOL_RESULT:` envelope (Core's `ChatMessage` doesn't carry `ToolCallId`; canonical role=tool wire body would require a Core widening — deferred). Persisted tool turns DO use canonical `AssistantTurnRole.Tool` already.

**Phase 3.D — ChatRecentTool scaffolded** on `kimi/phase3d-assistant-chat-recent-tool` (pairs with trellis-core PR #17 merged at `e790669`). New surface (this phase):

- `Trellis.Assistant/AgentExecution/ChatRecentTool.cs` — second production tool (after SearchDocumentsTool). LLM can fetch the requesting user's recent conversation history via `tool_call("chat_recent", {query, limit?, since?})`. Returns turns sorted newest-first; tenant + user scope enforced via `IAssistantConversationStore`'s chokepoint.
- `Trellis.Assistant/Data/PostgresAssistantConversationStore.SearchRecentTurnsAsync` — EF JOIN turns↔conversations with `EF.Functions.ILike`, defensive `Math.Clamp(limit, 1, 50)`, UTC kind coercion on `since`.
- `AgentToolInput.UserId` threading: ConversationOrchestrator → AssistantAgentExecutor → ExecuteLoopAsync → DispatchOneToolCallAsync → AgentToolInput. Tools that don't need user scope (SearchDocumentsTool / EchoTool) ignore the field.
- `AssistantAgentExecutor.AnonymousStandaloneUserId = "standalone"` sentinel for the operator `POST /api/agent-runs` path (no end-user identity). ChatRecentTool returns empty `[]` on that sentinel without making a store call.
- DI lifetime: ChatRecentTool is Singleton; takes `IServiceScopeFactory` + creates a fresh scope per RunAsync call to resolve the scoped IAssistantConversationStore. Canonical Singleton→Scoped fix (Phase 3.B's `Func<T>` factory pattern works for Transient typed-HttpClient consumers but not for genuinely Scoped services).
- 21 new tests (12 ChatRecentTool unit + 7 Postgres-backed integration + 2 E2E in ConversationEndpointTests).
- Part E PR #10 follow-up: renamed `PostTurns_WithoutToolsField_PreservesPhase2DirectLlmPath` → `PostTurns_EmptyToolsArray_OptsOutOfAgentPath_DirectLlmPath`; comment rewritten + assertion swapped to snapshot pattern.

**Phase 3.C — live agent-loop tool usage scaffolded** on `kimi/phase3c-live-agent-loop-tool-usage`. New surface (this phase):

- Routing default change (`ConversationOrchestrator`): `Tools=null` (omitted) → agent path with the registry's full exposed catalogue; `Tools=[]` → direct-LLM opt-out ("just chat" — preserves Phase 2 per-conversation-model semantics); `Tools=["x"]` → agent path with caller's filter. Empty-resolved-catalogue (no exposed tools or all-unknowns) degrades to direct-LLM.
- `AssistantAgentExecutor.BuildSystemPromptWithCatalogue` — composes a tool-aware system prompt from `_options.SystemPrompt` + dynamic `Available tools: - <name>: <description>` enumeration. Injected at `messages[0]` for BOTH the standalone `RunAsync` path and the conversation-integrated `RunForConversationAsync` path (Phase 3.A.2 left the conversation path without a system prompt entirely; 3.C closes that gap).
- `AssistantAgentExecutor.ResolveDescriptors` — swaps caller-supplied placeholder descriptors (from `ConversationOrchestrator.BuildToolCatalogue`) or stale caller-controlled descriptors for the registry's actual descriptor before the LLM sees them. The LLM always sees the real `Description` + real `ParameterSchema`. Unknown names dropped silently.
- `ToolCatalogueOptions` + `Assistant:Tools` config section — `ExposeEcho` boolean (default `false`, production-safe). `ToolRegistry.Descriptors` filters by exposure; `GetTool` unchanged (dispatchability preserved for operator-targeted `POST /api/agent-runs` with explicit `toolNames`).
- Per-tool-dispatch operator logging in `DispatchOneToolCallAsync`: `LogInformation` on dispatch start + on success-with-duration; `LogWarning` on failure-with-message. Counter metrics deferred to Phase 3.D.
- Model-selection trade-off documented: agent path uses `Assistant:Agent:Model` (qwen2.5:72b — function-calling-capable); direct-LLM path uses conversation's pinned `Model`. A single conversation may produce some turns from each model; per-conversation tool-model selection is Phase 3.D+ ergonomics.
- 10 new Phase 3.C pins: 3 ToolRegistry exposure (pure unit), 4 executor system-prompt + descriptor-resolution (Docker-gated), 3 orchestrator routing default + opt-out + LLM-sees-search_documents (Docker-gated, hub's "refund policy" E2E example).

**Phase 3.B — search_documents tool + Trainer integration + JSON Schema validation turn-on scaffolded** on `kimi/phase3b-assistant-search-documents-tool`. New surface (this phase):

- `Trellis.Assistant/Services/{TrainerSearchOptions,SearchQuery,ISearchClient,HttpSearchClient}.cs` — loopback-trust HTTP client against trellis-trainer's existing `GET /api/search`. Pure-function `BuildSearchUri` (unit-testable, bakes `source=augmentation` for audit-log discrimination, emits repeated `documentId=` / `contentType=` for multi-value filters, null-elides optionals so Trainer defaults apply). `SearchAsync` 2xx pass-through (verbatim body bytes into `SearchClientResult.ResponseBodyJson`); 4xx → `ProblemDetails.Detail` extraction with `title` fallback; 5xx / transport / timeout / cancellation mapped to structured `ErrorMessage`. 30s default timeout configurable via `Assistant:Trainer:RequestTimeoutSeconds`.
- `Trellis.Assistant/AgentExecution/{IJsonSchemaValidator,JsonSchemaNetValidator}.cs` — JSON Schema validator (JsonSchema.Net 9.2.0, Draft 2020-12) wrap with `ConcurrentDictionary` parsed-schema cache + normalized first-error reporting (`"<instance-path>: <message>"`). Two responsibilities: `EnsureValidSchema` (startup descriptor check) + `Validate` (runtime args check).
- `Trellis.Assistant/AgentExecution/SearchDocumentsTool.cs` — Phase 3.B's first real tool. Descriptor with full Draft 2020-12 schema mapping to Assistant-canonical snake_case (`query`, `top_k`, `mode`, `since`, `filter.document_ids`, `filter.content_types`); `MapToSearchQuery` boundary translation to the Trainer wire-aligned `SearchQuery`; pass-through (a) wire shape — Trainer's PascalCase response fields flow through to the LLM verbatim.
- `Trellis.Assistant/AgentExecution/ToolRegistry.cs` — ctor now takes `IJsonSchemaValidator`; calls `EnsureValidSchema` per registered tool at startup; malformed schema → `InvalidOperationException` with tool-identifying context (operators see which tool was misconfigured in startup log).
- `Trellis.Assistant/AgentExecution/AssistantAgentExecutor.cs` — ctor now takes `IJsonSchemaValidator`; `DispatchOneToolCallAsync` runtime gate before `tool.RunAsync` — invalid args persist as `AgentStep.Failed` with `ErrorMessage="Tool '<name>': schema validation failed: <path> <reason>"` + skip dispatch. REJECT semantics per decide-and-document #4 — LLM sees the failure in history + retries with corrected args.
- `appsettings.json` adds `Assistant:Trainer:{BaseUrl="http://127.0.0.1:5114/", RequestTimeoutSeconds=30}`.
- 169-test suite (43 new + 126 preserved): `HttpSearchClientTests` (18), `JsonSchemaNetValidatorTests` (9), `SearchDocumentsToolTests` (11 + 3 theory cases), `ToolRegistryTests` (+2 pins), `AssistantAgentExecutorTests` (+2 schema-gate pins).

**Phase 3.A.2 — conversation-integrated agent path landed.** What exists:

- ASP.NET Core net10.0 minimal-API project (`Microsoft.NET.Sdk.Web`)
- Three conversation endpoints under `/api/conversations`:
  - `POST /api/conversations` — create with optional channel + model
  - `GET /api/conversations/{id}` — read conversation + all turns
  - `POST /api/conversations/{id}/turns` — append user turn + stream LLM reply, persist atomically
- Two operational endpoints:
  - `/healthz` — liveness (200 once Kestrel listens)
  - `/readyz` — readiness (200 once `OllamaWarmupHostedService` has succeeded; 503 until then)
- **JWT bearer authentication (Macro 3 PR 2)** — `AddJwtBearer` validates incoming bearers against `Auth:Authority` + `Auth:Audience` (defaults: `trellis-assistant`); `RequireAuthorization` on `/api/conversations/*` + `/api/agent-runs/*`. `/healthz` + `/readyz` stay anonymous (operator probes). `TenantClaimsMiddleware` (renamed from `TenantClaimsMiddleware`) reads `tenant_id` (custom claim) + `sub` (canonical user id) from `HttpContext.User`; falls back to deprecated `X-Trellis-Tenant-Id`/`X-Trellis-User-Id` headers with structured warning + Prometheus counter telemetry per request, so operators can identify legacy callers before the eventual removal PR. Synthesizes a `ClaimsPrincipal` on the header-fallback path so `RequireAuthorization` accepts either auth source.
- Postgres-backed `IAssistantConversationStore` impl with per-conversation advisory lock around position assignment + INSERT pair
- Real `Trellis.Core.Services.OllamaClient` wired via `IHttpClientFactory` (Phase 1's stub still in the binary for tests)
- Per-conversation model selection: `conversations.model varchar(64) NOT NULL DEFAULT 'mistral-small:24b'`; pinned at create time, immutable for Phase 2
- **Phase 3.A.1 surface (this layer):**
  - `POST /api/agent-runs` — standalone agentic surface; caller supplies `userPrompt` + (optionally) `toolNames` filter + `maxSteps` override; returns terminal `AgentRun` with full step history
  - `AssistantAgentExecutor : IAgentExecutor` — LLM-driven plan-then-execute loop. Implements Trellis.Core's interface from Phase 0 PR #10
  - `IAgentLlmClient` + `OllamaAgentLlmClient` — function-calling LLM surface, parallel to `IOllamaClient` (text-vs-function-calling have different streaming semantics; lift to Trellis.Core deferred to first second-consumer)
  - `IToolRegistry` + `ToolRegistry` + `EchoTool` — tool catalogue with startup validation (name uniqueness, non-empty descriptor fields). JSON Schema validation deferred to Phase 3.B
  - **Budget gate**: consumes `Trellis.Core.Services.DefaultBudgetGate` per qwen Phase A merge (post-Phase-3.A.2 retrofit). `AssistantAgentExecutor.WithAssistantDefaults` injects `MaxSteps=25` (Phase 0 PR #10's documented Assistant default) when callers pass `BudgetOverrides=null` — Core's gate ships with `MaxSteps=1000` (Workflow's value), and Core's docstring on `AgentBudgetOverrides` directs callers to inject per-surface defaults. The Assistant executor IS that consumer for all Assistant paths.
  - `agent_runs` + `agent_steps` tables (EF migration `20260505103509_AddAgentRunsAndAgentStepsTables`); composite `ix_agent_runs_org_id_started_at` index per the dominant query pattern; `tokens_used` is `bigint` for long-run safety
  - 502 mapping for upstream Ollama errors on tool-using paths (HttpRequestException → 502 Bad Gateway with detail)
- **Phase 3.A.2 surface (this layer):**
  - `POST /api/conversations/{id}/turns` accepts optional `Tools: string[]?` field. Non-null + non-empty routes through `AssistantAgentExecutor.RunForConversationAsync`; tool turns persist as `Role=Tool` rows in `turns` inline with user/assistant turns; final assistant text persists as the last turn. All persisted atomically as one batch via `AppendTurnsAsync`.
  - `GET /api/conversations/{id}` returns the full chain inline in the `Turns` array per Option A wire shape (Phase 3.A.2 ratification): `[user, ...tool, assistant]` in position order.
  - `AppendTurnResponse` gains optional `ToolTurns` field — null on Phase 2 direct-LLM path, populated when the agent path ran.
  - `TurnDto` gains nullable `ToolCallId` + `ToolName` — both null on user/assistant/system turns; populated on Tool turns.
  - EF migration `AddToolCallIdAndToolNameToTurns` — strict-additive nullable varchar(64). Existing user/assistant rows persist as NULL.
  - `AssistantAgentExecutor.RunForConversationAsync` — new public method on the concrete class (NOT on `IAgentExecutor`) that orchestrators use to drive the loop with pre-built messages + return `ConversationAgentResult` carrying final text + per-dispatch summaries.
  - `IAssistantConversationStore.AppendTurnsAsync` consumes the widened `NewAssistantTurn` shape from Trellis.Core PR #13 (Tool=3 enum + nullable `ToolCallId`/`ToolName` fields).
  - **Tool turns flow through to the LLM as canonical `ChatRole.Tool`** (post-3.A.2-bridge per Trellis.Core PR #15). The `ConversationOrchestrator.ToCoreRole` mapping flipped from `AssistantTurnRole.Tool → ChatRole.System` (workaround) to `→ ChatRole.Tool` (canonical) — model's tool-trained head sees the proper role label when reconstructing history. Resolves Phase 3.A.2 architectural divergence #4. Pinned by `ToCoreRole_ToolTurn_MapsToChatRoleTool`.
- Phase 3.A.1's standalone `POST /api/agent-runs` remains — both endpoints coexist (standalone is one-shot agentic without conversation context; conversation-integrated threads tool history).
- 105-test suite — see test breakdown in `docs/phase-3a-design.md` § Phase 3.A.2.

What does NOT exist (Phase 3.C+):

- Tool-result streaming to channel adapters (Phase 4)
- Tool-allowlist per-model (Phase 3.C)
- Per-chunk `source_type` / `project_id` filtering (sibling ingestion-side asks for future macros; Trainer has no per-chunk schema for these in v0)
- S2S JWT auth between Assistant + Trainer (loopback-trust v0; future macro)
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
│   │                                TenantClaimsMiddleware, conversation
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
│   │   └── TenantClaimsMiddleware.cs   Macro 3 PR 2: JWT-claim
│   │                            extraction (canonical) + deprecated
│   │                            header fallback (telemetry-tracked).
│   │                            Bypass list: /healthz, /readyz
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

## Don't (Phase 3.I)

- **Don't verbatim-forward the inbound user JWT to a sibling service.** Phase 3.I fix-up: the user JWT carries `aud=trellis-assistant`; siblings validate against their own audience (e.g. `aud=trellis-workflow`). Verbatim-forwarding 401s every call. Use `IInternalTokenIssuer.GetAccessTokenAsync(targetResource, ct)` to mint a per-target token + forward tenant identity out-of-band as `X-Trellis-Tenant-Id`. Pinned by `ScheduleByIdAsync_201Created_OutboundCarriesServiceTokenAndTenantHeader` + the issuer's pin tests.
- **Don't inline `Assistant:Auth:InternalClient:ClientSecret` in appsettings.json (committed config).** Production loads it via env var (`Assistant__Auth__InternalClient__ClientSecret`). The default-value-empty pattern + `[Required]` validation crashes startup if a deploy misconfigures.
- **Don't expose the inline-JSON schedule branch of `POST /api/workflows/runs`.** Phase 3.I pin: schedule-by-id only. The inline `WorkflowDefinitionJson` blob is a full workflow DAG; an LLM hallucinating one creates runs that fail or worse, execute unintended steps. If a future need surfaces with a constrained sub-shape, that's a separate brief.
- **Don't default `ExposeWorkflowSchedule=true`.** Production-safe per Phase 3.I brief — the tool is side-effecting (real workflow runs) + untested in prod. QA validates before flipping the default. Once on, PerTool's `AllowedTenants` + `RequiredRole` gate layers on top for per-tenant rollout.
- **Don't reshape qwen's 201 body inside `WorkflowScheduleTool`.** Pass-through (a) per Phase 3.B precedent — the LLM sees qwen-canonical PascalCase fields directly. If qwen renames a field, re-bake the tool descriptor's example rather than introducing a translation layer.
- **Don't move `HttpWorkflowClient`'s JWT propagation into a shared `DelegatingHandler`.** Single-consumer rule (same posture Phase 3.B + 3.D + 3.F + 3.H took for their seams). Lift when a second tool needs the same inbound-JWT-propagation shape.
- **Don't tag the cancellation outcome as a failure.** Phase 3.I Thread 1 pin discipline: `outcome=cancelled` is distinct from `outcome=failure` AND `ToolDispatchFailureCount` doesn't increment on cancellation. Operator-driven cancellation (caller dropped the token) isn't a tool fault; conflating would trip failure-ratio dashboards on user disconnects. Pinned by `ToolDispatch_CancellationToken_RecordsOutcomeCancelled`.
- **Don't add scheduling-futures (cron, delayed) to `WorkflowScheduleTool`.** Hangfire upstream already handles delayed runs; the tool is fire-now. If a future need surfaces, surface a brief.
- **Don't cache `WorkflowDefinition` metadata Assistant-side.** Out of scope per Phase 3.I brief. If a future tool needs to inspect or validate definitions ahead of scheduling, that's a separate tool (`workflow_describe` or similar) with its own brief.
- **Don't reuse trellis-web's HttpWorkflowClient code.** Phase 3.I brief: copy the *pattern* (structured failure mapping + 4xx ProblemDetails + 5xx warning + timeout-distinct-from-cancellation), NOT the code. Trellis-web's variant has UI-side concerns (user-facing error rendering); this client's failure mapping is LLM-readable + operator-actionable.

## Don't (Phase 3.H)

- **Don't tag any metric with `user_id`.** Phase 3.H pin #3 — `user_id` is unbounded across the fleet's users; would explode cardinality at the collector. `tenant.id` is bounded (~10s–100s); `tool.name` is bounded (~3 in v0); `outcome` is fixed enum. Pinned by `ToolDispatch_Success_EmitsDispatchCount_WithExpectedTags`'s negative assertion. Adding `user_id` to any future tool-dispatch metric would silently break that contract — the test catches it at green-build time.
- **Don't switch the OTLP exporter from HTTP to gRPC.** Phase 3.H pin #4 — QA collector firewall posture is built around ports-80/443 (trellis-deploy network policy). gRPC requires port 4317 open + a less-standard network policy. The collector itself re-exports to anywhere; the Assistant's choice is just the wire protocol.
- **Don't pre-bucket the duration histogram.** Phase 3.H pin #2 — collector-derived p50/p95/p99 lets downstream tuning happen without a code change + redeploy. Pre-bucketing here would hardcode percentile boundaries that may not match Phase 4 SLO targets.
- **Don't add an `agent.run.id` or `step.index` metric tag.** High-cardinality (one per agent run / per dispatch step). Lives on **activity tags only** — tracing systems handle high cardinality; metrics never tag with this. The dispatch metric's tag set is locked at `{tool.name, tenant.id, outcome}`.
- **Don't aggregate budget-exhausted into the failure counter.** Phase 3.H pin #1 — budget halt is operator-policy outcome (the LLM emitted MORE tool_calls than the budget allowed), not a tool fault. Aggregating both as failures would mask budget-tuning signal. Pinned by `ToolDispatch_BudgetExhaustedMidIteration_EmitsBudgetExhaustedCounter_NotFailureCounter` (failure counter empty on the budget-halt path).
- **Don't gate the Meter / ActivitySource registration on `hasOtelEndpoint`.** They must ALWAYS register so in-process test pins observe local events via `MeterListener`/`ActivityListener`. Only the OTLP exporter is gated. Putting the `AddMeter` / `AddSource` calls inside the `if (hasOtelEndpoint)` block would silently break the 6 test pins (no listeners → no captures → all assertions fail).
- **Don't move `AgentTelemetry` to Trellis.Core.** Single-consumer rule (same posture Phase 3.A + 3.B + 3.D + 3.F took for their seams). Lift when Workflow or Server need to emit `tool.dispatch.*` metrics against their own tool catalogues.
- **Don't add a dedicated pure-unit `AgentTelemetryUnitTests` class.** The 6 SkippableFact pins under PostgresFixture cover the contract end-to-end. A pure-unit class would re-test the Meter/ActivitySource registration without the agent-path's invocation context — net coverage cost not earned.

## Don't (Phase 3.G)

- **Don't accept non-UUID tenant_id at any downstream layer.** Phase 3.G middleware validation is THE gate; downstream code uses `Guid.Parse` (not `TryParse`) and trusts it. Adding a defensive `TryParse + fallback` re-introduces the dead-code path the cleanup just deleted.
- **Don't try to flip `HttpContext.Items[TenantClaimsMiddleware.TenantIdKey]` to `Guid` in Phase 3.G.** Pin #3 was deferred because `IAssistantConversationStore` in Trellis.Core takes `string tenantId` across every method (cross-repo contract). Flipping HttpContext.Items to Guid means every endpoint does `((Guid)items).ToString("D")` before calling the store — more code, no real safety win. The real fix requires a Core widening; revisit when the cross-repo PR lands.
- **Don't change the deprecated-header-path UUID validation to skip-on-non-UUID.** Pin #5: legacy callers get the same gate. If a legacy caller emits a non-UUID tenant_id, that's a misconfiguration the operator needs to see (401, not silent acceptance).
- **Don't downgrade 401 ProblemDetails back to bespoke JSON.** Macro 3's HTTP-error contract uses RFC 7807; downstream Trellis services parse `type/title/status/detail` consistently. Operators relying on the standard shape would have to special-case the Assistant if it diverged.
- **Don't add a dedicated `TenantClaimsMiddlewareTests` class.** Integration-test coverage via `ConversationEndpointTests` is sufficient for the middleware's request-shape contract. A dedicated middleware test class would require a new fixture pattern (synthetic HttpContext setup); cost not earned at the current coverage gap.

## Don't (Phase 3.F)

- **Don't apply `IToolExposurePolicy` on the untenanted `Descriptors` property.** Pin #6: standalone `POST /api/agent-runs` is operator-facing; bypasses the policy intentionally. If a future operator-facing endpoint wants per-operator gating, that's a separate brief — `Descriptors` stays untenanted.
- **Don't tenancy-gate `GetTool(name)` (dispatch).** v0 relies on catalogue-only gating — the LLM can't emit tool_calls for tools it never saw. Dispatch-time tenancy check is defense-in-depth Phase 3.G+ when a real bypass surfaces.
- **Don't move `AgentToolTenancy` to Trellis.Core for Phase 3.F.** Single-consumer rule (Phase 3.A precedent): the only consumer today is `IToolExposurePolicy`. Lift when a second component (Workflow / Server) needs the same shape.
- **Don't broaden `RequiredRole` to a list of roles in Phase 3.F.** Pin #4: single-string ordinal compare matches Macro 2's single-role-per-user model. Multi-role-per-user is a config-shape change + a Macro 2 dependency; revisit when the upstream model supports it.
- **Don't pass `tenantRole` as required.** Permissive on null is the safety posture: fail-closed on null role + non-null RequiredRole hides the tool. Requiring the role at the boundary would 401 every JWT that didn't carry the claim, breaking Macro 2 callers that don't emit it.

## Don't (Phase 3.E)

- **Don't add `Task.WhenAll` parallel dispatch.** Phase 3.E pin #1 ratified serial-only. Database connection pool + Trainer loopback aren't optimized for high concurrency; test determinism is the larger win. If a real latency case surfaces, that's a Phase 3.F brief — measure first.
- **Don't short-circuit the foreach on the first tool failure.** Phase 3.E pin #5: schema validation + execution failures are independent per tool_call. The LLM gets ALL results (Success=true or Success=false per step) and decides what to do. Pinned by `MultiToolCall_FirstSucceedsSecondFails_BothResultsAppended_LoopContinues`.
- **Don't move the budget gate check above the first tool dispatch within a foreach iteration.** The top-of-while gate ALREADY passed when the LLM was called; the first dispatch is a contract of the LLM response. Mid-iteration gate fires from the SECOND iteration onward (`dispatchedToolCalls > 0`).
- **Don't introduce a synthetic system message that re-feeds the LLM after BUDGET_EXHAUSTED.** Pin #6 surfaces the message into the in-flight `messages` list but the loop terminates immediately — the message doesn't reach a follow-up LLM call. Operator visibility is via `LogWarning` + persisted `AgentRun.Status = CapReached` + populated `AgentRun.ErrorMessage`. If a future use case wants "soft halt → let LLM produce a final synthesis", that's a separate routing brief.
- **Don't try to encode `tool_call_id` in the in-loop `messages` list.** Core's `ChatMessage` carries only `(Role, Content)` — full OAI-compat role=tool+tool_call_id wire shape requires a Core widening. The in-loop encoding stays `ChatRole.System` with `TOOL_RESULT:` envelope (Phase 3.A.2 design preserved). Persisted tool turns DO use canonical `AssistantTurnRole.Tool` with `ToolCallId` populated.

## Don't (Phase 3.D)

- **Don't capture a scoped service in a Singleton tool directly.** Phase 3.D's `IServiceScopeFactory` pattern in ChatRecentTool is the canonical fix. Phase 3.B's `Func<T>` factory works for Transient consumers (typed-HttpClient) but NOT for genuinely Scoped services like `IAssistantConversationStore` — ASP.NET Core's scope validation blocks Singleton-from-Scoped resolution at runtime.
- **Don't add `chat_recent` results outside the (tenant, user) scope.** The JOIN-based filter at `PostgresAssistantConversationStore.SearchRecentTurnsAsync` is the single chokepoint; bypassing it via direct EF queries (rather than the interface method) would lose the protection. Pinned by `PostgresAssistantConversationStoreSearchTests.SearchRecentTurnsAsync_TenantAndUserScope_FiltersOutOtherTenantsAndUsers` + the E2E `ChatRecent_TenantScoping_TenantBCannotSeeTenantATurns`.
- **Don't run chat_recent against `AgentToolInput.UserId == AnonymousStandaloneUserId`.** The sentinel signals "no end-user identity" (operator-facing standalone path); querying with the sentinel as the userId would either match no rows OR (worse, hypothetically) match a tenant that registered a literal user named "standalone." ChatRecentTool short-circuits to empty `[]` at the tool layer; this contract is pinned.
- **Don't read `IAssistantConversationStore` from `ChatRecentTool` ctor directly.** The store is Scoped; the tool is Singleton. Use `IServiceScopeFactory.CreateScope()` per RunAsync. The Phase 3.D class doc on ChatRecentTool spells out why.
- **Don't add a trigram or tsvector index on `turns.content` for Phase 3.D.** v0 uses sequential-scan ILIKE; small per-user inboxes don't need it. If real-world query latency surfaces, that's a Phase 3.E+ migration brief.

## Don't (Phase 3.C)

- **Don't add per-tool exposure flags to `IAgentTool` itself.** Phase 3.C's `Assistant:Tools:ExposeEcho` config flag is the per-environment-override pattern matching `Assistant:AutoMigrate` / `Auth:RequireHttpsMetadata`. If 3.D+ adds 2+ more test-only tools and the config forest becomes a smell, the lift to `bool IAgentTool.IsExposedByDefault` is a follow-up brief — don't pre-empt it.
- **Don't bypass `ResolveDescriptors` and pass caller-controlled descriptors straight to `IAgentLlmClient`.** The LLM must see the registry's real `Description` + real `ParameterSchema`, not whatever the caller (orchestrator placeholder, future channel adapter, etc.) supplied. The resolution boundary is load-bearing for schema validation gate + the system prompt's catalogue enumeration.
- **Don't run the agent path when the resolved catalogue is empty.** The orchestrator degrades to direct-LLM in that case — running the executor with `tools.Count == 0` would spend budget-gate + agent_runs overhead on a call functionally equivalent to direct-LLM and would silently switch the model (conversation pinned Model → `Assistant:Agent:Model`).
- **Don't inject the system prompt at the orchestrator layer.** The executor's `BuildSystemPromptWithCatalogue` reads the registry; injecting at the orchestrator would either duplicate that logic OR force the orchestrator to pre-resolve descriptors before the executor's `ResolveDescriptors` runs. messages[0] is reserved for the executor's injection.
- **Don't add per-tool Prometheus counters in this PR.** Phase 3.C ships per-tool LogInformation/LogWarning lifecycle logs (journalctl-grep surface). Counter metrics are Phase 3.D scope per hub's ratify — don't pre-empt.

## Don't (Phase 3.B)

- **Don't add an SSE / streaming response surface.** Phase 1's POST /turns contract (buffer + return one JSON object) is preserved. SSE lands when there's a concrete streaming consumer (Slack/WhatsApp/Telegram inherently buffer; web/desktop/chat connect to the gateway, not the Assistant).
- **Don't add channel adapters.** Phase 4 owns that surface.
- **Don't lift `IAgentLlmClient` to Trellis.Core.** Phase 3.A.1 ships it parallel to `IOllamaClient` because text-vs-function-calling have different streaming semantics + only one consumer needs it. Lift happens when a SECOND consumer surfaces (qwen's Phase B if/when they need tool-aware LLM).
- **Don't lift `IJsonSchemaValidator` to Trellis.Core.** Phase 3.B keeps it Assistant-side because Trellis.Core stays dependency-light per `core/CLAUDE.md`'s "no platform deps" guideline; adding JsonSchema.Net to Core would force every Core consumer to take that dep. If a second consumer surfaces (qwen's Workflow turning on schema validation, Server proxying tool calls), lift then.
- **Don't reshape Trainer's response inside `SearchDocumentsTool` (pass-through (a) is canonical).** The 2xx body bytes flow verbatim into `AgentToolOutput.ResultJson`. The LLM sees Trainer-canonical PascalCase fields. If Trainer ever renames a field, we re-bake the tool descriptor's example response in the prompt rather than introducing a translation layer here.
- **Don't add tenant headers / JWT to the `HttpSearchClient`.** Loopback-trust v0 — Trainer binds `127.0.0.1:5114` only, kernel filter is the trust boundary. S2S JWT is a future macro when cross-host deployment surfaces.
- **Don't widen the executor to support multiple tool calls per step.** Core's v0 contract is "1 tool call per step" (61-doc § 5). Multiple tool_calls emitted by the model split into sequential AgentSteps; the per-step `MaxToolCalls=1` cap is pinned by `BudgetGate_PerStepToolCallCap`.
- **Don't reintroduce a custom `AssistantBudgetGate`.** Post-Phase-3.A.2 retrofit consumed `Trellis.Core.Services.DefaultBudgetGate`. Assistant's `MaxSteps=25` default is injected by `AssistantAgentExecutor.WithAssistantDefaults` (per Core's docstring contract: callers inject per-surface defaults). If a future Assistant-specific gate variant becomes useful, surface a brief — don't fork.
- **Don't add tool dispatch / MCP plumbing beyond Phase 3.B's `search_documents` + `EchoTool` surface.** Phase 3.B ships the first real tool; broader MCP plumbing follows. New tools follow the same shape: implement `IAgentTool`, register `services.AddSingleton<IAgentTool, MyTool>()`, the registry picks up + validates the schema at startup.
- **Don't introduce per-chunk filtering for `source_type` or `project_id`.** Trainer has no schema for these in v0 (verified against the trellis-trainer codebase 2026-05-11). Adding them is an ingestion-side schema-change ask, not a search-client ask. If a tool wants this filtering, surface a sibling ingestion brief rather than smuggling it into the Assistant tool descriptor.
- **Don't add voice surface.** TTS / STT / push-to-talk / wake-word — all post-tool-dispatch.
- **Don't add `appsettings.user.json` to the repo.** Gitignored; canonical csproj `<None Remove>` + `<Content Remove>` rules ensure it never rides into a publish bundle.
- **Don't pin a top-level `"Urls"` key in `appsettings.json`.** Defense-in-depth against the trainer-qa bootstrap-day port-binding bug. Pinned by `AppsettingsConventionsTests`.
- **Don't add a model allowlist** at conversation create time OR at agent-run create time. Phase 2/3.A ship free-text varchar(64); invalid tags surface as a 502 from Ollama on first call. Phase 3.C tool registry adds an allowlist when `search_documents` needs model-aware embedding selection.
- **Don't add a re-pin operation for `Model`** on conversations OR for agent runs. Per-conversation model is immutable in Phase 2. Per-agent-run model selection comes from `Assistant:Agent:Model` config (default `qwen2.5:72b` per Phase 3.A C3); Phase 3.A.2 may surface an explicit per-run override path if hub asks for it.
- **Don't use non-uuid tenantIds in tests.** Phase 3.A C1 contract: production tenantIds are uuid-shaped (gateway issues uuid-shaped tenants per JWT `tenant_id` claim). The agent-execution surface relies on `Guid.Parse(tenantId)` for OrgId derivation. All tests use `TestTenants.TenantA` / `TenantB` / etc. constants — Guid-shaped strings of the form `00000000-0000-0000-0000-00000000000a`. Non-uuid tenant slipping through `TenantClaimsMiddleware` surfaces as 400 Bad Request at `POST /api/agent-runs` rather than silently mis-mapping.
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

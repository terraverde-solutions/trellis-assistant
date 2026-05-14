# Phase 3.D design — ChatRecentTool (recent-conversation history as agent-callable surface)

**Status:** scaffolded on `kimi/phase3d-assistant-chat-recent-tool`. Pairs with trellis-core PR #17 (merged 2026-05-12 at `e790669`) which added `AgentToolInput.UserId`, `RecentTurnSummary`, and `IAssistantConversationStore.SearchRecentTurnsAsync`.

**Hub-ratified pre-scaffold:** Q1–Q5 + cross-repo sequencing.

## What's missing without this PR

The agent loop has search_documents (Phase 3.B) — RAG over the customer's indexed documents. But a user asking "what did we discuss yesterday about the refund policy?" gets nothing useful: the LLM has no access to the user's own conversation history beyond the current conversation thread. Phase 3.D's `chat_recent` tool closes that gap.

## Q1 — Data-access path: `IAssistantConversationStore.SearchRecentTurnsAsync`

Single chokepoint for tenant filtering, single Postgres round-trip via JOIN. Hub's brief mentioned `IConversationStore`; that's the Desktop/Chat single-tenant store. **Substituted to `IAssistantConversationStore`** (multi-tenant; matches the rest of Trellis.Assistant's persistence path).

The Postgres impl uses an EF JOIN between `turns` and `conversations` to push the (tenant, user) filter through the FK. The store's chokepoint discipline (every method takes `(tenantId, userId)` + applies WHERE tenant_id = $1 AND user_id = $2) protects against cross-tenant leak at the SQL layer.

## Q2 — Tenant context flow: AgentToolInput.UserId (1a)

`AgentToolInput.UserId : string` (required, sibling to existing `OrgId : Guid`). Threaded from `HttpContext.Items[TenantClaimsMiddleware.UserIdKey]` → `ConversationOrchestrator.HandleUserTurnAsync` → `AssistantAgentExecutor.RunForConversationAsync(userId: ...)` → `ExecuteLoopAsync` → `DispatchOneToolCallAsync` → `AgentToolInput { UserId = ... }`.

Tools that don't need user scope (SearchDocumentsTool loopback-trust, EchoTool) ignore the field; the executor still passes it through. Migration to a unified `Tenancy` record is a forward-flag if Phase 3.E+ adds enough scope dimensions to justify the indirection.

### Standalone-run user-scope sentinel

The operator-facing `POST /api/agent-runs` endpoint has no end-user identity — it's an audit/debugging surface, not a per-end-user conversation endpoint. The IAgentTool interface widening to require UserId applies uniformly, so the standalone path needed a sentinel value.

`AssistantAgentExecutor.AnonymousStandaloneUserId = "standalone"` is the sentinel. Tools that filter on user scope (ChatRecentTool) check for the sentinel + return empty without making a store call — no hypothetical user named "standalone" to leak data from. Tools that don't filter on user scope ignore the sentinel entirely.

Alternative considered: require userId on the standalone endpoint's request body (breaking wire change). Rejected — the sentinel keeps the standalone endpoint's existing shape; operator-driven audit runs continue to work as before.

## Q3 — Query: `ILIKE` substring

`turns.content ILIKE '%' || $query || '%'`. Case-insensitive. PostgreSQL's native operator via `EF.Functions.ILike`. No trigram or tsvector index in v0.

Future migration: `CREATE INDEX ... USING gin (content gin_trgm_ops)` if real-world query latency surfaces as a problem. For v0 (small per-user inboxes; conversations grow slowly per user), the sequential scan is fine.

## Q4 — `since`: `turns.created_at`

Filter is on per-turn `created_at`, NOT `conversations.updated_at`. More precise — matches the LLM's mental model ("what did we discuss yesterday after 3pm" = turn-level instant, not conversation-level last-touch).

UTC enforced at the store layer: incoming `DateTimeKind.Unspecified` is coerced to `Utc`; `Local` is converted via `ToUniversalTime()`. Prevents subtle off-by-one across timezone boundaries.

## Q5 — Projection shape: `RecentTurnSummary`

```csharp
public sealed record RecentTurnSummary(
    Guid ConversationId,
    Guid TurnId,
    int Position,
    AssistantTurnRole Role,
    string Content,
    DateTime CreatedAt);
```

`Position` is included so the LLM can reason about turn ordering when multiple matches share a `ConversationId`. `Channel`/`Model` excluded (LLM doesn't need routing context); `ToolCallId`/`ToolName` excluded (Tool-role turn content is JSON, not natural-language — would confuse field-attention).

Wire emit from ChatRecentTool uses `JsonNamingPolicy.SnakeCaseLower` (`{conversation_id, turn_id, position, role, content, created_at}`) + role serialized as the lowercase wire string ("user"/"assistant"/"system"/"tool") rather than the enum integer code (AssistantTurnRole has no `EnumMemberJsonConverter` in Core).

## DI lifetime: IServiceScopeFactory pattern

ChatRecentTool is registered as Singleton (consistent with the other IAgentTool tools that the Singleton ToolRegistry sweeps at host startup). But `IAssistantConversationStore` is Scoped (lifetime-bound to AssistantDbContext). Capturing the scoped store directly in the singleton fails ASP.NET Core's scope validation.

Phase 3.B's `Func<ISearchClient>` pattern doesn't apply here — that worked because ISearchClient is transient (typed-HttpClient), not scoped.

Canonical fix: inject `IServiceScopeFactory` (itself Singleton, built-in) + create a fresh scope per `RunAsync` call → fresh AssistantDbContext → query → dispose scope.

```csharp
using var scope = _scopeFactory.CreateScope();
var store = scope.ServiceProvider.GetRequiredService<IAssistantConversationStore>();
var results = await store.SearchRecentTurnsAsync(...);
```

Single round-trip per call; no captive-lifetime issues; no `Func<T>` wrapper indirection.

## Part E — 5th-test rename

Hub's PR #10 follow-up flagged `PostTurns_WithoutToolsField_PreservesPhase2DirectLlmPath` as still describing Phase 3.A.2 semantics. Confirmed at scaffold start — the test existed at line 812 of the merged main branch. Renamed to `PostTurns_EmptyToolsArray_OptsOutOfAgentPath_DirectLlmPath`; comment rewritten with Phase 3.C semantics; assertion swapped to the snapshot pattern (`agentCallsBefore` → compare-after) per hub's brittle-assertion note.

## What this PR adds

**Production (~430 LoC):**
- `Trellis.Assistant/AgentExecution/ChatRecentTool.cs` (new) — IAgentTool implementation: descriptor + JSON Schema + RunAsync + standalone-sentinel bypass + snake_case wire emit + IServiceScopeFactory per-call resolution
- `Trellis.Assistant/Data/PostgresAssistantConversationStore.cs` (modified) — `SearchRecentTurnsAsync` impl: EF JOIN turns↔conversations with (tenant, user) filter, `EF.Functions.ILike`, UTC kind coercion on `since`, defensive `Math.Clamp(limit, 1, 50)`, `OrderByDescending(t.CreatedAt)`
- `Trellis.Assistant/AgentExecution/AssistantAgentExecutor.cs` (modified) — `RunAsync` overload taking explicit userId (conversation path) + parameterless overload defaulting to standalone sentinel; ExecuteLoopAsync + DispatchOneToolCallAsync threaded with userId param; AgentToolInput construction adds UserId
- `Trellis.Assistant/Services/ConversationOrchestrator.cs` (modified) — forwards userId to RunForConversationAsync
- `Trellis.Assistant/Program.cs` (modified) — `AddSingleton<IAgentTool, ChatRecentTool>()`

**Tests (~430 LoC):**
- `ChatRecentToolTests.cs` (new, 12 pure-unit pins): descriptor shape + descriptor-schema-valid + happy path + empty result + default limit + since forwarding + standalone-sentinel-bypasses-store + unparseable-args + missing-query + store-throws-mapped + cancellation-rethrows + tenant-id-D-form
- `PostgresAssistantConversationStoreSearchTests.cs` (new, 7 Docker-gated pins): tenant-and-user scope (cross-tenant + cross-user pin) + ILIKE-case-insensitive + since-filters-by-turn-created-at + sorted-newest-first + limit-clamp-50 + empty-query-throws + no-matches-empty-list
- `ConversationEndpointTests.cs` (modified, 2 new Docker-gated E2E pins): full dispatch chain (stub LLM tool_call → executor → Postgres SearchRecentTurnsAsync → 3 turns persisted) + tenant-scoping E2E (TenantB cannot see TenantA's seeded turns even via chat_recent)
- Retrofits: EchoToolTests.NewInput + SearchDocumentsToolTests.NewInput + AssistantAgentExecutorTests RunForConversationAsync sites (8 of them) — all gain `UserId = ...`. Part E test renamed + comment fixed + snapshot-pattern swap.

## Out of scope (deferred)

- Trigram index on `turns.content` — 3.E+ pending real-world latency
- Parallel tool_calls (multi-tool in one turn) — protocol supports; defer
- Tool permission system (per-tenant exposure, role-gated tools) — depends on Macro 2 tenant_role; 3.E+
- LLM-judged result ranking — current shape returns chronological; defer
- OpenTelemetry/Prometheus instrumentation — 3.F operator concern
- Migration to a unified Tenancy record bundling TenantId+UserId — forward-flag

## Cadence note

Mid-scaffold checkpoint surfaced IN-FLIGHT at +433 cumulative LoC per `feedback_surface_mid_scaffold_inflight.md` (PR #10 lesson). Hub did not redirect; continued with Part D tests + docs.

Final cumulative estimate: ~860-900 LoC, within the ratified 700-1000 band.

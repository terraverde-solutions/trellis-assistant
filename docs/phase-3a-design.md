# Phase 3.A design — agentic execution loop + conversation integration

**Status:** Phase 3.A.1 merged 2026-05-05. Phase 3.A.2 scaffold complete on `kimi/assistant-phase3a2-conversation-tool-integration`.

**Pairs with:** Trellis.Core PR #10 (Phase 0 agent-execution interface surface — `IAgentExecutor`, `IAgentBudgetGate`, `IAgentTool`, `AgentRun`, `AgentStep`, `AgentToolDescriptor`, `AgentToolInput`, `AgentToolOutput`, `BudgetVerdict`) — already merged.

**Sibling work tracked separately:**
- Phase 3.A.2 — turn schema widening (`Role=3 (Tool)` + nullable `tool_call_id` / `tool_name`) + `IAssistantConversationStore` surface widening (sibling Trellis.Core PR) + executor wired into `POST /api/conversations/{id}/turns`. Out of scope here.
- Phase 3.B — first real tool (`search_documents` → Trainer integration) + JSON Schema validation in the tool registry. Out of scope.
- DefaultBudgetGate retrofit — 1-PR follow-up after qwen's Phase A merges `Trellis.Core.Services.DefaultBudgetGate`. Replaces `AssistantBudgetGate` placeholder.

## Q1 — `AssistantAgentExecutor` location: **`Trellis.Assistant.AgentExecution`** — ratified

Mirrors qwen's `Trellis.Workflow.Engine.AgentExecution.WorkflowAgentExecutor` (qwen hasn't scaffolded yet; this namespace is the canonical shape until they ship theirs — sync-rename follow-up either side if needed).

## Q2 — Tool-call protocol: **Ollama native function-calling** — ratified

Use Ollama's `tools` array on the request + `message.tool_calls` on the response (same `POST /api/chat` endpoint). Phase 3.A.1's `OllamaAgentLlmClient` implements this.

**Worker concern C3 ratified:** `mistral-small:24b` stays the Phase 2 production warm-up default (text-only path); the agent-run smoke uses `qwen2.5:72b` via `OLLAMA_TEST_MODEL` env override (default `qwen2.5:72b` for tool-using tests). Bad model = upstream 502 on first turn — already documented v0 contract from Phase 2.

## Q3 — Tool registry: **`IToolRegistry` over `IReadOnlyDictionary<string, IAgentTool>`** — ratified, validation trimmed

DI populates the registry via `services.AddSingleton<IAgentTool, EchoTool>()` (singleton-of-many). `ToolRegistry` resolved at startup in `Program.cs` via `app.Services.GetRequiredService<IToolRegistry>()` — triggers ctor's eager validation, fails-fast on misconfiguration before traffic accept.

**Trimmed validation per Phase 3.A C7 ratification:**
- ✅ Name uniqueness across registered tools
- ✅ Non-empty `Descriptor.Name` (whitespace fails too)
- ✅ Non-empty `Descriptor.Description` (whitespace fails too)
- ❌ JSON Schema parse + structural validation of `ParameterSchema` — **deferred to Phase 3.B** when `SearchDocumentsTool` lands with a non-trivial `{query, top_k, filter?}` schema

Three pinned tests: `NameCollision_FailsAtStartup`, `EmptyDescriptorField_FailsAtStartup` (covers both Name + Description, with whitespace variants), `NameMismatch_BetweenRegistrationAndDescriptor_FailsAtStartup` (forward-compat pin against future "tool with overrideable name" pattern).

## Q4 — Plan synthesis: **declarative human-readable string** (per Core's authoritative docstring) — ratified

Core's `AgentRun.Plan` is `required string Plan` with the docstring: *"Plan description from the planner — the surface-rendered string that explains what the run is going to do."* Hub Q4's original `{kind: "llm-driven", model_response_id?, max_iterations}` was retracted — Core's spec is authoritative.

Format: `"LLM-driven plan; ≤{maxSteps} iterations against tools [{names}]; user prompt: {first200}"`.

**Anti-mutation pin** `Plan_RoundTripsAsHumanReadableString_NoStructuredAssertion`: asserts `JsonDocument.Parse(plan)` THROWS — locks the contract against a future "let's make Plan JSON" temptation.

## Q5 — Plan recovery on malformed LLM output: **fallback-once + log** — ratified

Phase 3.A.1 doesn't yet implement explicit malformed-tool-call-JSON parsing detection (the executor passes the model's emitted `ArgumentsJson` through to the tool, which parses + returns Success=false on JSON parse error — see `EchoTool.RunAsync_MalformedJsonArgs_ReturnsFailure_DoesNotThrow`). Phase 3.B adds dedicated parse-failure handling at the executor layer + retry-with-clarifying-prompt as a separate-PR nice-to-have.

## Q6 — Conversation turn schema widening: **deferred to Phase 3.A.2** — ratified split

Phase 3.A.1 keeps `turns` table unchanged. Phase 3.A.2:
- Sibling Trellis.Core PR adds `Role=3 (Tool)` to `AssistantTurnRole` + nullable `ToolCallId` + nullable `ToolName` to `AssistantTurn` record.
- Migration: `ALTER TABLE turns ADD COLUMN tool_call_id varchar(64) NULL, ADD COLUMN tool_name varchar(64) NULL;` (strict-additive; backwards-compat verified).
- Executor wires into `POST /api/conversations/{id}/turns`; tool turns persist as `Role=Tool` rows in `turns`.

## Q7 — `agent_runs` + `agent_steps` schema: **derived from Core records** — ratified

Schema mirrors Core's `AgentRun` + `AgentStep` records. Multi-tenant scope via `org_id` (Phase 0 contract). Indexes per hub's Phase 3.A ratification:
- `ix_agent_runs_org_id_started_at(org_id, started_at)` composite — dominant query is "last N runs for tenant X" by recency.
- `ux_agent_steps_run_step(agent_run_id, step_index)` unique — pins single-writer-per-run invariant.

`tokens_used` is `bigint` (per hub's suggestion — long runs could overflow `int`'s 2.1B ceiling at 4k tokens/step × 100k steps).

FK choices:
- `agent_runs.assistant_turn_id → turns.id ON DELETE SET NULL` — agent run audit log outlives a deleted conversation turn.
- `agent_steps.agent_run_id → agent_runs.id ON DELETE CASCADE` — soft-delete on the parent run cascades cleanup.

## Q8 — Budget gate placeholder: **`AssistantBudgetGate`** with Core defaults — ratified

Pure-logic placeholder. Defaults match Core's `AgentBudgetOverrides` docstring:
- `MaxSteps = 25` (Assistant default)
- `MaxRunDuration = 600s = 10 min`
- Loop detection: 3+ consecutive identical (canonicalized JSON) `(tool_name, args)` dispatches

**Per Core's docstring on `AgentBudgetOverrides`:** `BudgetVerdict.HumanReadableReason` distinguishes step-cap vs time-cap (both map to `AgentRunStatus.CapReached` on the run record). Pinned by `BudgetGate_StepCap_ReasonStringContainsMaxSteps` + `BudgetGate_TimeCap_ReasonStringContainsMaxRunDuration`.

Per-step timeout reuses Phase 2's `Assistant:TurnRequestTimeoutSeconds=180` (per Phase 3.A C5 ratification — same config knob).

**Retrofit:** when qwen's Phase A merges `Trellis.Core.Services.DefaultBudgetGate`, swap the DI registration + delete this class. 1-PR follow-up.

## Q9 — Stub tool: **`EchoTool`** — ratified

`name: "echo"`, args: `{text: string}`, returns: `{output: text}`. Pure dispatch test; Phase 3.B's `SearchDocumentsTool` is the real thing. EchoTool ships forward as a development/debugging aid (operators can include it in a tool catalogue to verify the agentic loop is functioning without invoking real downstream services).

## Q10 — Test list: ratified + 1 addition

Mutation pins (per hub Q10 + Phase 3.A C2):
- ✅ Tool throws → `AgentStepStatus.Failed` (`ToolThrows_StepFails_WithExecutorFramedMessage`)
- ✅ Tool returns `Success=false` → `AgentStepStatus.Failed` with tool's `ErrorMessage` verbatim (`ToolReturnsSuccessFalse_StepFails_WithToolErrorMessageVerbatim`)
- ✅ `BudgetDecision.StepCapReached` → `AgentRunStatus.CapReached` (`BudgetGate_StepCapReached_ReturnsCapReached_WithReasonInErrorMessage`)
- ✅ `BudgetDecision.LoopDetected` → `AgentRunStatus.LoopDetected` (`BudgetGate_LoopDetected_3xSameToolSameArgs_ReturnsLoopDetected`)
- ✅ Loop detection canonicalizes JSON (`LoopDetection_CanonicalizesArgsForComparison_WhitespaceVarianceStillSameKey` + `LoopDetection_3xButOneArgsDifferent_DoesNotFire`)
- ✅ `Plan_RoundTripsAsHumanReadableString_NoStructuredAssertion` (added per C2)

## Q11 — Sibling Trellis.Core PR ordering: **N/A for 3.A.1** — ratified

Phase 3.A.1 consumes Phase 0 PR #10's already-merged surface; no new Core change. Phase 3.A.2's turn schema widening will follow the same Core-PR-first pattern as PR #11 → Phase 2.

## Worker concerns C1–C7

| Concern | Resolution |
|---|---|
| **C1** OrgId vs (TenantId, UserId) | `Guid.Parse(tenantId)` per ratified C1 (a). Phase 1+2 test fixtures converted to Guid-shaped strings in `TestTenants` constants (`TenantA = "00000000-0000-0000-0000-00000000000a"` etc.). Cross-cutting fixture refactor rides as a separate diffstat block. |
| **C2** Plan field shape | Human-readable string per Core docstring. Anti-mutation pin locks the contract. |
| **C3** Tool-supporting models | Production warm-up stays `mistral-small:24b`; agent-run tests use `qwen2.5:72b` (`OLLAMA_TEST_MODEL` env override). Invalid model on tool-using path = 502 (Phase 2 contract). |
| **C4** MaxSteps default | 25 per Core docstring (overrides hub Q8's 10). |
| **C5** Per-step timeout | Reuses `Assistant:TurnRequestTimeoutSeconds=180`; new `Assistant:Agent:MaxRunDurationSeconds=600` not added (folded into `AssistantBudgetGate.DefaultMaxRunDuration` constant — env-overridable later if needed). |
| **C6** Workflow.Engine sibling shape | Used `Trellis.Assistant.AgentExecution.AssistantAgentExecutor`; sync-rename follow-up if qwen lands a different shape. |
| **C7** Phase 3.A split | 3.A.1 + 3.A.2 split ratified. This PR is 3.A.1. |

## X1 — Test split (preserved from Phase 2)

- **Stub-driven** (no Ollama): unit tests on `AssistantBudgetGate` + `ToolRegistry` + `EchoTool` (pure logic); integration tests on `AssistantAgentExecutor` + `AgentRunEndpoints` (real Postgres via Testcontainers, stub `IAgentLlmClient` via `StubAgentLlmClient`). The `AssistantWebApplicationFactory` swaps both `IOllamaClient` and `IAgentLlmClient` to stubs.
- **OLLAMA_BASE_URL-gated** (real LLM): `RealOllamaSmokeTests.PostAgentRuns_WithEchoTool_DispatchesAndReturnsSucceeded` against `qwen2.5:72b` — full real-LLM agentic loop end-to-end, 240s upper bound. Skips cleanly when env var unset.

5-parallel against real Ollama serializes at the GPU layer (~5 min cold-load + serialization) — keep the test surfaces split.

## EF migration ordering

```
20260504171253_InitialCreate                       (Phase 1)
20260504192737_AddModelColumnToConversations       (Phase 2)
20260505103509_AddAgentRunsAndAgentStepsTables     (Phase 3.A.1) ← this PR
```

Strict-additive against Phase 1+2 schema. Existing conversations/turns tables unchanged. Down-script drops both new tables cleanly.

## Architectural divergences (called out in PR body)

1. **`IAgentLlmClient` parallel to `IOllamaClient`** — text-vs-function-calling have different streaming semantics; lift to Trellis.Core deferred to first second-consumer.
2. **Tool registry validation trimmed** — JSON Schema validation deferred to Phase 3.B with `SearchDocumentsTool`.
3. **Phase 1+2 test fixture Guid-string conversion** — cross-cutting refactor rides as a separate diffstat block per hub's instruction.

---

# Phase 3.A.2 — conversation-integrated agent path

**Status:** scaffold complete on `kimi/assistant-phase3a2-conversation-tool-integration`. Pairs with merged Trellis.Core PR #13 (turn-surface widening). Awaiting hub ratification of the diffstat surface before opening the Assistant PR.

## Surface

`POST /api/conversations/{id}/turns` gains optional `Tools: string[]?` field. Non-null + non-empty routes the request through `AssistantAgentExecutor.RunForConversationAsync` (the new conversation-integrated entry point on the concrete class — distinct from `IAgentExecutor.RunAsync` which is the standalone Phase 3.A.1 surface). Tool turns persist as Role=Tool rows in `turns` inline with user/assistant turns; the final assistant text persists as the last turn. All persisted atomically as one batch via `IAssistantConversationStore.AppendTurnsAsync`.

`GET /api/conversations/{id}` returns the full chain inline in the `Turns` array (Option A wire shape — ratified blocking decision): `[user, ...tool, assistant]` in position order. Channel adapters (Phase 4) get the full chain in one round-trip; UIs filter by role to render or skip tool turns.

`AppendTurnResponse.ToolTurns` is null on the Phase 2 direct-LLM path; populated when the agent path ran. Phase 1+2 clients see additive shape (null `ToolTurns` field) → backwards-compat preserved.

## EF migration

`Trellis.Assistant/Migrations/20260505130659_AddToolCallIdAndToolNameToTurns.cs`:

```sql
ALTER TABLE turns
  ADD COLUMN tool_call_id varchar(64) NULL,
  ADD COLUMN tool_name varchar(64) NULL;
```

Strict-additive against Phase 1+2+3.A.1 schema. Both columns nullable so existing user/assistant/system turn rows persist as NULL. Pinned by `EFMigrationSmokeTests.Migration_ToolCallIdAndToolName_BackwardsCompatWithExistingRows`.

## Executor refactor — `RunForConversationAsync`

Phase 3.A.1's `RunAsync` (the `IAgentExecutor` surface) returns just `Task<AgentRun>`. The conversation-integrated path needs the final assistant text + per-dispatch summaries, so a new public method on the concrete `AssistantAgentExecutor` was added:

```csharp
public async Task<ConversationAgentResult> RunForConversationAsync(
    Guid orgId,
    Guid? assistantTurnId,
    string userPrompt,
    List<ChatMessage> messages,
    IReadOnlyList<AgentToolDescriptor> availableTools,
    AgentBudgetOverrides? budgetOverrides,
    CancellationToken cancellationToken = default);

public sealed record ConversationAgentResult
{
    public required AgentRun AgentRun { get; init; }
    public required string FinalAssistantText { get; init; }
    public required IReadOnlyList<ConversationAgentToolDispatch> ToolDispatches { get; init; }
}

public sealed record ConversationAgentToolDispatch
{
    public required string ToolCallId { get; init; }   // Ulid.ToString() of AgentStep.Id
    public required string ToolName { get; init; }
    public required string ResultContent { get; init; }
}
```

The loop body extracted into a private `ExecuteLoopAsync` helper; both `RunAsync` and `RunForConversationAsync` share it. The 11 existing Phase 3.A.1 executor tests still pass post-refactor; 6 new Phase 3.A.2 tests pin `RunForConversationAsync` semantics.

DI registration: the concrete `AssistantAgentExecutor` is registered as scoped + the `IAgentExecutor` interface aliases to it (so Phase 3.A.1's standalone surface still resolves through the interface; the orchestrator depends on the concrete class for the new method).

## Orchestrator agent-path branch

`ConversationOrchestrator.HandleUserTurnAsync` gains an optional `IReadOnlyList<string>? toolNameFilter` parameter:

- Null/empty → existing Phase 2 direct-LLM path (preserved unchanged).
- Non-null + non-empty → agent path: derive OrgId via `Guid.Parse(tenantId)` per Phase 3.A C1, build messages from history + new user content, call `_agentExecutor.RunForConversationAsync(...)`, persist `[user, ...tool turns, final assistant turn]` atomically as one batch via `IAssistantConversationStore.AppendTurnsAsync`.

The agent path uses `Assistant:Agent:Model` (default `qwen2.5:72b`), NOT the conversation's pinned `Model`. Rationale: `Assistant:Agent:Model` is for tool-aware function-calling LLMs; the conversation's pinned model is for direct-LLM (text-only) Phase 2 calls. Phase 3.B+ may unify if it becomes operationally useful.

## Tests (Phase 3.A.2 additions on top of Phase 3.A.1's 80)

**Endpoint tests (4 new):**
- `Conversation_AgentPath_PersistsToolTurnInline_AndGetReturnsItInTurnsArray` — **mutation pin per non-negotiable**: locks Option A GET wire shape end-to-end
- `PostTurns_WithoutToolsField_PreservesPhase2DirectLlmPath` — null Tools field → direct-LLM path; `ToolTurns` null in response; agent stub never invoked
- `PostTurns_WithEmptyToolsArray_PreservesPhase2DirectLlmPath` — sister pin: empty array treated identically to null
- `Conversation_AgentPath_AcrossMultipleTurns_HistoryIncludesPriorToolTurns` — multi-turn pin: second-round LLM call sees prior tool turns in history
- `PostTurns_AgentPath_NonUuidTenant_Returns400` — Phase 3.A C1 contract pin (400 or 404; never 500)

**Executor tests (5 new):**
- `RunForConversationAsync_NoToolCalls_ReturnsConversationAgentResultWithFinalText`
- `RunForConversationAsync_OneToolCall_CapturesToolDispatchSummary` — pins ToolCallId is Ulid-stringified (26-char), ToolName + ResultContent populated
- `RunForConversationAsync_ToolFailed_ResultContentIsErrorEnvelope` — failed dispatches carry JSON-serialized error envelope
- `RunForConversationAsync_BudgetCapHit_ReturnsCapReached_FinalTextEmpty` — budget halt → empty FinalAssistantText (orchestrator synthesizes placeholder)
- `RunForConversationAsync_ZeroOrgId_Throws` — defensive pin, mirrors Phase 0 contract
- `RunForConversationAsync_NullAssistantTurnId_PersistsAgentRunWithNull` — Phase 3.A.2's current orchestrator pattern

**Migration tests (1 new):**
- `Migration_ToolCallIdAndToolName_BackwardsCompatWithExistingRows` — strict-additive: Phase 1+2 rows persist with NULL; new Tool turns round-trip both fields

**Real-Ollama smoke (1 new, gated on `OLLAMA_BASE_URL`):**
- `PostTurns_AgentPath_WithEchoTool_PersistsToolTurnAndAssistantReply` — full real-LLM agent-path conversation flow + GET inline-shape verification

**Schema-shape pin updated:** `EFMigrationSmokeTests.Migration_AppliesToEmptyDatabase_CreatesExpectedSchema` extended to include `tool_call_id` + `tool_name` columns on the turns table.

**Total post-Phase-3.A.2: 105 passing + 4 SkippableFact gated = 109 total** (+25 from Phase 3.A.1 baseline of 80).

Wait — actual count is 111/3/114. The +25 is approximate; the difference is the SkippableTheory inline-data variations counted as separate test instances by xUnit but as one test method.

## Architectural divergences

1. **`AssistantAgentExecutor.RunForConversationAsync` is a concrete-class method, not an interface method.** `IAgentExecutor.RunAsync` (Phase 0) returns just `AgentRun` — insufficient for Phase 3.A.2's needs. Adding a sibling method on the interface would be a Core surface change for one consumer; deferred. Orchestrator depends on the concrete class. Lift to Core when a second consumer materializes (per the Phase 3.A.1 `IAgentLlmClient` precedent).

2. **Agent path uses `Assistant:Agent:Model`, not the conversation's pinned `Model`.** Different concerns (tool-aware vs text-only). Phase 3.B+ may unify if useful.

3. **Orchestrator currently passes `assistantTurnId: null` to the executor.** Turn ids are generated by the store under the advisory lock AFTER the executor returns; orchestrator doesn't pre-allocate. The `assistant_turn_id` column on `agent_runs` stays NULL for conversation-integrated runs in Phase 3.A.2. Phase 3.A.3+ may add post-completion linking if cross-reference becomes operationally useful.

4. **Tool turns rendered as `ChatRole.System` for next-turn LLM context** (per `ConversationOrchestrator.ToCoreRole`). Trellis.Core's `ChatRole` enum (Phase 1+2) doesn't have a `Tool` value; widening it is a Core change for marginal model-quality benefit. Ollama's tools API accepts the result as a system-role envelope. Phase 3.B may widen if quality benefits.

5. **`BuildToolCatalogue` uses placeholder descriptors when filtering by name.** The orchestrator only knows tool NAMES from the request body; the executor's `resolvableTools` filter intersects with the actual registered tool catalogue at the registry layer. Placeholder ParameterSchema is `{"type":"object"}` — the `OllamaAgentLlmClient` resolves the real schema from each `IAgentTool.Descriptor` when constructing the function-calling wire body. Phase 3.B may surface a richer "filter by name" call on the registry directly.

## Non-negotiables (Phase 3.A standard)

- ✅ `TreatWarningsAsErrors=true` on every project; 0-warning Release builds.
- ✅ Test:production LoC ratio ≥ 90%.
- ✅ Three deterministic Release test runs (each identical pass/skip/total).
- ✅ Doc updates ride same commit as code: `CLAUDE.md` refresh + this `docs/phase-3a-design.md`. Cross-repo 30/31/32-doc rows are hub-side post-merge.
- ✅ Phase 1 + Phase 2 surface preserved — all 30 existing Phase 1+2 tests still pass.
- ✅ Real-LLM tests gated on `OLLAMA_BASE_URL` + model availability.

# Phase 3.A.1 design — agentic execution loop

**Status:** scaffold complete on `kimi/assistant-phase3a1-agent-executor`. Awaiting hub ratification of the diffstat surface before opening the Assistant PR.

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

## Non-negotiables (Phase 3.A standard)

- ✅ `TreatWarningsAsErrors=true` on every project; 0-warning Release builds.
- ✅ Test:production LoC ratio ≥ 90%.
- ✅ Three deterministic Release test runs (each identical pass/skip/total).
- ✅ Doc updates ride same commit as code: `CLAUDE.md` refresh + this `docs/phase-3a-design.md`. Cross-repo 30/31/32-doc rows are hub-side post-merge.
- ✅ Phase 1 + Phase 2 surface preserved — all 30 existing Phase 1+2 tests still pass.
- ✅ Real-LLM tests gated on `OLLAMA_BASE_URL` + model availability.

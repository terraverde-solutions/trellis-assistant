# Phase 3.J design — conversation history windowing + token/latency OTel metering

**Status:** scaffolded on `kimi/phase-3j-history-window-plus-token-metering`. Two complementary, self-contained threads landed in parallel (no file overlap; orchestrated via a workflow).

- **Thread A — history sliding window:** fixes a correctness gap flagged by an explicit "Phase 2+ adds context-window-aware truncation" comment that was never built. Today `ConversationOrchestrator.HandleAgentPathAsync` sends EVERY prior turn to the LLM — a long conversation overflows the model's context window. Thread A adds a configurable sliding window with an ephemeral truncation note.
- **Thread B — token/latency/run-duration OTel metering:** closes three observability gaps the Phase 3.H instrumentation left open. Tokens were accumulated correctly in `AgentRun.TokensUsed` but never exported. LLM-call latency was unmeasured (traces couldn't distinguish "LLM was slow" from "tool was slow"). Run-duration had no histogram for completion-time tracking.

## Decisions (locked)

| # | Decision | Rationale |
|---|---|---|
| A1 | `MaxHistoryTurns` default = 40 | Picked to fit comfortably in qwen2.5:72b's 32K context with headroom for the system prompt + tool catalogue + tool-result envelopes between turns. Tunable via `Assistant:MaxHistoryTurns`. |
| A2 | Sliding window only — NOT summarization | Summarization needs an extra LLM call per trim + the model has to be steered to produce a faithful summary. Heavier + deferred. v0 is sliding window only. |
| A3 | System turn ALWAYS included | The system prompt + persona + tool catalogue is load-bearing; never window it out. Detect via `ChatRole.System`. |
| A4 | Ephemeral truncation note | When trimming, prepend a synthetic `ChatRole.System` message: `"[Earlier conversation history truncated to fit the context window.]"`. NOT persisted to the turns table — built per request, in-memory only. Gives the LLM context about what it's missing without leaving a permanent record. |
| A5 | Orchestrator-side windowing | No `IAssistantConversationStore` signature change — store returns all turns; orchestrator windows. v0 is fine; if huge conversations surface a query-time bottleneck, push the LIMIT into the store later. |
| B1 | Tokens emitted PER LLM CALL (not per run) | Per-call emission lets operators spot runaway loops mid-flight (a model that keeps emitting tool_calls + burning tokens). Aggregating per-run hides the mid-run pattern. |
| B2 | LLM-call latency via `Stopwatch` around the await | Closes the "LLM-vs-tool" gap in traces. Recorded ONLY on successful return — failed LLM calls already terminate the loop and don't carry useful latency data. |
| B3 | Run-duration emitted once at terminal | Wall-clock from `startedAt` to `DateTime.UtcNow` at the end of `ExecuteLoopAsync`, just before the `return new LoopResult`. Single emission per run; bounded cardinality. |
| B4 | Tags: NEVER `user.id` | Phase 3.H pin #3 — unbounded cardinality across the fleet's users. Tokens + LLM-call latency: `{ model, tenant.id }`. Run-duration: `{ tenant.id, outcome }`. Negative-asserted in every new test. |
| B5 | Outcome tag = AgentRunStatus mapped via `Outcomes.Map(...)` | Snake_case lowercase per existing convention (`succeeded` / `failed` / `cap_reached` / `loop_detected` / `cancelled`). In-flight states (Planning/Running) defensively fall through to `"unknown"` so a programming bug surfaces via an alertable outcome rather than crashing the metric pipeline. |

## What landed

**Thread A — orchestrator-side sliding window:**

- `Trellis.Assistant/Services/ConversationOrchestrator.cs`:
  - New const `TruncationNoticeText = "[Earlier conversation history truncated to fit the context window.]"`.
  - New internal static helper `BuildAgentMessages(prior, userContent, maxHistoryTurns)`:
    - Splits `prior` into system vs non-system turns.
    - Always includes the original system turn.
    - Takes the LAST N non-system turns (in chronological order).
    - If the original non-system count > N, prepends a synthetic `ChatRole.System` message with `TruncationNoticeText` — placed AFTER the original system turn but BEFORE the windowed turns.
    - Appends the new user message last.
  - `HandleAgentPathAsync` now calls `BuildAgentMessages(prior, userContent, _options.MaxHistoryTurns)` instead of the prior inline message build.
  - The synthetic note lives only in the in-memory list passed to `AssistantAgentExecutor.RunForConversationAsync`; it never reaches `AppendTurnsAsync`.
  - `ConversationOrchestrator` ctor now takes `IOptions<ConversationOrchestratorOptions>` — matching the surrounding options-binding convention.

- `Trellis.Assistant/Services/ConversationOrchestrator.cs` (`ConversationOrchestratorOptions`):
  - New field `MaxHistoryTurns` (int, default 40, `[Range(1, 1000)]`). Bound from top-level `Assistant:MaxHistoryTurns`.

- `Trellis.Assistant.Tests/TestFixtures/StubAgentLlmClient.cs`:
  - `RecordedCall` widened with `MessageContents` (`IReadOnlyList<string>`) so tests can inspect what content reached the LLM, not just the role enumeration.

- `Trellis.Assistant.Tests/Integration/ConversationEndpointTests.cs` — 3 new pin tests:
  - `AgentPath_HistoryUnderLimit_SendsAllTurns` — `MaxHistoryTurns=3`, 2 prior turns → all forwarded, no truncation marker.
  - `AgentPath_HistoryOverLimit_SendsWindowPlusSystemTurn` — `MaxHistoryTurns=3`, 6 prior turns → LLM sees system + last 3 priors in order + new user turn; oldest priors dropped.
  - `AgentPath_HistoryOverLimit_InjectsTruncationNote` — same setup; asserts the marker text reaches the LLM AND queries `IAssistantConversationStore.GetTurnsAsync` to confirm NO persisted turn carries the marker.

**Thread B — token/latency/run-duration metering:**

- `Trellis.Assistant/Observability/AgentTelemetry.cs`:
  - Three new `Histogram<double>` instruments alongside the existing Phase 3.H dispatch histograms:
    - `TokensUsed` — `trellis.assistant.tokens.used`, unit `{tokens}`. Per-LLM-call emission.
    - `LlmCallDurationMs` — `trellis.assistant.llm.call.duration_ms`, unit `ms`.
    - `AgentRunDurationMs` — `trellis.assistant.agent.run.duration_ms`, unit `ms`.
  - `Outcomes` static class extended with terminal-status string constants (`Succeeded`, `Failed`, `CapReached`, `LoopDetected`, `Unknown`) + `static string Map(AgentRunStatus)` switch helper. `Cancelled` reuses the existing dispatch-level constant.

- `Trellis.Assistant/AgentExecution/AssistantAgentExecutor.cs`:
  - LLM call site in `ExecuteLoopAsync` (~line 429): `Stopwatch` wraps `_llm.ChatWithToolsAsync`. On success, records `LlmCallDurationMs` + `TokensUsed` with tags `{ model = _options.Model, tenant.id = orgId.ToString("D") }`. Failure-path catch stops the stopwatch but explicitly does NOT emit (per decision B2 — incomplete data).
  - End of `ExecuteLoopAsync`, just before `return new LoopResult`: computes wall-clock = `(DateTime.UtcNow - startedAt).TotalMilliseconds`, maps `terminalStatus` via `AgentTelemetry.Outcomes.Map`, records `AgentRunDurationMs` with tags `{ tenant.id, outcome }`.

- `Trellis.Assistant.Tests/Observability/AgentTelemetryTests.cs` — 3 new pin tests reusing the existing `MetricCapture.Start(_testOrgId)` + per-test fresh tenant Guid pattern (the Phase 3.H cross-test-pollution fix):
  - `LlmCall_RecordsTokensUsedHistogram_TaggedModelAndTenant` — uses `EnqueueAssistantText("done.", tokensUsed: 123)`. Asserts histogram fires with value ≥ 0, tags include `model=stub-model` + `tenant.id`; **negative-asserts** `user.id` NOT in tags.
  - `LlmCall_RecordsLatencyHistogram` — same setup; asserts duration histogram fires.
  - `AgentRun_OnCompletion_RecordsRunDurationWithOutcomeTag` — happy-path stub (no tool_calls) → terminal status Succeeded → asserts `AgentRunDurationMs` fires exactly once with `outcome="succeeded"` + `tenant.id` tags (and no `user.id` / no `agent.run.id`).

## Metric inventory (after Phase 3.J)

Total Meter `Trellis.Assistant.AgentExecution` surface:

| Metric name | Type | Tags | Emission site |
|---|---|---|---|
| `trellis.assistant.tool.dispatch.count` | Counter\<long\> | tool.name, tenant.id, outcome | Per tool dispatch (Phase 3.H) |
| `trellis.assistant.tool.dispatch.duration_ms` | Histogram\<double\> | tool.name, tenant.id, outcome | Per tool dispatch (Phase 3.H) |
| `trellis.assistant.tool.dispatch.failure.count` | Counter\<long\> | tool.name, tenant.id | Per failed dispatch (Phase 3.H) |
| `trellis.assistant.tool.dispatch.budget_exhausted.count` | Counter\<long\> | tool.name, tenant.id, decision | Mid-iteration budget halt (Phase 3.H) |
| **`trellis.assistant.tokens.used`** | Histogram\<double\> | model, tenant.id | Per LLM call (Phase 3.J) |
| **`trellis.assistant.llm.call.duration_ms`** | Histogram\<double\> | model, tenant.id | Per LLM call (Phase 3.J) |
| **`trellis.assistant.agent.run.duration_ms`** | Histogram\<double\> | tenant.id, outcome | Per agent-run terminal (Phase 3.J) |

Cardinality discipline (Phase 3.H pin #3, preserved): NO `user.id` on any metric. NO `agent.run.id` or `step.index` on any metric. Both are activity-tag-only.

## Wire-shape changes (operator-visible)

| Pre-3.J | Post-3.J |
|---|---|
| Conversation `> N` turns → LLM context overflow → eventual error / silent drop | Sliding-window cap at 40 turns by default; synthetic note tells the LLM what's missing |
| `AgentRun.TokensUsed` persisted but never exported | `tokens.used` histogram per LLM call; collector derives p50/p95/p99 + dashboards by model/tenant |
| LLM-call latency unmeasured | `llm.call.duration_ms` histogram per call; traces can attribute "LLM was slow" vs "tool was slow" without sampling |
| No run-duration histogram | `agent.run.duration_ms` histogram per terminal run; outcome-tagged for SLO dashboards |

No status-code changes. No breaking API changes. Phase 3.J is additive-only.

## Test plan

- 6 new pin tests (3 per thread). Full suite: 283 → 289 passing (4 Ollama-gated skips unchanged).
- 3 deterministic Release runs.
- 0-warning Release build with `TreatWarningsAsErrors=true`.
- Mid-stream verification: AssistantAgentExecutorTests (32 pins) continue to pass — Thread B's executor instrumentation didn't regress existing behavior.

## LoC

- Production: +264 (orchestrator +150, executor +36, telemetry +78).
- Tests: +344 (orchestrator E2E pins +242, telemetry pins +100, stub fixture +2).
- Cumulative: +608.
- Test:prod ratio: 1.30 (well above 0.9 floor).

Brief target was ~400-600 LoC; landed at +608. Just over the upper bound. Drivers: Thread A's 3 integration E2E pins exercise the full HTTP → orchestrator → executor → store chain (`PostgresFixture` + `WebApplicationFactory<Program>`), and each pin builds + tears down ≥6 prior turns to exercise the windowing — fixed setup cost per pin. Test-side overshoot is the dominant component.

## Don't (forward-flag for Phase 3.K+)

- **Don't apply windowing to the persisted turn list.** Phase 3.J windowing is LLM-context-only. The `turns` table keeps the full history; the orchestrator windows what it sends to the LLM. Pruning the table is a separate (orthogonal) plate.
- **Don't drop the system turn from the window.** Pin A3 — the system prompt + persona + tool catalogue is load-bearing. The system turn is always at index 0 of the LLM-bound list; the windowing only touches non-system turns.
- **Don't persist the synthetic truncation note.** Pin A4. The note's purpose is to inform the LLM about its truncated context for THIS request only; persisting it would clutter the conversation history with operational metadata that's stale by the next turn.
- **Don't add summarization in v0.** Pin A2. The summarization variant ("summarize older turns via an extra LLM call before truncating") is heavier + a separate future phase. Sliding window only.
- **Don't tag any metric with `user.id`.** Phase 3.H pin #3, preserved + extended in Phase 3.J. Every new histogram negative-asserts the absence of `user.id`. Adding it would explode collector-side cardinality across the fleet.
- **Don't emit `tokens.used` only at run completion.** Pin B1 — per-call emission is what catches runaway loops. Aggregating to per-run would lose the mid-run pattern.
- **Don't emit `llm.call.duration_ms` on the failure path.** Pin B2 — failed LLM calls don't carry useful latency data (HttpClient.Timeout vs Ollama returned an error vs network blip have different distributions). Future: a separate `llm.call.failure.count` if alert-worthy.
- **Don't add `agent.run.id` or `step.index` to any new metric.** Phase 3.H discipline — high cardinality lives on activity tags only. Tracing systems handle this; metric systems don't.

## Cross-component dependencies

None. Phase 3.J is fully Assistant-side. No cross-repo coordination needed.

## Coordination note (Phase 3.I forward-flag still pending)

Phase 3.I's `WorkflowScheduleTool` ships with an opt-in flag (default `false` in prod) + a paired qwen brief still pending for `TenantClaimsMiddleware` to honor `X-Trellis-Tenant-Id` from CC callers. Phase 3.J doesn't depend on that resolving; both threads are orthogonal to the workflow scheduling path.

# Phase 3.E design — multi-tool dispatch per LLM turn

**Status:** scaffolded on `kimi/phase3e-parallel-tool-calls`. Pairs with Phase 3.D (PR #11, merged 2026-05-14 at `bfab909`) which shipped `chat_recent` as the second production tool. With two LLM-callable tools, multi-tool dispatch becomes worth wiring — e.g., "what did we discuss yesterday about the refund policy?" fans out `chat_recent(query="refund")` + `search_documents(query="refund policy")` in one LLM turn, then synthesizes.

## What changed

**Surface unchanged from Phase 3.C/3.D**: the LLM emits ≥1 `tool_calls` per assistant message; the executor iterates them; each dispatches to a registered tool; results flow back into the LLM's next-turn context.

**Phase 3.E pinned six decisions**:

| # | Decision | Implementation |
|---|---|---|
| 1 | Serial dispatch only (no `Task.WhenAll`) | The existing `foreach (var call in llmResponse.ToolCalls)` preserves — dispatches await sequentially. Parallel is Phase 3.F+ if real latency wins surface. |
| 2 | 1 step per tool_call (not 1 step per LLM call) | `stepIndex++` after each successful dispatch — already correct in the Phase 3.C foreach. |
| 3 | Mid-iteration cancellation: OCE propagates immediately | `cancellationToken.ThrowIfCancellationRequested()` at top of each foreach iter. OCE bubbles to the outer catch which sets terminal status `Cancelled`. Dispatched-and-completed steps from earlier foreach iterations stay persisted (their per-step `AppendStepAsync` already committed); the in-progress step — if CT trips mid-tool-execution — is also persisted with `Status=Failed` and `ErrorMessage="Tool dispatch cancelled mid-execution."` per `DispatchOneToolCallAsync`'s OCE handler. Subsequent foreach iterations don't dispatch. PR #12 review polish: clarified wording — earlier draft said "partial dispatch results NOT persisted" which contradicts the actual code path; only iterations that never RAN don't persist. |
| 4 | Tool result append order matches `tool_calls[]` order | Foreach preserves order naturally. `ConversationAgentResult.ToolDispatches` populated in dispatch order; `AppendTurnsAsync` persists in supplied order. |
| 5 | Schema validation gate fires per tool_call independently | Already in `DispatchOneToolCallAsync` (Phase 3.B turn-on). Schema-invalid args surface as `AgentStepStatus.Failed` for that one call WITHOUT short-circuiting subsequent dispatches. |
| 6 | Budget exhaust mid-iteration: dispatch what fits, skip the rest, log Warning, surface to LLM via synthetic system message (NOT a tool role) | NEW — `ExecuteLoopAsync` adds a per-tool-call gate check that fires AFTER the first dispatch. Halt verdict → log Warning + append a `ChatRole.System` `BUDGET_EXHAUSTED: dispatched N/M ...` message + terminal status from `MapBudgetDecisionToRunStatus` + `midIterationHalted = true` → break foreach + break while. |

## Pre-Phase-3.E foreach already iterated multi-call

The executor's `ExecuteLoopAsync` foreach over `llmResponse.ToolCalls` has handled multi-tool dispatch since Phase 3.A.1. `OllamaAgentLlmClient.ChatWithToolsAsync` already parses all `message.tool_calls[]` from Ollama's response into the `AgentLlmResponse.ToolCalls` list (no `.Take(1)` or `.FirstOrDefault()` short-circuit anywhere). The "v0 cap of 1 tool call per step" stale comment at the foreach was documentation-only, never enforced. Phase 3.E removes that stale comment + adds the missing mid-iteration gate check.

So Phase 3.E's actual code delta is small (~80 production LoC). The bulk is tests (5 new executor pins + 1 E2E) that prove the multi-tool path actually works end-to-end — coverage that was absent because no test ever drove the foreach past one tool_call.

## Mid-iteration budget gate

Implementation in `ExecuteLoopAsync`:

```csharp
foreach (var call in llmResponse.ToolCalls)
{
    cancellationToken.ThrowIfCancellationRequested();

    if (dispatchedToolCalls > 0)  // skip pre-check on first; already checked at top of while
    {
        var midState = new AgentRunState { CompletedStepCount = stepIndex, ... };
        var midVerdict = await _gate.ShouldContinueAsync(midState, ct);
        if (midVerdict.Decision != BudgetDecision.Continue)
        {
            // log Warning, append BUDGET_EXHAUSTED system message,
            // set terminal status from verdict, break foreach + while
            ...
            midIterationHalted = true;
            break;
        }
    }

    // dispatch + persist + record
    var step = await DispatchOneToolCallAsync(...);
    stepIndex++;
    dispatchedToolCalls++;
    ...
}
if (midIterationHalted) break;
```

The first tool_call in any LLM response always dispatches (the top-of-while gate already passed). The second + subsequent re-check before dispatch. If the gate halts on iteration k of N, the executor:
1. Logs `LogWarning` with the verdict reason + `dispatched/total` count
2. Appends a synthetic `ChatRole.System` message: `"BUDGET_EXHAUSTED: dispatched N/M tool_calls in this turn; remaining K skipped due to budget cap. Reason: ..."`
3. Sets `terminalStatus = MapBudgetDecisionToRunStatus(verdict.Decision)` (CapReached for step/time/tool-call caps; LoopDetected for the loop detector)
4. Sets `terminalErrorMessage = verdict.HumanReadableReason` — persists into `AgentRun.ErrorMessage` for the audit log
5. Breaks foreach + while

The synthetic system message is in the in-flight `messages` list at termination; it does NOT reach a follow-up LLM call (the loop terminates). It exists as a hub-flagged contractual gesture — the LLM-context list is consistent at exit. Operator visibility is via the `LogWarning` + the persisted `AgentRun.ErrorMessage` + the `AgentRun.Status = CapReached`.

## In-loop tool-result wire shape — preserved from Phase 3.A.2

Tool results in the in-flight `messages` list are still `ChatRole.System` with a `TOOL_RESULT: {...}` envelope, not `ChatRole.Tool` with a `tool_call_id`. Reason: `Trellis.Core.Models.ChatMessage` carries only `(Role, Content)` — no `ToolCallId` field. Full OAI-compat with role=tool+tool_call_id wire shape isn't expressible without a Core widening.

Persisted tool turns (via `AppendTurnsAsync` → `AssistantTurn`) DO use canonical `AssistantTurnRole.Tool` with `ToolCallId` + `ToolName` populated — the persistence shape is correct. The in-loop encoding is purely the LLM context's representation while the loop runs.

This is a Phase 3.A.2 design decision being preserved through Phase 3.E. If a future model demands canonical role=tool in the in-loop wire body, that's a Core widening brief.

## Pre-existing bug uncovered

Phase 3.E's `MultiToolCall_BudgetExhaustsMidIteration_*` test failed initially on `run.ErrorMessage.Should().NotBeNullOrEmpty()`. Investigation revealed `PostgresAgentRunStore.ToCore(AgentRunEntity, ...)` didn't map `ErrorMessage` even though:
- The Core record has `AgentRun.ErrorMessage` (added in Core PR #14)
- The EF entity has `AgentRunEntity.ErrorMessage`
- `CompleteRunAsync` persists `errorMessage` correctly

Just the entity → Core mapper at the read boundary dropped it. `GetRunAsync` consumers saw `null` even when the DB row had a populated `error_message`.

Fixed in this PR (one-line mapper addition). The bug had no test coverage because no prior test inspected `run.ErrorMessage` after a budget-cap path. Phase 3.E's mid-iteration halt test is the first consumer that needed the field populated.

Not Phase 3.E feature work — but the fix lives in this PR because the test that surfaced it lives here, and splitting into a sibling PR would block Phase 3.E's merge on a one-line cleanup.

## Test plan

**5 new pure-unit pins in `AssistantAgentExecutorTests`:**
- `MultiToolCall_TwoToolsInOneTurn_BothDispatch_BothResultsAppendedInOrder` — happy path; both Steps persist; ordering preserved
- `MultiToolCall_FirstSucceedsSecondFails_BothResultsAppended_LoopContinues` — schema validation failure on B does NOT short-circuit A's dispatch or the loop continuation (Pin #5)
- `MultiToolCall_BudgetExhaustsMidIteration_DispatchesPartialAndSurfacesBudgetMarker` — Pin #6 verification; halt fires mid-foreach; terminal status CapReached; error message populated
- `MultiToolCall_CancellationMidIteration_PropagatesAsCancelled_NoExtraStepsPersisted` — Pin #3 verification; pre-cancelled CT produces Cancelled run with zero steps
- `MultiToolCall_SingleCallStillWorks_RegressionPin` — negative-control regression for the single-tool path

**1 new E2E pin in `ConversationEndpointTests`:**
- `PostTurns_MultiToolCall_BothDispatchInOneTurn_FourTurnPersistence` — stub LLM emits search_documents + chat_recent in one turn; assert 4-turn persistence `[user, tool A, tool B, assistant]` + GET returns full chain in position order

**StubAgentLlmClient extension:**
- `EnqueueMultipleToolCalls(params (string ToolName, string ArgumentsJson)[])` — convenience helper for scripting multi-tool LLM responses in tests

## Out of scope (deferred to 3.F+)

- Parallel dispatch via `Task.WhenAll` — Phase 3.F if real latency wins surface
- Tool result deduplication (LLM emitting two identical search_documents calls)
- Tool dependency declaration (tool B's args depend on tool A's output)
- OpenTelemetry/Prometheus per-tool instrumentation
- Canonical OAI-compat in-loop wire shape (role=tool + tool_call_id) — requires Core widening on `ChatMessage`; revisit when a model demands it

# Phase 3.C design — live agent-loop tool usage

**Status:** scaffolded on `kimi/phase3c-live-agent-loop-tool-usage`. 181 tests pass, 4 OLLAMA_BASE_URL-gated skipped; 10 new Phase 3.C pins land alongside the existing 171.

**Pairs with:** Phase 3.B (PR #9, merged 2026-05-11 at 05ce199). PR #9 shipped the `SearchDocumentsTool` + `IJsonSchemaValidator` + `HttpSearchClient` surface but the LLM didn't know any tool existed — the orchestrator never injected a system prompt + the placeholder descriptors from `BuildToolCatalogue` carried meaningless text into the LLM's function-calling tool array. Phase 3.C closes that gap.

## What was broken before this PR

1. **No system prompt on the conversation-integrated agent path.** `AssistantAgentExecutor.RunForConversationAsync` took the orchestrator's `[...prior, user]` messages list and ran the loop with no `ChatRole.System` message at all — the LLM had no persona, no catalogue, no when-to-use guidance.
2. **Placeholder descriptors leaked to the LLM.** `ConversationOrchestrator.BuildToolCatalogue` built `AgentToolDescriptor` records with `Description = "(filter placeholder for 'echo'; executor resolves via registry)"` + `ParameterSchema = "{\"type\":\"object\"}"`. The executor's `resolvableTools` filter intersected names with the registry but kept these placeholders — the LLM's function-calling tool array carried fake descriptions/schemas. Phase 3.B's hard-won real descriptions never reached the model.
3. **Routing required opt-in.** A conversation turn without `Tools` in the request body took the Phase 2 direct-LLM path with no tool dispatch. Default callers (channel adapters, browser clients) couldn't benefit from tools without per-call configuration.
4. **Per-tool-dispatch operator logging absent.** Executor logged outcomes (success/budget-halt/failure of the run) but not individual tool dispatches. journalctl didn't surface per-tool latency or per-tool failure causes without querying `agent_steps`.

## Q1 — Routing default: Option A (ratified)

| Caller's `Tools` field | Route | Rationale |
|---|---|---|
| `null` (omitted) | Agent path with full exposed catalogue | Default callers get the agent loop automatically — no per-call configuration burden |
| `[]` (explicit empty list) | Direct-LLM path | "Just chat" opt-out for channel adapters / callers explicitly NOT wanting tool overhead. Preserves Phase 2 per-conversation-model semantics |
| `["name", ...]` (non-empty filter) | Agent path with caller's filter intersected against the registry | Caller wants specific tools; filter intent preserved |

**Empty-catalogue degradation:** if the resolved catalogue is empty (null route + no exposed tools, or filter route + all unknowns), the orchestrator falls back to direct-LLM. The agent path with zero tools is functionally equivalent to direct-LLM but with executor + budget-gate overhead + a different model (`Assistant:Agent:Model` vs the conversation's pinned model); falling back is the correct semantic.

### Model-selection trade-off

The agent path uses `Assistant:Agent:Model` (default `qwen2.5:72b` — function-calling-capable per Phase 3.A C3). The direct-LLM path uses the conversation's pinned `Model` column. **A single conversation may produce some turns from one model and other turns from another model**, depending on whether the user's turn triggers the agent path. v0 trade-off:
- Tool-using calls need a function-calling-capable model; the conversation's pinned model may not support it
- Per-conversation tool-model selection is Phase 3.D+ ergonomics
- Operators who want consistency can either pin the conversation Model to a tool-supporting model (which doesn't actually drive the agent-path call but documents intent), or set `Assistant:Agent:Model` to match the conversation's pinned model environment-wide

This is acceptable for v0 because the model choice is observable in `agent_runs.model_used` (Phase 3.A.1) — future audit can attribute behavior differences to model differences. Production deployments today use `qwen2.5:72b` for the agent-path model and `mistral-small:24b` as the per-conversation default; both run on GB10 without conflict.

## Q2 — System prompt wording (ratified)

Built from `AssistantAgentExecutorOptions.SystemPrompt` (operator-configurable base persona) + a dynamically-enumerated `Available tools:` section listing each resolvable tool's `Name: Description`. Each tool's `Description` already carries when-to-use guidance per Core's `AgentToolDescriptor.Description` docstring contract, so the catalogue section is just "Name: Description" lines with no extra editorial wrapper.

Example produced text:
```
You are an assistant with access to a small set of tools. When a tool is helpful, emit a tool_call. When you have a complete answer, return a plain assistant message with no tool_calls. Be concise and avoid invoking the same tool twice with identical arguments.

Available tools — use them when the user's request matches a tool's description:
- search_documents: Search the customer's indexed document corpus and return the top-K matching chunks. Use this when the user asks a question whose answer is likely in their documents...
- echo: Echo the supplied text back in a structured response. Use this when you want to confirm the agent loop is dispatching tools correctly...
```

**Token budget:** ~530 chars / ~135 tokens for the current 2-tool catalogue. Holds under hub's 200-token cap up to ~12 tools at ~140 chars per descriptor. Phase 3.D+ revisits when the catalogue grows past 5-7 tools (likely shift to "summary + dynamic tool-selection-by-name" pattern).

**Empty-catalogue handling:** when the resolvable list is empty (rare; reached only via the standalone `POST /api/agent-runs` endpoint with explicit empty `availableTools` — the conversation path's empty-catalogue degradation routes to direct-LLM before reaching here), the catalogue section is omitted entirely. The LLM gets just the base persona. This avoids confusing the model with `Available tools: (none)`.

## Q3 — EchoTool exposure: Option A (config flag, ratified)

`Assistant:Tools:ExposeEcho` boolean, default `false` (production-safe — EchoTool is a debugging aid, not a production tool). Test environments override to `true` via `AssistantWebApplicationFactory.ConfigureAppConfiguration`. Pattern matches `Assistant:AutoMigrate` + `Auth:RequireHttpsMetadata` surrounding conventions.

**Exposure semantics:** `ExposeEcho=false` removes EchoTool's descriptor from `IToolRegistry.Descriptors` (the LLM-visible catalogue) but `IToolRegistry.GetTool("echo")` still resolves the tool. Operators can still target EchoTool through the standalone `POST /api/agent-runs` endpoint with an explicit `toolNames: ["echo"]` filter — the executor's `ResolveDescriptors` resolves through the registry, dispatching unexposed tools when explicitly named. Exposure only gates LLM visibility, not dispatchability.

**Forward-flag:** if Phase 3.D+ adds 2+ more test-only tools (e.g., `EchoDelay`, `EchoError`), the per-tool `Expose<X>` config forest becomes a maintenance smell. At that point we revisit moving exposure onto the `IAgentTool` interface itself as `bool IsExposedByDefault` (the Option B from pre-scaffold ratify). For Phase 3.C with one test tool, the per-tool flag is the smaller diff + matches surrounding patterns.

## Q4 — Descriptor resolution (ratified)

`AssistantAgentExecutor.ResolveDescriptors` swaps caller-supplied descriptors (which may be orchestrator placeholders OR caller-controlled real descriptors from the standalone endpoint) for the registry's actual descriptor — keyed by `AgentToolDescriptor.Name`. The LLM always sees the real `Description` + real `ParameterSchema`. Unknown names get dropped silently (the planner can't dispatch what isn't resolvable; emitting an unknown-name tool_call would just surface as a Failed step inside the loop, so cleaner to never tell the planner about them).

## What this PR adds

**Production (~290 LoC):**
- `Trellis.Assistant/AgentExecution/ToolCatalogueOptions.cs` (new) — bound from `Assistant:Tools`, carries `ExposeEcho` boolean (default `false`).
- `Trellis.Assistant/AgentExecution/ToolRegistry.cs` — ctor now takes `IOptions<ToolCatalogueOptions>`; `Descriptors` filtered by `IsExposed` gate. `GetTool` unchanged (dispatchability preserved).
- `Trellis.Assistant/AgentExecution/EchoTool.cs` — adds `public const string ToolName = "echo"` for the registry's exposure gate to compare against without string-literal duplication.
- `Trellis.Assistant/AgentExecution/AssistantAgentExecutor.cs` — adds `ResolveDescriptors` (registry-keyed real-descriptor swap) + `BuildSystemPromptWithCatalogue` (base persona + per-tool enumeration). System prompt injected at `messages[0]` for BOTH `RunAsync` (replaces existing static system prompt) and `RunForConversationAsync` (prepended; previously absent). Adds three new LogInformation/LogWarning calls per tool dispatch (dispatched / succeeded+duration / failed+message).
- `Trellis.Assistant/Services/ConversationOrchestrator.cs` — adds `IToolRegistry _toolRegistry` ctor dep. Routing logic per Q1 Option A: null → agent path with `Registry.Descriptors`; empty → direct-LLM; non-empty → agent path with placeholder filter. Empty-catalogue degradation routes back to direct-LLM. `HandleAgentPathAsync` signature changed to take `IReadOnlyList<AgentToolDescriptor>` directly (no longer builds placeholders inside).
- `Trellis.Assistant/Program.cs` — `AddOptions<ToolCatalogueOptions>().Bind(...)`.
- `Trellis.Assistant/appsettings.json` — `Assistant:Tools:ExposeEcho = false`.

**Tests (~250 LoC; 10 new + 8 retrofits):**
- `ToolRegistryTests` (+3 pure pins): `ExposeEchoFalse_HidesEchoFromDescriptors_ButGetToolStillResolves`, `ExposeEchoTrue_IncludesEchoInDescriptors`, `NonEchoTools_AlwaysExposed_RegardlessOfExposeEchoFlag`.
- `AssistantAgentExecutorTests` (+4 Docker-gated pins): `SystemPrompt_StandalonePath_InjectedAtMessagesIndexZero_WithToolCatalogue`, `SystemPrompt_IncludesToolCatalogue_WithNamesAndDescriptions`, `SystemPrompt_NoResolvableTools_FallsBackToBaseSystemPromptOnly`, `DescriptorResolution_PlaceholderDescriptorSwappedForRegisteredOne`.
- `ConversationEndpointTests` (+3 Docker-gated pins): `PostTurns_NullToolsFilter_RoutesThroughAgentPath_WithFullExposedCatalogue`, `PostTurns_NullToolsFilter_DefaultRoute_LLMSeesSearchDocumentsInCatalogue` (hub's E2E "refund policy" example, stub LLM), `PostTurns_EmptyToolsFilter_OptsIntoDirectLlmPath`.
- `StubAgentLlmClient` extension — adds `FirstMessageContent` + `ToolDescriptions` to the recorded call shape so system-prompt + tool-array content assertions are clean.
- `ToolRegistryTests` retrofit — pre-existing 9 ctor sites updated to pass `_catalogueOptions`.
- `AssistantAgentExecutorTests` retrofit — `NewExecutor` helper updated to wire `ToolCatalogueOptions` with `ExposeEcho=true`.
- `ConversationEndpointTests` retrofit — 7 Phase 1+2 tests migrated to `Tools = Array.Empty<string>()` to explicitly opt into the direct-LLM path under the new routing default; `ToCoreRole_ToolTurn_MapsToChatRoleTool` assertion updated to acknowledge the new `messages[0]` system prompt.
- `AssistantWebApplicationFactory` — `Assistant:Tools:ExposeEcho = "true"` so test env exposes EchoTool.

## Operator logging surface

Per-dispatch lifecycle log emitted from `AssistantAgentExecutor.DispatchOneToolCallAsync`:
- `LogInformation` at dispatch start: `"AgentRun {RunId} step {StepIndex}: tool {Tool} dispatched."`
- `LogInformation` on success: `"AgentRun {RunId} step {StepIndex}: tool {Tool} succeeded in {DurationMs}ms."`
- `LogWarning` on failure (tool returned Success=false OR threw): `"AgentRun {RunId} step {StepIndex}: tool {Tool} failed in {DurationMs}ms: {ErrorMessage}"`

Counter-based metrics (Prometheus, OpenTelemetry) deferred to Phase 3.D per hub's ratify — journalctl-grep surfaces the needed operator signal for v0.

## Out of scope (defer)

- Multiple tools in one turn (parallel tool_calls) — protocol supports, defer pending real use-case (3.D+)
- Tool permission system (per-tenant tool exposure, role-gated tools) — depends on Macro 2 tenant_role claim (3.D+)
- Search result re-ranking / citation extraction in the second LLM turn — current shape "LLM sees raw chunks, decides what to use" is sufficient for v0
- Real-Ollama E2E in CI — the existing `OLLAMA_BASE_URL` gate is fine for ad-hoc smoke; not required for merge
- Prometheus / OpenTelemetry counters — 3.D scope
- Per-conversation tool-model selection — 3.D+ ergonomics; v0 lives with two-model semantics documented above

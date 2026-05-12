using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Trellis.Assistant.Data;
using Trellis.Core.Models;
using Trellis.Core.Services;

namespace Trellis.Assistant.AgentExecution;

/// <summary>
/// Phase 3.A.1 LLM-driven plan-then-execute loop. Implements
/// <see cref="IAgentExecutor"/> from Trellis.Core (Phase 0 PR #10).
/// Phase 3.A.2 adds <see cref="RunForConversationAsync"/> for the
/// conversation-integrated agent path. The DefaultBudgetGate retrofit
/// (post-Phase-3.A.2) replaced the placeholder <c>AssistantBudgetGate</c>
/// with <c>Trellis.Core.Services.DefaultBudgetGate</c> from qwen's
/// Phase A merge; the executor now injects Assistant's documented
/// <see cref="AssistantDefaultMaxSteps"/> default when callers don't
/// supply <see cref="AgentBudgetOverrides"/>, since DefaultBudgetGate's
/// own default is 1000 (Workflow's value, not Assistant's 25).
///
/// <para>
/// Two entry points:
/// <list type="bullet">
/// <item><see cref="RunAsync"/> — standalone POST /api/agent-runs surface
/// (Phase 3.A.1). Builds messages from system prompt + user prompt;
/// returns a terminal <see cref="AgentRun"/>.</item>
/// <item><see cref="RunForConversationAsync"/> — conversation-integrated
/// path invoked by <see cref="Services.ConversationOrchestrator"/> when
/// the request specifies a tools filter (Phase 3.A.2). Receives
/// pre-built messages from the orchestrator (system + history + new user
/// turn); returns a <see cref="ConversationAgentResult"/> carrying the
/// terminal AgentRun + the final assistant text + the per-dispatch
/// summaries the orchestrator persists as Role=Tool turns.</item>
/// </list>
/// </para>
///
/// <para>
/// Both entry points share <see cref="ExecuteLoopAsync"/> (private
/// helper) — the loop body, budget gate checks, tool dispatch, and
/// retry-on-malformed-output semantics are identical between the two
/// entry points; only the message-construction + return-type framing
/// differs.
/// </para>
///
/// <para>
/// Single-writer per run. The executor is the only writer to a given
/// <c>agent_run_id</c> + its child <c>agent_steps</c> rows; no advisory
/// lock is needed at the agent layer (the
/// <c>(agent_run_id, step_index)</c> unique constraint catches a
/// programming bug if a future executor accidentally double-dispatches).
/// </para>
///
/// <para>
/// Plan field: human-readable string per Core's docstring + Phase 3.A
/// C2 ratification. NOT structured JSON. Format:
/// <c>"LLM-driven plan; ≤{maxSteps} iterations against tools [{names}]; user prompt: {first200}"</c>.
/// </para>
///
/// <para>
/// Malformed model output (Phase 3.A Q5 ratification): if the model
/// returns tool_calls JSON we can't parse, the tool's RunAsync surfaces
/// a structured failure (<see cref="AgentStepStatus.Failed"/> with
/// <see cref="AgentStep.ErrorMessage"/>); the loop continues + the next
/// LLM iteration sees the failure in the message history. Phase 3.B
/// adds dedicated parse-failure detection at the executor layer.
/// </para>
/// </summary>
public sealed class AssistantAgentExecutor : IAgentExecutor
{
    private static readonly JsonSerializerOptions ToolResultJsonOpts = new()
    {
        WriteIndented = false,
    };

    /// <summary>
    /// Static fallback content the executor returns when the model emits
    /// unparseable tool_calls JSON. Pinned by
    /// <c>Plan_MalformedToolCallsJson_FallbackAssistantTurn_LogsFailedPlanStep</c>
    /// per the Phase 3.A Q5 ratification.
    /// </summary>
    public const string MalformedPlanFallbackText =
        "I couldn't reason through that — please rephrase.";

    /// <summary>
    /// Assistant's documented MaxSteps default (Phase 0 PR #10's
    /// docstring + Phase 3.A C4 ratification). Distinct from
    /// <see cref="DefaultBudgetGate.DefaultMaxSteps"/> (1000 — Workflow's
    /// for-each default). The executor injects this value when callers
    /// pass <c>BudgetOverrides=null</c>, satisfying Core's "callers
    /// override via AgentBudgetOverrides.MaxSteps" contract — the
    /// Assistant executor IS that consumer for all Assistant paths.
    /// Pinned by <c>Executor_BudgetOverridesNull_InjectsAssistantDefault25</c>.
    /// </summary>
    public const int AssistantDefaultMaxSteps = 25;

    private readonly IAgentRunStore _store;
    private readonly IAgentLlmClient _llm;
    private readonly IToolRegistry _tools;
    private readonly IAgentBudgetGate _gate;
    private readonly IJsonSchemaValidator _schemaValidator;
    private readonly AssistantAgentExecutorOptions _options;
    private readonly ILogger<AssistantAgentExecutor> _logger;

    public AssistantAgentExecutor(
        IAgentRunStore store,
        IAgentLlmClient llm,
        IToolRegistry tools,
        IAgentBudgetGate gate,
        IJsonSchemaValidator schemaValidator,
        IOptions<AssistantAgentExecutorOptions> options,
        ILogger<AssistantAgentExecutor> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _schemaValidator = schemaValidator ?? throw new ArgumentNullException(nameof(schemaValidator));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.Model);
        _logger = logger;
    }

    // ---------------- IAgentExecutor surface (Phase 3.A.1) ----------------

    public async Task<AgentRun> RunAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var startedAt = DateTime.UtcNow;
        var effectiveOverrides = WithAssistantDefaults(request.BudgetOverrides);
        var maxSteps = effectiveOverrides.MaxSteps ?? AssistantDefaultMaxSteps;

        // Resolve the request's tool catalogue against the registry —
        // Phase 3.C swaps placeholder descriptors (orchestrator-supplied
        // for the conversation path) for the real ones the registered
        // tool exposes. The LLM sees actual descriptions + schemas; the
        // system prompt's tool-catalogue section enumerates these.
        // Unknown names get dropped (the planner can't call what isn't
        // resolvable).
        var resolvableTools = ResolveDescriptors(request.AvailableTools);
        var planSummary = BuildPlanSummary(request.UserPrompt, maxSteps, resolvableTools);

        var runId = await PersistInitialRunAsync(
            request.OrgId, request.AssistantTurnId, planSummary, startedAt).ConfigureAwait(false);

        var messages = new List<ChatMessage>(8)
        {
            new() { Role = ChatRole.System, Content = BuildSystemPromptWithCatalogue(resolvableTools) },
            new() { Role = ChatRole.User, Content = request.UserPrompt },
        };

        var loopResult = await ExecuteLoopAsync(
            runId, request.OrgId, startedAt, messages, resolvableTools,
            effectiveOverrides, cancellationToken).ConfigureAwait(false);

        return await PersistTerminalStateAsync(
            request.OrgId, runId, loopResult).ConfigureAwait(false);
    }

    /// <summary>
    /// Inject Assistant's documented defaults onto a caller-supplied
    /// (or null) <see cref="AgentBudgetOverrides"/>. Single source of
    /// truth for the per-surface defaults the Core docstring on
    /// <see cref="AgentBudgetOverrides"/> says callers should provide
    /// (Assistant 25 / Workflow 1000). The Workflow surface injects
    /// 1000 elsewhere (their own executor); this method covers all
    /// Assistant entry points.
    ///
    /// <para>
    /// When the caller supplies a non-null overrides record, only fill
    /// in MaxSteps if it's null on the caller's record. Caller's
    /// MaxRunDuration override (if any) propagates verbatim.
    /// </para>
    /// </summary>
    private static AgentBudgetOverrides WithAssistantDefaults(AgentBudgetOverrides? caller)
    {
        return new AgentBudgetOverrides
        {
            MaxSteps = caller?.MaxSteps ?? AssistantDefaultMaxSteps,
            MaxRunDuration = caller?.MaxRunDuration,  // null → DefaultBudgetGate uses its 10-min default
        };
    }

    // ---------------- Conversation-integrated surface (Phase 3.A.2) ----------------

    /// <summary>
    /// Run the agentic loop with caller-supplied conversation history as
    /// the LLM context. Used by
    /// <see cref="Services.ConversationOrchestrator"/> to route
    /// <c>POST /api/conversations/{id}/turns</c> requests through the
    /// executor when the request body specifies a tools filter.
    ///
    /// <para>
    /// The orchestrator is responsible for translating prior conversation
    /// turns into <see cref="ChatMessage"/> entries (including any prior
    /// Role=Tool turns rendered as role=tool messages with
    /// tool_call_id/tool_name attached). The executor prepends the
    /// system prompt + runs the loop; the returned
    /// <see cref="ConversationAgentResult.ToolDispatches"/> list carries
    /// the data the orchestrator needs to persist Role=Tool turns
    /// inline with the final Role=Assistant turn.
    /// </para>
    ///
    /// <para>
    /// Distinct from <see cref="RunAsync"/>: the standalone surface
    /// builds a fresh user prompt + returns just the terminal AgentRun;
    /// this method takes pre-built messages + returns the rich shape
    /// the orchestrator needs. Both go through
    /// <see cref="ExecuteLoopAsync"/> for the loop body.
    /// </para>
    /// </summary>
    /// <param name="orgId">Multi-tenant scope (Guid.Parse'd from the
    /// JWT tenant claim by the orchestrator's caller layer per Phase
    /// 3.A C1).</param>
    /// <param name="assistantTurnId">Optional cross-link to the
    /// <see cref="AssistantTurn"/> that kicked off this run. The
    /// orchestrator sets this to the ID of the user turn it's about to
    /// persist (the trigger), or null if the orchestrator hasn't yet
    /// allocated turn IDs.</param>
    /// <param name="userPrompt">The new user message text — used for
    /// the <see cref="AgentRun.Plan"/> human-readable summary; the
    /// message itself is included in <paramref name="messages"/>.</param>
    /// <param name="messages">Pre-built chat history including system
    /// prompt context (orchestrator's responsibility) + prior turns
    /// + the new user message at the end. The executor mutates this
    /// list as the loop runs (appending tool results between iterations);
    /// callers that need an immutable copy should clone before
    /// passing in.</param>
    public async Task<ConversationAgentResult> RunForConversationAsync(
        Guid orgId,
        Guid? assistantTurnId,
        string userPrompt,
        List<ChatMessage> messages,
        IReadOnlyList<AgentToolDescriptor> availableTools,
        AgentBudgetOverrides? budgetOverrides,
        CancellationToken cancellationToken = default)
    {
        if (orgId == Guid.Empty)
        {
            throw new ArgumentException(
                "OrgId must be non-empty (matches AgentRunRequest.Create's contract).",
                nameof(orgId));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(userPrompt);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(availableTools);

        var startedAt = DateTime.UtcNow;
        var effectiveOverrides = WithAssistantDefaults(budgetOverrides);
        var maxSteps = effectiveOverrides.MaxSteps ?? AssistantDefaultMaxSteps;

        var resolvableTools = ResolveDescriptors(availableTools);
        var planSummary = BuildPlanSummary(userPrompt, maxSteps, resolvableTools);

        var runId = await PersistInitialRunAsync(
            orgId, assistantTurnId, planSummary, startedAt).ConfigureAwait(false);

        // Phase 3.C: prepend the tool-aware system prompt to the
        // conversation-path messages list. Phase 3.A.2 left this gap —
        // the orchestrator built [...prior, user] but never injected a
        // system prompt, so the LLM had no persona + no tool catalogue.
        // Insert at index 0 so prior history (already in role order)
        // follows naturally.
        messages.Insert(0, new ChatMessage
        {
            Role = ChatRole.System,
            Content = BuildSystemPromptWithCatalogue(resolvableTools),
        });

        var loopResult = await ExecuteLoopAsync(
            runId, orgId, startedAt, messages, resolvableTools,
            effectiveOverrides, cancellationToken).ConfigureAwait(false);

        var completedRun = await PersistTerminalStateAsync(
            orgId, runId, loopResult).ConfigureAwait(false);

        return new ConversationAgentResult
        {
            AgentRun = completedRun,
            FinalAssistantText = loopResult.FinalAssistantText,
            ToolDispatches = loopResult.ToolDispatches,
        };
    }

    // ---------------- Shared helpers ----------------

    private async Task<Guid> PersistInitialRunAsync(
        Guid orgId, Guid? assistantTurnId, string planSummary, DateTime startedAt)
    {
        // CT.None on the initial create: the run record is the audit
        // log + a pre-cancelled token would otherwise prevent the row
        // from ever existing, which means CompleteRunAsync below would
        // have nothing to update on the cancellation path.
        var runId = Ulid.NewUlid().ToGuid();
        var initialRun = new AgentRun
        {
            Id = runId,
            OrgId = orgId,
            AssistantTurnId = assistantTurnId,
            Plan = planSummary,
            Status = AgentRunStatus.Planning,
            StartedAt = startedAt,
            CompletedAt = null,
            ArchivedAt = null,
            Steps = Array.Empty<AgentStep>(),
            TokensUsed = 0,
        };
        await _store.CreateRunAsync(initialRun, CancellationToken.None).ConfigureAwait(false);
        return runId;
    }

    private async Task<AgentRun> PersistTerminalStateAsync(
        Guid orgId, Guid runId, LoopResult loopResult)
    {
        // CT.None on the terminal write — completion writes need to
        // succeed even on cancellation so the run record reflects reality.
        return await _store
            .CompleteRunAsync(
                orgId,
                runId,
                loopResult.TerminalStatus,
                DateTime.UtcNow,
                loopResult.TotalTokens,
                loopResult.TerminalErrorMessage,
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Shared loop body. Receives the pre-built messages list +
    /// resolvable tools + budget overrides; runs the
    /// budget-gate→LLM→tool-dispatch loop until the model emits a
    /// non-tool-call assistant turn, the gate halts, or cancellation
    /// fires. Returns a <see cref="LoopResult"/> with everything both
    /// public entry points need to assemble their respective return
    /// shapes.
    /// </summary>
    private async Task<LoopResult> ExecuteLoopAsync(
        Guid runId,
        Guid orgId,
        DateTime startedAt,
        List<ChatMessage> messages,
        IReadOnlyList<AgentToolDescriptor> resolvableTools,
        AgentBudgetOverrides? budgetOverrides,
        CancellationToken cancellationToken)
    {
        var recentDispatches = new List<AgentRunStateToolDispatch>();
        var toolDispatches = new List<ConversationAgentToolDispatch>();
        var totalTokens = 0L;
        var stepIndex = 0;
        var terminalStatus = AgentRunStatus.Running;
        string? terminalErrorMessage = null;
        var finalAssistantText = "";

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // ---- Budget gate check (pre-dispatch) ----
                var state = new AgentRunState
                {
                    AgentRunId = runId,
                    OrgId = orgId,
                    StartedAt = startedAt,
                    CheckedAt = DateTime.UtcNow,
                    CompletedStepCount = stepIndex,
                    ToolCallsInCurrentStep = 0,
                    RecentToolDispatches = recentDispatches.ToList(),
                    Overrides = budgetOverrides,
                };
                var verdict = await _gate
                    .ShouldContinueAsync(state, cancellationToken)
                    .ConfigureAwait(false);
                if (verdict.Decision != BudgetDecision.Continue)
                {
                    terminalStatus = MapBudgetDecisionToRunStatus(verdict.Decision);
                    terminalErrorMessage = verdict.HumanReadableReason;
                    _logger.LogInformation(
                        "AgentRun {RunId} halted by budget gate: {Decision} — {Reason}",
                        runId, verdict.Decision, verdict.HumanReadableReason);
                    break;
                }

                // ---- Call LLM ----
                AgentLlmResponse llmResponse;
                try
                {
                    llmResponse = await _llm
                        .ChatWithToolsAsync(
                            _options.Model,
                            messages,
                            resolvableTools,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    terminalStatus = AgentRunStatus.Failed;
                    terminalErrorMessage = $"LLM call failed: {ex.Message}";
                    _logger.LogWarning(ex,
                        "AgentRun {RunId} LLM call threw — terminating with Failed.", runId);
                    break;
                }
                totalTokens += llmResponse.TokensUsed;

                // ---- No tool_calls = final assistant turn = success ----
                if (llmResponse.ToolCalls.Count == 0)
                {
                    _logger.LogInformation(
                        "AgentRun {RunId} succeeded after {StepCount} step(s); model emitted final assistant text.",
                        runId, stepIndex);
                    terminalStatus = AgentRunStatus.Succeeded;
                    finalAssistantText = llmResponse.AssistantText ?? "";
                    break;
                }

                // ---- Dispatch tool calls (sequentially, one per step
                // per Core's v0 cap of 1 tool call per step) ----
                foreach (var call in llmResponse.ToolCalls)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var canonicalArgs = CanonicalizeJson(call.ArgumentsJson);
                    var step = await DispatchOneToolCallAsync(
                        runId, orgId, stepIndex, call, canonicalArgs, cancellationToken)
                        .ConfigureAwait(false);

                    recentDispatches.Add(new AgentRunStateToolDispatch
                    {
                        ToolName = call.ToolName,
                        ParametersJson = canonicalArgs,
                    });

                    // Phase 3.A.2: capture the per-dispatch summary the
                    // ConversationOrchestrator persists as a Role=Tool
                    // turn. ToolCallId is the Ulid-stringified AgentStep.Id;
                    // ResultContent is the tool's output JSON (success)
                    // or a JSON-serialized error envelope (failure).
                    var dispatchResultContent = step.Status == AgentStepStatus.Succeeded
                        ? step.ToolOutputJson ?? "{}"
                        : JsonSerializer.Serialize(
                            new { error = step.ErrorMessage ?? "tool failed" },
                            ToolResultJsonOpts);
                    toolDispatches.Add(new ConversationAgentToolDispatch
                    {
                        ToolCallId = new Ulid(step.Id).ToString(),
                        ToolName = call.ToolName,
                        ResultContent = dispatchResultContent,
                    });

                    stepIndex++;

                    // Append tool result back into chat history so the
                    // next LLM iteration sees it. Phase 3.A.2: still
                    // rendered as role=System with a TOOL_RESULT envelope
                    // because the in-flight `messages` list isn't the
                    // persisted-turns shape — it's the LLM context. Tool
                    // turns persist separately when the orchestrator
                    // calls AppendTurnsAsync after the loop.
                    var toolResultPayload = new
                    {
                        tool = call.ToolName,
                        success = step.Status == AgentStepStatus.Succeeded,
                        result = step.ToolOutputJson,
                        error = step.ErrorMessage,
                    };
                    messages.Add(new ChatMessage
                    {
                        Role = ChatRole.System,
                        Content = "TOOL_RESULT: " +
                            JsonSerializer.Serialize(toolResultPayload, ToolResultJsonOpts),
                    });
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            terminalStatus = AgentRunStatus.Cancelled;
            terminalErrorMessage = "Cancelled by caller.";
            _logger.LogInformation(
                "AgentRun {RunId} cancelled at step {StepCount}.", runId, stepIndex);
        }

        return new LoopResult
        {
            TerminalStatus = terminalStatus,
            TerminalErrorMessage = terminalErrorMessage,
            TotalTokens = totalTokens,
            StepCount = stepIndex,
            FinalAssistantText = finalAssistantText,
            ToolDispatches = toolDispatches,
        };
    }

    /// <summary>
    /// Dispatch a single tool call. Persists an <see cref="AgentStep"/>
    /// with the appropriate terminal status. Honors Core's contract:
    /// tool throws → step Failed with a generic message; tool returns
    /// Success=false → step Failed with the tool's ErrorMessage; tool
    /// returns Success=true → step Succeeded with the tool's ResultJson.
    /// </summary>
    private async Task<AgentStep> DispatchOneToolCallAsync(
        Guid runId,
        Guid orgId,
        int stepIndex,
        AgentLlmToolCall call,
        string canonicalArgs,
        CancellationToken cancellationToken)
    {
        var stepStartedAt = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();

        // Phase 3.C: operator-visible per-dispatch lifecycle log. Pairs
        // with the succeeded/failed log below to surface tool latency +
        // outcome in journalctl. Counter-based metrics (Prometheus,
        // OpenTelemetry) deferred to Phase 3.D per hub's ratify.
        _logger.LogInformation(
            "AgentRun {RunId} step {StepIndex}: tool {Tool} dispatched.",
            runId, stepIndex, call.ToolName);

        var tool = _tools.GetTool(call.ToolName);
        if (tool is null)
        {
            sw.Stop();
            var step = new AgentStep
            {
                Id = Ulid.NewUlid().ToGuid(),
                AgentRunId = runId,
                StepIndex = stepIndex,
                ToolName = call.ToolName,
                ToolInputJson = canonicalArgs,
                ToolOutputJson = null,
                Status = AgentStepStatus.Failed,
                StartedAt = stepStartedAt,
                CompletedAt = DateTime.UtcNow,
                ErrorMessage = $"Tool '{call.ToolName}' is not registered.",
                DurationMs = sw.ElapsedMilliseconds,
            };
            return await _store.AppendStepAsync(orgId, step, cancellationToken).ConfigureAwait(false);
        }

        // Phase 3.B turn-on: schema-validate args against the tool's
        // declared ParameterSchema BEFORE dispatch (Phase 0 contract).
        // Invalid args → AgentStep.Failed with a structured error the
        // LLM can read in history + retry against. Decide-and-document #4
        // REJECT semantics: validation failures are LLM-retry-recoverable,
        // not operator-alert-worthy — Information-level logging only.
        var schemaResult = _schemaValidator.Validate(tool.Descriptor.ParameterSchema, canonicalArgs);
        if (!schemaResult.IsValid)
        {
            sw.Stop();
            var schemaErrorMessage = $"Tool '{call.ToolName}': schema validation failed: {schemaResult.FirstError ?? "unknown error"}";
            _logger.LogInformation(
                "AgentRun {RunId} step {StepIndex}: schema validation rejected {Tool} args — {Reason}",
                runId, stepIndex, call.ToolName, schemaResult.FirstError);
            var step = new AgentStep
            {
                Id = Ulid.NewUlid().ToGuid(),
                AgentRunId = runId,
                StepIndex = stepIndex,
                ToolName = call.ToolName,
                ToolInputJson = canonicalArgs,
                ToolOutputJson = null,
                Status = AgentStepStatus.Failed,
                StartedAt = stepStartedAt,
                CompletedAt = DateTime.UtcNow,
                ErrorMessage = schemaErrorMessage,
                DurationMs = sw.ElapsedMilliseconds,
            };
            return await _store.AppendStepAsync(orgId, step, cancellationToken).ConfigureAwait(false);
        }

        AgentToolOutput? output = null;
        string? errorMessage = null;
        bool threw = false;
        try
        {
            output = await tool
                .RunAsync(
                    new AgentToolInput
                    {
                        AgentRunId = runId,
                        StepIndex = stepIndex,
                        ParametersJson = canonicalArgs,
                        OrgId = orgId,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            errorMessage = "Tool dispatch cancelled mid-execution.";
            threw = true;
        }
        catch (Exception ex)
        {
            sw.Stop();
            errorMessage = $"Tool '{call.ToolName}' threw: {ex.Message}";
            threw = true;
            _logger.LogWarning(ex,
                "AgentRun {RunId} step {StepIndex}: tool {Tool} threw.",
                runId, stepIndex, call.ToolName);
        }
        sw.Stop();

        AgentStepStatus status;
        string? resultJson = null;
        if (threw)
        {
            status = AgentStepStatus.Failed;
        }
        else if (output is null || !output.Success)
        {
            status = AgentStepStatus.Failed;
            errorMessage = output?.ErrorMessage ?? $"Tool '{call.ToolName}' returned no structured output.";
            resultJson = output?.ResultJson;
        }
        else
        {
            status = AgentStepStatus.Succeeded;
            resultJson = output.ResultJson;
        }

        var persistedStep = new AgentStep
        {
            Id = Ulid.NewUlid().ToGuid(),
            AgentRunId = runId,
            StepIndex = stepIndex,
            ToolName = call.ToolName,
            ToolInputJson = canonicalArgs,
            ToolOutputJson = resultJson,
            Status = status,
            StartedAt = stepStartedAt,
            CompletedAt = DateTime.UtcNow,
            ErrorMessage = errorMessage,
            DurationMs = sw.ElapsedMilliseconds,
        };

        // Phase 3.C: operator-visible per-dispatch outcome log.
        // Information on success (with latency); Information on
        // cancellation (user-disconnect / executor-timeout; not an
        // operator-actionable failure); Warning on real failure (tool
        // threw OR returned Success=false). PR #10 review Blocker 2:
        // cancellation used to fire LogWarning, which would flood
        // journalctl with noise on every user disconnect and mask real
        // tool failures. The OCE-suppression pattern matches Macro 2 PR
        // 6.7's `when (ex is not OperationCanceledException)` filter
        // convention.
        if (status == AgentStepStatus.Succeeded)
        {
            _logger.LogInformation(
                "AgentRun {RunId} step {StepIndex}: tool {Tool} succeeded in {DurationMs}ms.",
                runId, stepIndex, call.ToolName, sw.ElapsedMilliseconds);
        }
        else if (cancellationToken.IsCancellationRequested && threw)
        {
            _logger.LogInformation(
                "AgentRun {RunId} step {StepIndex}: tool {Tool} cancelled after {DurationMs}ms.",
                runId, stepIndex, call.ToolName, sw.ElapsedMilliseconds);
        }
        else
        {
            _logger.LogWarning(
                "AgentRun {RunId} step {StepIndex}: tool {Tool} failed in {DurationMs}ms: {ErrorMessage}",
                runId, stepIndex, call.ToolName, sw.ElapsedMilliseconds, errorMessage ?? "(no message)");
        }

        return await _store
            .AppendStepAsync(orgId, persistedStep, threw && cancellationToken.IsCancellationRequested
                ? CancellationToken.None
                : cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Phase 3.C: resolve a caller-supplied descriptor list against the
    /// registry. The conversation orchestrator's
    /// <c>BuildToolCatalogue</c> emits placeholder descriptors (Name is
    /// real, Description + ParameterSchema are placeholders); the
    /// standalone <c>POST /api/agent-runs</c> caller may supply real or
    /// placeholder descriptors. Either way, the registry's actual
    /// descriptor is what the LLM should see — that's what carries the
    /// real description text + the real ParameterSchema.
    ///
    /// <para>
    /// Unknown names (descriptor refers to a tool not in the registry)
    /// are dropped silently — the planner can't dispatch what isn't
    /// resolvable, and emitting an unknown-name tool_call would just
    /// surface as a Failed step inside the loop. Cleaner to never tell
    /// the planner about them.
    /// </para>
    /// </summary>
    private IReadOnlyList<AgentToolDescriptor> ResolveDescriptors(
        IReadOnlyList<AgentToolDescriptor> requested)
    {
        var resolved = new List<AgentToolDescriptor>(requested.Count);
        foreach (var requestedDescriptor in requested)
        {
            var tool = _tools.GetTool(requestedDescriptor.Name);
            if (tool is null)
            {
                continue;
            }
            resolved.Add(tool.Descriptor);
        }
        return resolved;
    }

    /// <summary>
    /// Phase 3.C: compose the system prompt sent to the LLM as
    /// <see cref="ChatRole.System"/> at the top of every loop iteration.
    /// Built from <see cref="AssistantAgentExecutorOptions.SystemPrompt"/>
    /// (the base persona / behavior text; operator-configurable via
    /// <c>Assistant:Agent:SystemPrompt</c>) plus a dynamically-enumerated
    /// catalogue of the resolvable tools — each tool's
    /// <see cref="AgentToolDescriptor.Description"/> already carries
    /// when-to-use guidance per Core's
    /// <see cref="AgentToolDescriptor.Description"/> docstring contract,
    /// so the catalogue section is "Name: Description" lines, no extra
    /// editorial.
    ///
    /// <para>
    /// When the resolvable set is empty (e.g., a caller explicitly
    /// passed no tools — direct-LLM path doesn't reach here, but the
    /// standalone agent-runs endpoint can land here with an empty
    /// AvailableTools list), the catalogue section is omitted entirely;
    /// the LLM gets just the base persona. This avoids a confusing
    /// "Available tools: (none)" line that might make the model wonder
    /// what it's supposed to do.
    /// </para>
    ///
    /// <para>
    /// Token budget: ~530 chars / ~135 tokens for the current 2-tool
    /// catalogue (SearchDocumentsTool + EchoTool). Holds under hub's
    /// 200-token cap up to ~12 tools at ~140 chars per tool descriptor.
    /// Phase 3.D+ revisits if the catalogue grows past 5-7 tools, at
    /// which point a "summary + dynamic tool-selection" pattern may
    /// replace the full enumeration.
    /// </para>
    /// </summary>
    private string BuildSystemPromptWithCatalogue(IReadOnlyList<AgentToolDescriptor> tools)
    {
        if (tools.Count == 0)
        {
            return _options.SystemPrompt;
        }

        var sb = new System.Text.StringBuilder(_options.SystemPrompt.Length + 128 * tools.Count);
        sb.Append(_options.SystemPrompt);
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("Available tools — use them when the user's request matches a tool's description:");
        foreach (var tool in tools)
        {
            sb.Append("- ");
            sb.Append(tool.Name);
            sb.Append(": ");
            sb.AppendLine(tool.Description);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Build the human-readable Plan string per the Phase 3.A C2
    /// ratification. NOT structured JSON. User prompt is truncated to
    /// 200 chars to keep the row compact.
    /// </summary>
    private static string BuildPlanSummary(
        string userPrompt,
        int maxSteps,
        IReadOnlyList<AgentToolDescriptor> resolvableTools)
    {
        var toolNames = resolvableTools.Count == 0
            ? "(none)"
            : "[" + string.Join(", ", resolvableTools.Select(d => d.Name)) + "]";
        var truncatedPrompt = userPrompt.Length > 200
            ? userPrompt[..200] + "..."
            : userPrompt;
        return $"LLM-driven plan; ≤{maxSteps} iterations against tools {toolNames}; user prompt: {truncatedPrompt}";
    }

    private static readonly JsonSerializerOptions CanonicalizeJsonOpts = new()
    {
        WriteIndented = false,
    };

    /// <summary>
    /// Canonicalize a JSON string by parsing + re-serializing through
    /// <see cref="JsonSerializer.Serialize{T}(T,JsonSerializerOptions)"/>.
    /// Used for loop-detection identity per
    /// <see cref="AgentRunStateToolDispatch"/>'s docstring.
    /// </summary>
    private static string CanonicalizeJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "{}";
        }
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return JsonSerializer.Serialize(doc.RootElement, CanonicalizeJsonOpts);
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    /// <summary>
    /// Map <see cref="BudgetDecision"/> to the corresponding terminal
    /// <see cref="AgentRunStatus"/>. Per Core docstring on
    /// <see cref="BudgetDecision.TimedOut"/>: time-cap maps to
    /// CapReached on the run record (the gate's reason string
    /// distinguishes time-cap vs step-cap).
    /// </summary>
    private static AgentRunStatus MapBudgetDecisionToRunStatus(BudgetDecision decision) => decision switch
    {
        BudgetDecision.StepCapReached => AgentRunStatus.CapReached,
        BudgetDecision.TimedOut => AgentRunStatus.CapReached,
        BudgetDecision.ToolCallCapReached => AgentRunStatus.CapReached,
        BudgetDecision.LoopDetected => AgentRunStatus.LoopDetected,
        BudgetDecision.Continue => throw new InvalidOperationException(
            "Continue is not a terminal decision; should not reach status mapping."),
        _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, "Unknown BudgetDecision"),
    };

    /// <summary>
    /// Internal shape returned by <see cref="ExecuteLoopAsync"/> — both
    /// public entry points assemble their respective return types from
    /// this.
    /// </summary>
    private sealed record LoopResult
    {
        public required AgentRunStatus TerminalStatus { get; init; }
        public string? TerminalErrorMessage { get; init; }
        public required long TotalTokens { get; init; }
        public required int StepCount { get; init; }
        public required string FinalAssistantText { get; init; }
        public required IReadOnlyList<ConversationAgentToolDispatch> ToolDispatches { get; init; }
    }
}

/// <summary>
/// Configuration bound from <c>Assistant:Agent:*</c> in appsettings.
///
/// <see cref="Model"/> is the LLM tag passed to
/// <see cref="IAgentLlmClient.ChatWithToolsAsync"/> for every iteration
/// of the loop. Phase 3.A.1 default is <c>qwen2.5:72b</c> (per the
/// Phase 3.A C3 ratification: known tool-supporting model on GB10;
/// distinct from Phase 2's per-conversation model column which still
/// drives non-tool-using turns).
///
/// <see cref="SystemPrompt"/> is the role=system message prepended to
/// every loop. Default conveys the v0 contract to the model: "you have
/// a small set of tools, dispatch them when useful, return a plain
/// assistant message when you have a complete answer."
/// </summary>
public sealed class AssistantAgentExecutorOptions
{
    public const string SectionName = "Assistant:Agent";

    public string Model { get; set; } = "qwen2.5:72b";

    public string SystemPrompt { get; set; } =
        "You are an assistant with access to a small set of tools. " +
        "When a tool is helpful, emit a tool_call. When you have a complete " +
        "answer, return a plain assistant message with no tool_calls. " +
        "Be concise and avoid invoking the same tool twice with identical arguments.";
}

/// <summary>
/// Phase 3.A.2 result type for
/// <see cref="AssistantAgentExecutor.RunForConversationAsync"/>. Carries
/// the terminal AgentRun (audit) + the final assistant text + the
/// per-dispatch summaries the orchestrator persists as Role=Tool turns.
/// </summary>
public sealed record ConversationAgentResult
{
    public required AgentRun AgentRun { get; init; }
    public required string FinalAssistantText { get; init; }
    public required IReadOnlyList<ConversationAgentToolDispatch> ToolDispatches { get; init; }
}

/// <summary>
/// One tool dispatch as the conversation orchestrator needs it: the
/// stable tool-call id (Ulid-stringified <see cref="AgentStep.Id"/>),
/// the tool name, and the result content (tool's output JSON on success
/// or a JSON-serialized <c>{error: "..."}</c> envelope on failure).
/// </summary>
public sealed record ConversationAgentToolDispatch
{
    public required string ToolCallId { get; init; }
    public required string ToolName { get; init; }
    public required string ResultContent { get; init; }
}

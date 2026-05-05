using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Trellis.Assistant.Data;
using Trellis.Core.Models;
using Trellis.Core.Services;

namespace Trellis.Assistant.AgentExecution;

/// <summary>
/// Phase 3.A.1 LLM-driven plan-then-execute loop. Implements
/// <see cref="IAgentExecutor"/> from Trellis.Core (Phase 0 PR #10).
///
/// <para>
/// Loop shape: receive <see cref="AgentRunRequest"/> → persist a fresh
/// <see cref="AgentRun"/> in <see cref="AgentRunStatus.Planning"/> →
/// build initial chat history → loop {gate.ShouldContinueAsync →
/// llm.ChatWithToolsAsync → for each emitted tool_call: look up tool,
/// run it, persist a step} → terminate when the model stops emitting
/// tool_calls (success), the gate halts (cap_reached / loop_detected /
/// timed_out), or the caller cancels.
/// </para>
///
/// <para>
/// Single-writer per run. The executor is the only writer to a given
/// <c>agent_run_id</c> + its child <c>agent_steps</c> rows; no
/// advisory lock is needed at the agent layer (the
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
/// returns tool_calls JSON we can't parse, emit a single
/// <see cref="AgentStepStatus.Failed"/> step + a final fallback
/// assistant text + terminate the run with
/// <see cref="AgentRunStatus.Failed"/>. Retry-with-clarifying-prompt
/// is a Phase 3.B nice-to-have.
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

    private readonly IAgentRunStore _store;
    private readonly IAgentLlmClient _llm;
    private readonly IToolRegistry _tools;
    private readonly IAgentBudgetGate _gate;
    private readonly AssistantAgentExecutorOptions _options;
    private readonly ILogger<AssistantAgentExecutor> _logger;

    public AssistantAgentExecutor(
        IAgentRunStore store,
        IAgentLlmClient llm,
        IToolRegistry tools,
        IAgentBudgetGate gate,
        IOptions<AssistantAgentExecutorOptions> options,
        ILogger<AssistantAgentExecutor> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.Model);
        _logger = logger;
    }

    public async Task<AgentRun> RunAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var startedAt = DateTime.UtcNow;
        var maxSteps = request.BudgetOverrides?.MaxSteps ?? AssistantBudgetGate.DefaultMaxSteps;

        // Filter the request's tool catalogue to those we actually have
        // registered. The planner sees only resolvable tools; an
        // emitted tool_call for a name not in the registry surfaces
        // as a Failed step (see DispatchOneToolCallAsync below).
        var resolvableTools = request.AvailableTools
            .Where(d => _tools.GetTool(d.Name) is not null)
            .ToList();

        var planSummary = BuildPlanSummary(request, maxSteps, resolvableTools);

        // ---- Persist the initial run row ----
        // CT.None on the initial create: the run record is the audit
        // log + a pre-cancelled token would otherwise prevent the row
        // from ever existing, which means CompleteRunAsync below would
        // have nothing to update on the cancellation path. Same reason
        // CompleteRunAsync at the bottom uses CT.None — terminal state
        // writes must succeed even on cancellation.
        var runId = Ulid.NewUlid().ToGuid();
        var initialRun = new AgentRun
        {
            Id = runId,
            OrgId = request.OrgId,
            AssistantTurnId = request.AssistantTurnId,
            Plan = planSummary,
            Status = AgentRunStatus.Planning,
            StartedAt = startedAt,
            CompletedAt = null,
            ArchivedAt = null,
            Steps = Array.Empty<AgentStep>(),
            TokensUsed = 0,
        };
        await _store.CreateRunAsync(initialRun, CancellationToken.None).ConfigureAwait(false);

        // ---- Loop ----
        var messages = new List<ChatMessage>(8)
        {
            new() { Role = ChatRole.System, Content = _options.SystemPrompt },
            new() { Role = ChatRole.User, Content = request.UserPrompt },
        };
        var recentDispatches = new List<AgentRunStateToolDispatch>();
        var totalTokens = 0L;
        var stepIndex = 0;
        var terminalStatus = AgentRunStatus.Running;
        string? terminalErrorMessage = null;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // ---- Budget gate check (pre-dispatch) ----
                var state = new AgentRunState
                {
                    AgentRunId = runId,
                    OrgId = request.OrgId,
                    StartedAt = startedAt,
                    CheckedAt = DateTime.UtcNow,
                    CompletedStepCount = stepIndex,
                    ToolCallsInCurrentStep = 0,
                    RecentToolDispatches = recentDispatches.ToList(),
                    Overrides = request.BudgetOverrides,
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
                    break;
                }

                // ---- Dispatch tool calls (sequentially, one per step
                // per Core's v0 cap of 1 tool call per step) ----
                foreach (var call in llmResponse.ToolCalls)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var canonicalArgs = CanonicalizeJson(call.ArgumentsJson);
                    var step = await DispatchOneToolCallAsync(
                        runId, request.OrgId, stepIndex, call, canonicalArgs, cancellationToken)
                        .ConfigureAwait(false);

                    recentDispatches.Add(new AgentRunStateToolDispatch
                    {
                        ToolName = call.ToolName,
                        ParametersJson = canonicalArgs,
                    });
                    stepIndex++;

                    // Append the tool result back into chat history so
                    // the next LLM call sees it. We render as a system
                    // role with a structured result envelope — Phase 3.A.1
                    // doesn't have the Tool role on ChatMessage yet (that
                    // requires the Phase 3.A.2 schema widening + Core PR).
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

        // ---- Persist terminal state ----
        // Disconnect from the caller's CT — completion writes need to
        // succeed even on cancellation so the run record reflects reality.
        var completedRun = await _store
            .CompleteRunAsync(
                request.OrgId,
                runId,
                terminalStatus,
                DateTime.UtcNow,
                totalTokens,
                terminalErrorMessage,
                CancellationToken.None)
            .ConfigureAwait(false);
        return completedRun;
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
            // Bubble cancellation up to the executor's main loop; the
            // step record reflects the unfinished dispatch.
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

        // CT.None for persistence — see RunAsync's terminal-state write
        // for the same reasoning.
        return await _store
            .AppendStepAsync(orgId, persistedStep, threw && cancellationToken.IsCancellationRequested
                ? CancellationToken.None
                : cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Build the human-readable Plan string per the Phase 3.A C2
    /// ratification. NOT structured JSON. Format reads naturally in
    /// operator-tail journalctl output. User prompt is truncated to
    /// 200 chars to keep the row compact.
    /// </summary>
    private static string BuildPlanSummary(
        AgentRunRequest request,
        int maxSteps,
        IReadOnlyList<AgentToolDescriptor> resolvableTools)
    {
        var toolNames = resolvableTools.Count == 0
            ? "(none)"
            : "[" + string.Join(", ", resolvableTools.Select(d => d.Name)) + "]";
        var truncatedPrompt = request.UserPrompt.Length > 200
            ? request.UserPrompt[..200] + "..."
            : request.UserPrompt;
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
    ///
    /// <para>
    /// <see cref="JsonElement.GetRawText"/> preserves the original input
    /// whitespace (it returns a slice of the source bytes), so it does
    /// NOT canonicalize. The <see cref="JsonSerializer.Serialize{T}"/>
    /// round-trip emits compact JSON regardless of source whitespace —
    /// that's the canonical form the budget gate compares verbatim.
    /// Property ordering is preserved by JsonDocument; same model +
    /// same call typically emits same property order, so this gives
    /// stable identity for the 3-consecutive-identical loop-detection
    /// rule.
    /// </para>
    ///
    /// <para>
    /// Malformed JSON falls through to the raw string — the budget gate
    /// compares verbatim either way; we just lose canonicalization.
    /// </para>
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

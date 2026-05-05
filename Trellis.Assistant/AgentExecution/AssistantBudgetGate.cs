using Microsoft.Extensions.Options;
using Trellis.Core.Models;
using Trellis.Core.Services;

namespace Trellis.Assistant.AgentExecution;

/// <summary>
/// Phase 3.A.1 placeholder <see cref="IAgentBudgetGate"/>. Pure logic
/// over <see cref="AgentRunState"/> — no Postgres or Hangfire deps.
/// Retrofit-to-Core when qwen's Phase A merges with
/// <c>DefaultBudgetGate</c>; Trellis.Core.PR-TBD swap is a 1-PR
/// follow-up that (a) deletes this class and (b) registers
/// <c>Trellis.Core.Services.DefaultBudgetGate</c> in DI instead.
///
/// <para>
/// Defaults match Core's contract per
/// <see cref="AgentBudgetOverrides"/> docstring:
/// <list type="bullet">
/// <item>MaxSteps = 25 (Assistant-side default per 61-doc § 5)</item>
/// <item>MaxRunDuration = 600s = 10 min</item>
/// <item>Loop detection: 3+ consecutive identical (tool, args)
/// dispatches</item>
/// </list>
/// Per-run overrides via
/// <see cref="AgentRunRequest.BudgetOverrides"/> apply with
/// caller-supplied values winning over the defaults.
/// </para>
///
/// <para>
/// Per Core's <see cref="BudgetVerdict"/> docstring: when the verdict
/// is a stop reason, the <see cref="BudgetVerdict.HumanReadableReason"/>
/// distinguishes step-cap vs time-cap (both map to
/// <see cref="AgentRunStatus.CapReached"/> on the run record). Loop
/// detection maps to <see cref="AgentRunStatus.LoopDetected"/>
/// distinctly.
/// </para>
/// </summary>
public sealed class AssistantBudgetGate : IAgentBudgetGate
{
    /// <summary>Default cap on total step count. Matches Core's "Assistant 25" docstring.</summary>
    public const int DefaultMaxSteps = 25;

    /// <summary>Default wall-clock cap on run duration. Matches Core's "10 min" docstring.</summary>
    public static readonly TimeSpan DefaultMaxRunDuration = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Window size for loop detection. The gate compares the most
    /// recent N tool dispatches; if all N are identical (same name +
    /// same canonicalized args), the run halts. 3 is the
    /// 61-doc § 5 standard.
    /// </summary>
    public const int LoopDetectionWindow = 3;

    public Task<BudgetVerdict> ShouldContinueAsync(
        AgentRunState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();

        var maxSteps = state.Overrides?.MaxSteps ?? DefaultMaxSteps;
        var maxRunDuration = state.Overrides?.MaxRunDuration ?? DefaultMaxRunDuration;

        // ---- Step cap ----
        if (state.CompletedStepCount >= maxSteps)
        {
            return Task.FromResult(new BudgetVerdict
            {
                Decision = BudgetDecision.StepCapReached,
                HumanReadableReason =
                    $"Step cap reached: {state.CompletedStepCount} steps completed >= MaxSteps={maxSteps}.",
            });
        }

        // ---- Wall-clock cap ----
        var elapsed = state.CheckedAt - state.StartedAt;
        if (elapsed >= maxRunDuration)
        {
            return Task.FromResult(new BudgetVerdict
            {
                Decision = BudgetDecision.TimedOut,
                HumanReadableReason =
                    $"MaxRunDuration reached: elapsed={elapsed.TotalSeconds:0}s >= MaxRunDuration={maxRunDuration.TotalSeconds:0}s.",
            });
        }

        // ---- Loop detection ----
        // Last LoopDetectionWindow dispatches all identical (same tool,
        // same canonicalized JSON args) → halt. Canonicalization is the
        // executor's responsibility (per AgentRunStateToolDispatch
        // docstring); this gate compares the strings verbatim.
        if (state.RecentToolDispatches.Count >= LoopDetectionWindow)
        {
            var window = state.RecentToolDispatches
                .Skip(state.RecentToolDispatches.Count - LoopDetectionWindow)
                .ToList();
            var first = window[0];
            var allIdentical = window.All(d =>
                d.ToolName == first.ToolName &&
                d.ParametersJson == first.ParametersJson);
            if (allIdentical)
            {
                return Task.FromResult(new BudgetVerdict
                {
                    Decision = BudgetDecision.LoopDetected,
                    HumanReadableReason =
                        $"Loop detected: {LoopDetectionWindow} consecutive identical dispatches of " +
                        $"tool '{first.ToolName}' with the same arguments.",
                });
            }
        }

        // ---- Per-step tool-call cap ----
        // v0 caps at 1 tool call per step (61-doc § 5). The executor
        // enforces this structurally (one dispatch per AgentStep), but
        // pin it here too in case a future executor loosens the cap.
        if (state.ToolCallsInCurrentStep > 1)
        {
            return Task.FromResult(new BudgetVerdict
            {
                Decision = BudgetDecision.ToolCallCapReached,
                HumanReadableReason =
                    $"Per-step tool-call cap exceeded: {state.ToolCallsInCurrentStep} > 1.",
            });
        }

        return Task.FromResult(new BudgetVerdict
        {
            Decision = BudgetDecision.Continue,
            HumanReadableReason =
                $"Within budget: step {state.CompletedStepCount + 1}/{maxSteps}, " +
                $"elapsed {elapsed.TotalSeconds:0}s/{maxRunDuration.TotalSeconds:0}s.",
        });
    }
}

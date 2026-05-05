using FluentAssertions;
using Trellis.Core.Models;
using Trellis.Core.Services;
using Xunit;

namespace Trellis.Assistant.Tests.AgentExecution;

/// <summary>
/// Assistant-side usage tests for <see cref="DefaultBudgetGate"/> (the
/// canonical Core impl post-retrofit). Covers the same 12 scenarios the
/// prior Phase 3.A.1 placeholder <c>AssistantBudgetGate</c> tests pinned;
/// the impl change is transparent at the
/// <see cref="BudgetVerdict.Decision"/> level, but reason-string
/// substring assertions adapt to Core's verbiage per Phase 3.A retrofit
/// D2a ratification ("max-steps cap" / "wall-clock budget" instead of
/// "MaxSteps" / "MaxRunDuration"). Trellis.Core has its own gate-internal
/// tests (<c>Trellis.Core.Tests.DefaultBudgetGateTests</c>); these
/// duplicate the scenarios for Assistant-side confidence and to lock the
/// contract from this consumer's perspective.
/// </summary>
public sealed class DefaultBudgetGateAssistantUsageTests
{
    private static readonly Guid TestRunId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TestOrgId = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly DateTime FixedStart = new(2026, 5, 5, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task FreshState_BelowAllCaps_ReturnsContinue()
    {
        var gate = new DefaultBudgetGate();
        var state = NewState(completedSteps: 0, elapsed: TimeSpan.FromSeconds(5));
        var verdict = await gate.ShouldContinueAsync(state);
        verdict.Decision.Should().Be(BudgetDecision.Continue);
    }

    [Fact]
    public async Task StepCap_AtConfiguredMaxSteps_ReturnsStepCapReached()
    {
        // DefaultBudgetGate's default is 1000 (Workflow), but here we
        // exercise via override = 25 (Assistant's documented value
        // injected by AssistantAgentExecutor.WithAssistantDefaults at
        // the executor layer; pinned separately by
        // Executor_BudgetOverridesNull_InjectsAssistantDefault25).
        var gate = new DefaultBudgetGate();
        var state = NewState(
            completedSteps: 25,
            elapsed: TimeSpan.FromSeconds(5),
            overrides: new AgentBudgetOverrides { MaxSteps = 25 });
        var verdict = await gate.ShouldContinueAsync(state);
        verdict.Decision.Should().Be(BudgetDecision.StepCapReached);
    }

    [Fact]
    public async Task StepCap_ReasonStringContainsMaxStepsCap()
    {
        // Phase 3.A retrofit D2a: substring asserts Core's "max-steps cap"
        // (lowercase, hyphenated) instead of the prior placeholder's
        // "MaxSteps". Operators tailing journalctl pattern-match on
        // "max-steps cap" — distinguishable from "wall-clock budget"
        // (time-cap) in the reason string per Phase 3.A C5 intent.
        var gate = new DefaultBudgetGate();
        var state = NewState(
            completedSteps: 25,
            elapsed: TimeSpan.FromSeconds(5),
            overrides: new AgentBudgetOverrides { MaxSteps = 25 });
        var verdict = await gate.ShouldContinueAsync(state);
        verdict.HumanReadableReason.Should().Contain("max-steps cap",
            "operators distinguish step-cap from time-cap by the reason-string substring; Core ships 'max-steps cap'");
    }

    [Fact]
    public async Task TimeCap_AtMaxRunDuration_ReturnsTimedOut()
    {
        var gate = new DefaultBudgetGate();
        var state = NewState(completedSteps: 1, elapsed: DefaultBudgetGate.DefaultMaxRunDuration);
        var verdict = await gate.ShouldContinueAsync(state);
        verdict.Decision.Should().Be(BudgetDecision.TimedOut);
    }

    [Fact]
    public async Task TimeCap_ReasonStringContainsWallClockBudget()
    {
        // Phase 3.A retrofit D2a sister pin to step-cap.
        var gate = new DefaultBudgetGate();
        var state = NewState(completedSteps: 1, elapsed: TimeSpan.FromMinutes(11));
        var verdict = await gate.ShouldContinueAsync(state);
        verdict.HumanReadableReason.Should().Contain("wall-clock budget",
            "time-cap reason string must distinguish from step-cap so operators can pattern-match in logs; Core ships 'wall-clock budget'");
    }

    [Fact]
    public async Task LoopDetection_3xConsecutiveIdentical_ReturnsLoopDetected()
    {
        var gate = new DefaultBudgetGate();
        var state = NewState(
            completedSteps: 3,
            elapsed: TimeSpan.FromSeconds(5),
            recentDispatches: new[]
            {
                new AgentRunStateToolDispatch { ToolName = "echo", ParametersJson = "{\"text\":\"hi\"}" },
                new AgentRunStateToolDispatch { ToolName = "echo", ParametersJson = "{\"text\":\"hi\"}" },
                new AgentRunStateToolDispatch { ToolName = "echo", ParametersJson = "{\"text\":\"hi\"}" },
            });
        var verdict = await gate.ShouldContinueAsync(state);
        verdict.Decision.Should().Be(BudgetDecision.LoopDetected);
    }

    [Fact]
    public async Task LoopDetection_3xButOneArgsDifferent_DoesNotFire()
    {
        var gate = new DefaultBudgetGate();
        var state = NewState(
            completedSteps: 3,
            elapsed: TimeSpan.FromSeconds(5),
            recentDispatches: new[]
            {
                new AgentRunStateToolDispatch { ToolName = "echo", ParametersJson = "{\"text\":\"hi\"}" },
                new AgentRunStateToolDispatch { ToolName = "echo", ParametersJson = "{\"text\":\"DIFFERENT\"}" },
                new AgentRunStateToolDispatch { ToolName = "echo", ParametersJson = "{\"text\":\"hi\"}" },
            });
        var verdict = await gate.ShouldContinueAsync(state);
        verdict.Decision.Should().Be(BudgetDecision.Continue,
            "loop detection requires identical (tool, args) for 3 consecutive — varying args means progress");
    }

    [Fact]
    public async Task LoopDetection_3xButOneToolDifferent_DoesNotFire()
    {
        var gate = new DefaultBudgetGate();
        var state = NewState(
            completedSteps: 3,
            elapsed: TimeSpan.FromSeconds(5),
            recentDispatches: new[]
            {
                new AgentRunStateToolDispatch { ToolName = "echo", ParametersJson = "{\"text\":\"hi\"}" },
                new AgentRunStateToolDispatch { ToolName = "search_documents", ParametersJson = "{\"text\":\"hi\"}" },
                new AgentRunStateToolDispatch { ToolName = "echo", ParametersJson = "{\"text\":\"hi\"}" },
            });
        var verdict = await gate.ShouldContinueAsync(state);
        verdict.Decision.Should().Be(BudgetDecision.Continue);
    }

    [Fact]
    public async Task LoopDetection_FewerThan3_DoesNotFire()
    {
        var gate = new DefaultBudgetGate();
        var state = NewState(
            completedSteps: 2,
            elapsed: TimeSpan.FromSeconds(5),
            recentDispatches: new[]
            {
                new AgentRunStateToolDispatch { ToolName = "echo", ParametersJson = "{\"text\":\"hi\"}" },
                new AgentRunStateToolDispatch { ToolName = "echo", ParametersJson = "{\"text\":\"hi\"}" },
            });
        var verdict = await gate.ShouldContinueAsync(state);
        verdict.Decision.Should().Be(BudgetDecision.Continue);
    }

    [Fact]
    public async Task PerStepToolCallCap_OverOne_ReturnsToolCallCapReached()
    {
        var gate = new DefaultBudgetGate();
        var state = NewState(completedSteps: 1, elapsed: TimeSpan.FromSeconds(5), toolCallsInCurrentStep: 2);
        var verdict = await gate.ShouldContinueAsync(state);
        verdict.Decision.Should().Be(BudgetDecision.ToolCallCapReached);
    }

    [Fact]
    public async Task Override_MaxSteps_AppliesOverDefault()
    {
        var gate = new DefaultBudgetGate();
        // Per-run override = 5; gate's own default is 1000. With
        // completedSteps=5, override fires.
        var state = NewState(
            completedSteps: 5,
            elapsed: TimeSpan.FromSeconds(5),
            overrides: new AgentBudgetOverrides { MaxSteps = 5 });
        var verdict = await gate.ShouldContinueAsync(state);
        verdict.Decision.Should().Be(BudgetDecision.StepCapReached);
    }

    [Fact]
    public async Task Override_MaxRunDuration_AppliesOverDefault()
    {
        var gate = new DefaultBudgetGate();
        // Per-run override = 30s; default is 10 min. With elapsed=30s,
        // override fires.
        var state = NewState(
            completedSteps: 1,
            elapsed: TimeSpan.FromSeconds(30),
            overrides: new AgentBudgetOverrides { MaxRunDuration = TimeSpan.FromSeconds(30) });
        var verdict = await gate.ShouldContinueAsync(state);
        verdict.Decision.Should().Be(BudgetDecision.TimedOut);
    }

    private static AgentRunState NewState(
        int completedSteps,
        TimeSpan elapsed,
        IReadOnlyList<AgentRunStateToolDispatch>? recentDispatches = null,
        int toolCallsInCurrentStep = 0,
        AgentBudgetOverrides? overrides = null) => new()
    {
        AgentRunId = TestRunId,
        OrgId = TestOrgId,
        StartedAt = FixedStart,
        CheckedAt = FixedStart + elapsed,
        CompletedStepCount = completedSteps,
        ToolCallsInCurrentStep = toolCallsInCurrentStep,
        RecentToolDispatches = recentDispatches ?? Array.Empty<AgentRunStateToolDispatch>(),
        Overrides = overrides,
    };
}

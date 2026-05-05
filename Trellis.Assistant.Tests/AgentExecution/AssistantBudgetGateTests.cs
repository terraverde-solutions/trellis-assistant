using FluentAssertions;
using Trellis.Assistant.AgentExecution;
using Trellis.Core.Models;
using Trellis.Core.Services;
using Xunit;

namespace Trellis.Assistant.Tests.AgentExecution;

/// <summary>
/// Pure-logic unit tests for <see cref="AssistantBudgetGate"/>. No
/// Postgres dependency; the gate consumes only <see cref="AgentRunState"/>
/// snapshots. Mutation pins per Phase 3.A C5 + Q10 ratifications.
/// </summary>
public sealed class AssistantBudgetGateTests
{
    private static readonly Guid TestRunId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TestOrgId = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly DateTime FixedStart = new(2026, 5, 5, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task FreshState_BelowAllCaps_ReturnsContinue()
    {
        var gate = new AssistantBudgetGate();
        var state = NewState(completedSteps: 0, elapsed: TimeSpan.FromSeconds(5));
        var verdict = await gate.ShouldContinueAsync(state);
        verdict.Decision.Should().Be(BudgetDecision.Continue);
    }

    [Fact]
    public async Task StepCap_AtConfiguredMaxSteps_ReturnsStepCapReached()
    {
        var gate = new AssistantBudgetGate();
        var state = NewState(completedSteps: AssistantBudgetGate.DefaultMaxSteps, elapsed: TimeSpan.FromSeconds(5));
        var verdict = await gate.ShouldContinueAsync(state);
        verdict.Decision.Should().Be(BudgetDecision.StepCapReached);
    }

    [Fact]
    public async Task StepCap_ReasonStringContainsMaxSteps()
    {
        // Phase 3.A C5 ratification: BudgetVerdict.Reason distinguishes
        // step-cap vs time-cap when CapReached fires. Pin the substring.
        var gate = new AssistantBudgetGate();
        var state = NewState(completedSteps: 25, elapsed: TimeSpan.FromSeconds(5));
        var verdict = await gate.ShouldContinueAsync(state);
        verdict.HumanReadableReason.Should().Contain("MaxSteps",
            "operators tailing journalctl distinguish step-cap from time-cap by the reason string substring");
    }

    [Fact]
    public async Task TimeCap_AtMaxRunDuration_ReturnsTimedOut()
    {
        var gate = new AssistantBudgetGate();
        var state = NewState(completedSteps: 1, elapsed: AssistantBudgetGate.DefaultMaxRunDuration);
        var verdict = await gate.ShouldContinueAsync(state);
        verdict.Decision.Should().Be(BudgetDecision.TimedOut);
    }

    [Fact]
    public async Task TimeCap_ReasonStringContainsMaxRunDuration()
    {
        // Phase 3.A C5 ratification — sister pin to step-cap.
        var gate = new AssistantBudgetGate();
        var state = NewState(completedSteps: 1, elapsed: TimeSpan.FromMinutes(11));
        var verdict = await gate.ShouldContinueAsync(state);
        verdict.HumanReadableReason.Should().Contain("MaxRunDuration",
            "time-cap reason string must distinguish from step-cap so operators can pattern-match in logs");
    }

    [Fact]
    public async Task LoopDetection_3xConsecutiveIdentical_ReturnsLoopDetected()
    {
        var gate = new AssistantBudgetGate();
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
        // Mutation pin per hub Q10: loop detection actually counts
        // identical hashes (canonicalized JSON), not just count of total
        // steps. A 3-step run with one differing args must NOT fire.
        var gate = new AssistantBudgetGate();
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
        var gate = new AssistantBudgetGate();
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
        var gate = new AssistantBudgetGate();
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
        var gate = new AssistantBudgetGate();
        var state = NewState(completedSteps: 1, elapsed: TimeSpan.FromSeconds(5), toolCallsInCurrentStep: 2);
        var verdict = await gate.ShouldContinueAsync(state);
        verdict.Decision.Should().Be(BudgetDecision.ToolCallCapReached);
    }

    [Fact]
    public async Task Override_MaxSteps_AppliesOverDefault()
    {
        var gate = new AssistantBudgetGate();
        // Per-run override = 5; default is 25. With completedSteps=5, override fires.
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
        var gate = new AssistantBudgetGate();
        // Per-run override = 30s; default is 10 min. With elapsed=30s, override fires.
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

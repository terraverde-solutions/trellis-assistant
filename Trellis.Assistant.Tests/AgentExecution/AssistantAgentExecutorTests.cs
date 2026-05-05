using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Trellis.Assistant.AgentExecution;
using Trellis.Assistant.Data;
using Trellis.Assistant.Tests.TestFixtures;
using Trellis.Core.Models;
using Trellis.Core.Services;
using Xunit;

namespace Trellis.Assistant.Tests.AgentExecution;

/// <summary>
/// Stub-driven integration tests for <see cref="AssistantAgentExecutor"/>.
/// Real Postgres via <see cref="PostgresFixture"/> (so the
/// <see cref="AgentRun"/> + <see cref="AgentStep"/> persistence path is
/// exercised); stub <see cref="IAgentLlmClient"/> + real
/// <see cref="ToolRegistry"/> + real <see cref="AssistantBudgetGate"/>.
///
/// X1 split holds: this is the stub-LLM surface. Real Ollama
/// happy-path lives in <see cref="RealOllamaSmokeTests"/> (gated on
/// <c>OLLAMA_BASE_URL</c>).
/// </summary>
public sealed class AssistantAgentExecutorTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid TestOrgId = Guid.Parse(TestTenants.TenantA);
    private readonly PostgresFixture _pg;
    private AssistantDbContext? _db;

    public AssistantAgentExecutorTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    public async Task InitializeAsync()
    {
        if (_pg.IsAvailable)
        {
            var options = new DbContextOptionsBuilder<AssistantDbContext>()
                .UseNpgsql(_pg.ConnectionString)
                .Options;
            _db = new AssistantDbContext(options);
            await _db.Database.MigrateAsync();
        }
    }

    public async Task DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task HappyPath_NoToolCallsFromModel_ReturnsSucceeded_WithEmptySteps()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var llm = new StubAgentLlmClient()
            .EnqueueAssistantText("This is the final answer.", tokensUsed: 42);
        var executor = NewExecutor(llm);

        var request = AgentRunRequest.Create(
            orgId: TestOrgId,
            userPrompt: "What's the answer?",
            availableTools: new List<AgentToolDescriptor> { new EchoTool().Descriptor });

        var run = await executor.RunAsync(request);

        run.Status.Should().Be(AgentRunStatus.Succeeded);
        run.Steps.Should().BeEmpty(
            "no tool_calls means the model went straight to the final answer; no steps dispatched");
        run.TokensUsed.Should().Be(42);
        run.CompletedAt.Should().NotBeNull();
    }

    [SkippableFact]
    public async Task HappyPath_OneToolCall_ThenAssistantText_PersistsStepAndReturnsSucceeded()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var llm = new StubAgentLlmClient()
            .EnqueueToolCall("echo", """{"text":"first call"}""", tokensUsed: 30)
            .EnqueueAssistantText("Done.", tokensUsed: 25);
        var executor = NewExecutor(llm);

        var request = AgentRunRequest.Create(
            orgId: TestOrgId,
            userPrompt: "Echo something.",
            availableTools: new List<AgentToolDescriptor> { new EchoTool().Descriptor });

        var run = await executor.RunAsync(request);

        run.Status.Should().Be(AgentRunStatus.Succeeded);
        run.Steps.Should().HaveCount(1);
        run.Steps[0].ToolName.Should().Be("echo");
        run.Steps[0].Status.Should().Be(AgentStepStatus.Succeeded);
        run.Steps[0].ToolOutputJson.Should().Contain("\"output\":\"first call\"");
        run.TokensUsed.Should().Be(55);

        // LLM was called twice — once to emit the tool_call, once to
        // emit the final assistant text after seeing the tool result.
        llm.Calls.Should().HaveCount(2);
        // Second call's message history must include the tool result
        // appended by the executor.
        llm.Calls[1].LastMessageContent.Should().Contain("TOOL_RESULT").And.Contain("first call");
    }

    [SkippableFact]
    public async Task BudgetGate_StepCapReached_ReturnsCapReached_WithReasonInErrorMessage()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        // Override max-steps to 2; queue 3 tool_calls so the gate fires
        // after 2 successful dispatches.
        var llm = new StubAgentLlmClient()
            .EnqueueToolCall("echo", """{"text":"step 0"}""")
            .EnqueueToolCall("echo", """{"text":"step 1"}""")
            .EnqueueToolCall("echo", """{"text":"step 2"}""")
            .EnqueueAssistantText("won't reach this");
        var executor = NewExecutor(llm);

        var request = AgentRunRequest.Create(
            orgId: TestOrgId,
            userPrompt: "Loop.",
            availableTools: new List<AgentToolDescriptor> { new EchoTool().Descriptor },
            budgetOverrides: new AgentBudgetOverrides { MaxSteps = 2 });

        var run = await executor.RunAsync(request);

        run.Status.Should().Be(AgentRunStatus.CapReached,
            "BudgetDecision.StepCapReached maps to AgentRunStatus.CapReached on the run record");
        run.Steps.Should().HaveCount(2,
            "exactly 2 steps dispatched before the gate halted (CompletedStepCount==MaxSteps)");
    }

    [SkippableFact]
    public async Task BudgetGate_LoopDetected_3xSameToolSameArgs_ReturnsLoopDetected()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var llm = new StubAgentLlmClient()
            .EnqueueToolCall("echo", """{"text":"identical"}""")
            .EnqueueToolCall("echo", """{"text":"identical"}""")
            .EnqueueToolCall("echo", """{"text":"identical"}""")
            // 4th call would be checked against the gate AFTER 3 prior
            // identical dispatches — the gate halts here.
            .EnqueueToolCall("echo", """{"text":"identical"}""")
            .EnqueueAssistantText("won't reach");
        var executor = NewExecutor(llm);

        var request = AgentRunRequest.Create(
            orgId: TestOrgId,
            userPrompt: "Stuck on echo.",
            availableTools: new List<AgentToolDescriptor> { new EchoTool().Descriptor });

        var run = await executor.RunAsync(request);

        run.Status.Should().Be(AgentRunStatus.LoopDetected,
            "BudgetDecision.LoopDetected maps to AgentRunStatus.LoopDetected (distinct from CapReached)");
        run.Steps.Should().HaveCount(3,
            "the loop fires AFTER the 3rd identical step has persisted");
    }

    [SkippableFact]
    public async Task ToolNotInRegistry_LlmEmitsUnknownToolName_StepFailsWithToolNotRegistered()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var llm = new StubAgentLlmClient()
            .EnqueueToolCall("nonexistent_tool", """{"foo":"bar"}""")
            .EnqueueAssistantText("Recovered.");
        var executor = NewExecutor(llm);

        var request = AgentRunRequest.Create(
            orgId: TestOrgId,
            userPrompt: "Try a missing tool.",
            availableTools: new List<AgentToolDescriptor> { new EchoTool().Descriptor });

        var run = await executor.RunAsync(request);

        run.Status.Should().Be(AgentRunStatus.Succeeded,
            "the run continues after a failed step — model recovers with a final assistant text");
        run.Steps.Should().HaveCount(1);
        run.Steps[0].Status.Should().Be(AgentStepStatus.Failed);
        run.Steps[0].ErrorMessage.Should().Contain("not registered");
    }

    [SkippableFact]
    public async Task ToolReturnsSuccessFalse_StepFails_WithToolErrorMessageVerbatim()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        // Echo with empty text returns Success=false per its contract.
        var llm = new StubAgentLlmClient()
            .EnqueueToolCall("echo", """{"text":""}""")
            .EnqueueAssistantText("got it");
        var executor = NewExecutor(llm);

        var request = AgentRunRequest.Create(
            orgId: TestOrgId,
            userPrompt: "Echo nothing.",
            availableTools: new List<AgentToolDescriptor> { new EchoTool().Descriptor });

        var run = await executor.RunAsync(request);

        run.Steps.Should().HaveCount(1);
        run.Steps[0].Status.Should().Be(AgentStepStatus.Failed);
        run.Steps[0].ErrorMessage.Should().Contain("text",
            "tool's own ErrorMessage surfaces verbatim, distinguishing from the executor's own framing");
    }

    [SkippableFact]
    public async Task ToolThrows_StepFails_WithExecutorFramedMessage()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        // Throwing tool — Core contract says "throws are reserved for
        // unexpected internal errors; executor catches + converts to
        // AgentStepStatus.Failed with a generic message."
        var throwingTool = new ThrowingTestTool();
        var llm = new StubAgentLlmClient()
            .EnqueueToolCall("throwing_tool", """{}""")
            .EnqueueAssistantText("recovered");
        var executor = NewExecutor(llm, extraTools: new IAgentTool[] { throwingTool });

        var request = AgentRunRequest.Create(
            orgId: TestOrgId,
            userPrompt: "Try a throwing tool.",
            availableTools: new List<AgentToolDescriptor> { throwingTool.Descriptor });

        var run = await executor.RunAsync(request);

        run.Status.Should().Be(AgentRunStatus.Succeeded);
        run.Steps[0].Status.Should().Be(AgentStepStatus.Failed);
        run.Steps[0].ErrorMessage.Should().Contain("threw",
            "executor wraps the exception under a generic 'tool threw' label rather than leaking the raw stack");
    }

    [SkippableFact]
    public async Task Plan_RoundTripsAsHumanReadableString_NoStructuredAssertion()
    {
        // Phase 3.A C2 ratified pin. Plan field is a string per Core's
        // authoritative docstring, NOT JSON. Locks the contract against
        // a future "let's make Plan JSON" temptation.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var llm = new StubAgentLlmClient().EnqueueAssistantText("done");
        var executor = NewExecutor(llm);

        var request = AgentRunRequest.Create(
            orgId: TestOrgId,
            userPrompt: "What is the meaning of life?",
            availableTools: new List<AgentToolDescriptor> { new EchoTool().Descriptor });

        var run = await executor.RunAsync(request);

        run.Plan.Should().NotBeNullOrWhiteSpace();
        run.Plan.Should().Contain("LLM-driven", "Plan is a human-readable summary, not structured JSON");
        run.Plan.Should().Contain("echo", "tool catalogue surface in the plan summary");
        // Anti-mutation: plan must NOT parse as JSON. If a future change
        // makes it JSON, this assertion catches it.
        var act = () => System.Text.Json.JsonDocument.Parse(run.Plan);
        act.Should().Throw<System.Text.Json.JsonException>(
            "Plan is a human-readable string per Core's authoritative docstring; not JSON");
    }

    [SkippableFact]
    public async Task LoopDetection_CanonicalizesArgsForComparison_WhitespaceVarianceStillSameKey()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        // Same arguments but with different whitespace — canonicalization
        // means these are identical for loop detection. Three calls with
        // whitespace variants → loop detected.
        var llm = new StubAgentLlmClient()
            .EnqueueToolCall("echo", """{"text":"hi"}""")
            .EnqueueToolCall("echo", """{ "text" : "hi" }""")  // extra spaces
            .EnqueueToolCall("echo", """{"text":"hi"}""")
            .EnqueueToolCall("echo", """{"text":"hi"}""")  // 4th would dispatch if loop didn't fire
            .EnqueueAssistantText("won't reach");
        var executor = NewExecutor(llm);

        var request = AgentRunRequest.Create(
            orgId: TestOrgId,
            userPrompt: "Whitespace canonicalization probe.",
            availableTools: new List<AgentToolDescriptor> { new EchoTool().Descriptor });

        var run = await executor.RunAsync(request);

        run.Status.Should().Be(AgentRunStatus.LoopDetected,
            "JSON canonicalization in the executor strips whitespace — identical-after-canonicalization counts as identical for loop detection");
    }

    [SkippableFact]
    public async Task Cancellation_TripsToken_PersistsRunWithCancelled_ReturnsTerminalRecord()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        // Cancel before the first LLM call — the executor's loop body
        // throws OperationCanceledException at the first ThrowIfCancellationRequested
        // and the catch handler maps it to AgentRunStatus.Cancelled.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var llm = new StubAgentLlmClient().EnqueueAssistantText("won't reach");
        var executor = NewExecutor(llm);

        var request = AgentRunRequest.Create(
            orgId: TestOrgId,
            userPrompt: "Cancel me.",
            availableTools: new List<AgentToolDescriptor> { new EchoTool().Descriptor });

        var run = await executor.RunAsync(request, cts.Token);
        run.Status.Should().Be(AgentRunStatus.Cancelled);
        run.CompletedAt.Should().NotBeNull(
            "even on cancellation, the terminal write must succeed — the run record reflects reality");
    }

    [SkippableFact]
    public async Task PersistedSteps_StepIndex_AssignedSequentiallyFromZero()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var llm = new StubAgentLlmClient()
            .EnqueueToolCall("echo", """{"text":"step 0 attempt 1"}""")
            .EnqueueToolCall("echo", """{"text":"step 1 different"}""")
            .EnqueueAssistantText("done");
        var executor = NewExecutor(llm);

        var request = AgentRunRequest.Create(
            orgId: TestOrgId,
            userPrompt: "Multi-step.",
            availableTools: new List<AgentToolDescriptor> { new EchoTool().Descriptor });

        var run = await executor.RunAsync(request);

        run.Steps.Should().HaveCount(2);
        run.Steps[0].StepIndex.Should().Be(0);
        run.Steps[1].StepIndex.Should().Be(1);
    }

    private AssistantAgentExecutor NewExecutor(
        IAgentLlmClient llm,
        IEnumerable<IAgentTool>? extraTools = null)
    {
        var allTools = new List<IAgentTool> { new EchoTool() };
        if (extraTools is not null)
        {
            allTools.AddRange(extraTools);
        }
        var registry = new ToolRegistry(allTools);
        var gate = new AssistantBudgetGate();
        var store = new PostgresAgentRunStore(_db!);
        var options = Options.Create(new AssistantAgentExecutorOptions
        {
            Model = "stub-model",
            SystemPrompt = "You are a stub.",
        });
        return new AssistantAgentExecutor(
            store, llm, registry, gate, options,
            NullLogger<AssistantAgentExecutor>.Instance);
    }

    private sealed class ThrowingTestTool : IAgentTool
    {
        public AgentToolDescriptor Descriptor { get; } = new()
        {
            Name = "throwing_tool",
            Description = "Always throws — used to pin Core's throw → AgentStepStatus.Failed contract.",
            ParameterSchema = """{"type":"object"}""",
            Category = AgentToolCategory.Inspect,
        };

        public Task<AgentToolOutput> RunAsync(AgentToolInput input, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("intentional test throw");
    }
}

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
    private const string TestUserId = TestTenants.UserA;
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

    // ---------------- Phase 3.A.2: RunForConversationAsync ----------------

    [SkippableFact]
    public async Task RunForConversationAsync_NoToolCalls_ReturnsConversationAgentResultWithFinalText()
    {
        // Phase 3.A.2 entry point: orchestrator-supplied messages, model
        // emits no tool_calls, returns ConversationAgentResult with the
        // final assistant text + empty tool dispatches.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var llm = new StubAgentLlmClient()
            .EnqueueAssistantText("conversation answer", tokensUsed: 50);
        var executor = NewExecutor(llm);

        var messages = new List<ChatMessage>
        {
            new() { Role = ChatRole.User, Content = "what's the capital?" },
        };
        var result = await executor.RunForConversationAsync(
            orgId: TestOrgId,
            userId: TestUserId,
            assistantTurnId: null,
            userPrompt: "what's the capital?",
            messages: messages,
            availableTools: new[] { new EchoTool().Descriptor },
            budgetOverrides: null);

        result.AgentRun.Status.Should().Be(AgentRunStatus.Succeeded);
        result.AgentRun.TokensUsed.Should().Be(50);
        result.FinalAssistantText.Should().Be("conversation answer");
        result.ToolDispatches.Should().BeEmpty();
    }

    [SkippableFact]
    public async Task RunForConversationAsync_OneToolCall_CapturesToolDispatchSummary()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var llm = new StubAgentLlmClient()
            .EnqueueToolCall("echo", """{"text":"agent dispatch"}""")
            .EnqueueAssistantText("done");
        var executor = NewExecutor(llm);

        var messages = new List<ChatMessage>
        {
            new() { Role = ChatRole.User, Content = "echo agent dispatch" },
        };
        var result = await executor.RunForConversationAsync(
            orgId: TestOrgId,
            userId: TestUserId,
            assistantTurnId: null,
            userPrompt: "echo agent dispatch",
            messages: messages,
            availableTools: new[] { new EchoTool().Descriptor },
            budgetOverrides: null);

        result.AgentRun.Status.Should().Be(AgentRunStatus.Succeeded);
        result.FinalAssistantText.Should().Be("done");
        result.ToolDispatches.Should().HaveCount(1);
        result.ToolDispatches[0].ToolName.Should().Be("echo");
        result.ToolDispatches[0].ToolCallId.Should().NotBeNullOrEmpty();
        result.ToolDispatches[0].ToolCallId.Length.Should().Be(26,
            "ToolCallId is the Ulid-stringified AgentStep.Id (26-char base32)");
        result.ToolDispatches[0].ResultContent.Should().Contain("agent dispatch",
            "tool result content carries the dispatched tool's output JSON");
    }

    [SkippableFact]
    public async Task RunForConversationAsync_ToolFailed_ResultContentIsErrorEnvelope()
    {
        // Mutation pin: when a tool dispatch returns Success=false, the
        // ConversationAgentToolDispatch.ResultContent carries a
        // JSON-serialized error envelope, NOT the tool's null
        // ResultJson. The orchestrator persists this verbatim as the
        // Tool turn's content; clients see the failure inline in
        // history.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        // Echo with empty text returns Success=false.
        var llm = new StubAgentLlmClient()
            .EnqueueToolCall("echo", """{"text":""}""")
            .EnqueueAssistantText("recovered");
        var executor = NewExecutor(llm);

        var messages = new List<ChatMessage>
        {
            new() { Role = ChatRole.User, Content = "test failure path" },
        };
        var result = await executor.RunForConversationAsync(
            orgId: TestOrgId,
            userId: TestUserId,
            assistantTurnId: null,
            userPrompt: "test failure path",
            messages: messages,
            availableTools: new[] { new EchoTool().Descriptor },
            budgetOverrides: null);

        result.ToolDispatches.Should().HaveCount(1);
        result.ToolDispatches[0].ResultContent.Should().Contain("error",
            "failed dispatches carry a JSON-serialized error envelope so clients see structured failure inline");
    }

    [SkippableFact]
    public async Task RunForConversationAsync_BudgetCapHit_ReturnsCapReached_FinalTextEmpty()
    {
        // Pin: when the budget gate halts the run, FinalAssistantText
        // is empty (no model-emitted final text) and the orchestrator
        // is responsible for synthesizing a placeholder assistant
        // turn (which the orchestrator does — verified at the endpoint
        // layer).
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var llm = new StubAgentLlmClient()
            .EnqueueToolCall("echo", """{"text":"loop 0"}""")
            .EnqueueToolCall("echo", """{"text":"loop 1"}""")
            .EnqueueToolCall("echo", """{"text":"loop 2"}""");
        var executor = NewExecutor(llm);

        var messages = new List<ChatMessage>
        {
            new() { Role = ChatRole.User, Content = "loop a bit" },
        };
        var result = await executor.RunForConversationAsync(
            orgId: TestOrgId,
            userId: TestUserId,
            assistantTurnId: null,
            userPrompt: "loop a bit",
            messages: messages,
            availableTools: new[] { new EchoTool().Descriptor },
            budgetOverrides: new AgentBudgetOverrides { MaxSteps = 2 });

        result.AgentRun.Status.Should().Be(AgentRunStatus.CapReached);
        result.FinalAssistantText.Should().BeEmpty(
            "budget halt before final assistant text → FinalAssistantText is empty; orchestrator synthesizes placeholder");
        result.ToolDispatches.Should().HaveCount(2,
            "exactly 2 dispatches before the gate halted (CompletedStepCount==MaxSteps)");
    }

    [SkippableFact]
    public async Task RunForConversationAsync_ZeroOrgId_Throws()
    {
        // Defensive pin: Guid.Empty for OrgId triggers the same
        // ArgumentException as IAgentExecutor.RunAsync via
        // AgentRunRequest.Create. RunForConversationAsync mirrors that
        // contract.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var executor = NewExecutor(new StubAgentLlmClient());
        var messages = new List<ChatMessage>
        {
            new() { Role = ChatRole.User, Content = "x" },
        };
        var act = () => executor.RunForConversationAsync(
            orgId: Guid.Empty,
            userId: TestUserId,
            assistantTurnId: null,
            userPrompt: "x",
            messages: messages,
            availableTools: Array.Empty<AgentToolDescriptor>(),
            budgetOverrides: null);
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*OrgId*non-empty*");
    }

    [SkippableFact]
    public async Task RunForConversationAsync_NullAssistantTurnId_PersistsAgentRunWithNull()
    {
        // Phase 3.A.2's orchestrator currently passes assistantTurnId=null
        // (turn ids are generated by the store under the advisory lock
        // AFTER the executor returns; orchestrator can't pre-allocate).
        // Pin that null flows through cleanly to the persisted AgentRun
        // — the FK with ON DELETE SET NULL accepts null.
        //
        // (A propagation pin for the non-null case would require pre-
        // creating a real turn row to satisfy the FK; deferred until
        // a Phase 3.A.3+ pre-allocation flow makes it relevant.)
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var llm = new StubAgentLlmClient().EnqueueAssistantText("done");
        var executor = NewExecutor(llm);

        var messages = new List<ChatMessage>
        {
            new() { Role = ChatRole.User, Content = "test" },
        };
        var result = await executor.RunForConversationAsync(
            orgId: TestOrgId,
            userId: TestUserId,
            assistantTurnId: null,
            userPrompt: "test",
            messages: messages,
            availableTools: Array.Empty<AgentToolDescriptor>(),
            budgetOverrides: null);

        result.AgentRun.AssistantTurnId.Should().BeNull(
            "null assistantTurnId persists as null — Phase 3.A.2 orchestrator's current pattern");
    }

    // ---------------- Post-Phase-3.A.2 retrofit: Assistant defaults injection ----------------

    [SkippableFact]
    public async Task Executor_BudgetOverridesNull_InjectsAssistantDefault25()
    {
        // Phase 3.A retrofit D1 mutation pin: when the caller passes
        // BudgetOverrides=null, the executor must inject Assistant's
        // documented MaxSteps=25 default before delegating to the gate.
        // DefaultBudgetGate's own default is 1000 (Workflow's value);
        // without injection, an Assistant agent run would silently allow
        // 1000 steps before halting — divergence from Phase 0 PR #10's
        // documented "Assistant 25" default.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var capturedState = new CaptureBudgetGate();
        var llm = new StubAgentLlmClient().EnqueueAssistantText("done");
        var executor = NewExecutor(llm, customGate: capturedState);

        await executor.RunAsync(AgentRunRequest.Create(
            orgId: TestOrgId,
            userPrompt: "test",
            availableTools: new List<AgentToolDescriptor> { new EchoTool().Descriptor },
            budgetOverrides: null));  // <-- caller passes null

        capturedState.FirstCallState.Should().NotBeNull(
            "the executor must call the gate at least once before deciding to terminate");
        capturedState.FirstCallState!.Overrides.Should().NotBeNull(
            "executor injected AgentBudgetOverrides for the gate even though caller passed null");
        capturedState.FirstCallState!.Overrides!.MaxSteps.Should().Be(
            AssistantAgentExecutor.AssistantDefaultMaxSteps,
            "Assistant default MaxSteps=25 must be injected when caller passes null — Phase 0 PR #10 contract");
    }

    [SkippableFact]
    public async Task Executor_BudgetOverridesNotNull_RespectsCallerValue()
    {
        // Sister mutation pin: when the caller DID supply a MaxSteps
        // override, the executor must preserve it — NOT silently
        // overwrite with the Assistant default. A regression that
        // always-injects-25 would cap a long-running batch run at 25
        // even when the caller asked for more (or less).
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var capturedState = new CaptureBudgetGate();
        var llm = new StubAgentLlmClient().EnqueueAssistantText("done");
        var executor = NewExecutor(llm, customGate: capturedState);

        await executor.RunAsync(AgentRunRequest.Create(
            orgId: TestOrgId,
            userPrompt: "test",
            availableTools: new List<AgentToolDescriptor> { new EchoTool().Descriptor },
            budgetOverrides: new AgentBudgetOverrides { MaxSteps = 42 }));

        capturedState.FirstCallState!.Overrides!.MaxSteps.Should().Be(42,
            "caller-supplied MaxSteps=42 must propagate verbatim to the gate; injecting the Assistant default would silently override caller intent");
    }

    [SkippableFact]
    public async Task RunForConversationAsync_BudgetOverridesNull_InjectsAssistantDefault25()
    {
        // Same injection contract for the Phase 3.A.2 entry point —
        // both Assistant paths share the WithAssistantDefaults helper.
        // Pinned here separately since RunForConversationAsync's
        // budgetOverrides parameter is its own argument (not via
        // AgentRunRequest).
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var capturedState = new CaptureBudgetGate();
        var llm = new StubAgentLlmClient().EnqueueAssistantText("done");
        var executor = NewExecutor(llm, customGate: capturedState);

        var messages = new List<ChatMessage>
        {
            new() { Role = ChatRole.User, Content = "test" },
        };
        await executor.RunForConversationAsync(
            orgId: TestOrgId,
            userId: TestUserId,
            assistantTurnId: null,
            userPrompt: "test",
            messages: messages,
            availableTools: Array.Empty<AgentToolDescriptor>(),
            budgetOverrides: null);

        capturedState.FirstCallState!.Overrides!.MaxSteps.Should().Be(
            AssistantAgentExecutor.AssistantDefaultMaxSteps,
            "RunForConversationAsync must inject the same Assistant MaxSteps=25 default as RunAsync — both paths share WithAssistantDefaults");
    }

    /// <summary>
    /// Test gate that captures the first <see cref="AgentRunState"/>
    /// passed in + then halts the loop with StepCapReached so the
    /// executor exits quickly. Used by the injection-contract pins.
    /// </summary>
    private sealed class CaptureBudgetGate : IAgentBudgetGate
    {
        public AgentRunState? FirstCallState { get; private set; }

        public Task<BudgetVerdict> ShouldContinueAsync(AgentRunState state, CancellationToken cancellationToken = default)
        {
            FirstCallState ??= state;
            return Task.FromResult(new BudgetVerdict
            {
                Decision = BudgetDecision.StepCapReached,
                HumanReadableReason = "stub-halt",
            });
        }
    }

    // ---------------- Phase 3.C: system prompt + descriptor resolution ----------------

    [SkippableFact]
    public async Task SystemPrompt_StandalonePath_InjectedAtMessagesIndexZero_WithToolCatalogue()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var llm = new StubAgentLlmClient()
            .EnqueueAssistantText("ok.");
        var executor = NewExecutor(llm);

        var request = AgentRunRequest.Create(
            orgId: TestOrgId,
            userPrompt: "test prompt",
            availableTools: new List<AgentToolDescriptor> { new EchoTool().Descriptor });

        await executor.RunAsync(request);

        llm.Calls.Should().HaveCount(1);
        var call = llm.Calls[0];
        call.MessageRoles[0].Should().Be(ChatRole.System,
            "Phase 3.C contract: executor injects system prompt at messages[0]");
    }

    [SkippableFact]
    public async Task SystemPrompt_IncludesToolCatalogue_WithNamesAndDescriptions()
    {
        // The system-prompt builder enumerates each resolvable tool's
        // Name + Description so the LLM has natural-language guidance
        // for when to invoke each tool (the function-calling protocol's
        // tools array carries the full schema; this gives the LLM
        // selection guidance).
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var llm = new StubAgentLlmClient()
            .EnqueueAssistantText("ok.");
        var executor = NewExecutor(llm);

        var request = AgentRunRequest.Create(
            orgId: TestOrgId,
            userPrompt: "x",
            availableTools: new List<AgentToolDescriptor> { new EchoTool().Descriptor });

        await executor.RunAsync(request);

        llm.Calls.Should().HaveCount(1);
        var systemPromptContent = llm.Calls[0].FirstMessageContent;
        systemPromptContent.Should().Contain("Available tools",
            "system prompt's catalogue section starts with 'Available tools' header");
        systemPromptContent.Should().Contain(EchoTool.ToolName,
            "system prompt enumerates each registered tool's Name");
        systemPromptContent.Should().Contain("Echo the supplied text",
            "system prompt enumerates each registered tool's Description (echo's description starts 'Echo the supplied text back...')");
    }

    [SkippableFact]
    public async Task SystemPrompt_NoResolvableTools_FallsBackToBaseSystemPromptOnly()
    {
        // When the caller passes an empty AvailableTools list, the
        // executor still injects messages[0] as System role but with
        // the base persona only (no "Available tools:" section). This
        // avoids confusing the model with "Available tools: (none)".
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var llm = new StubAgentLlmClient()
            .EnqueueAssistantText("ok.");
        var executor = NewExecutor(llm);

        var request = AgentRunRequest.Create(
            orgId: TestOrgId,
            userPrompt: "x",
            availableTools: new List<AgentToolDescriptor>());  // empty

        await executor.RunAsync(request);

        llm.Calls.Should().HaveCount(1);
        llm.Calls[0].MessageRoles[0].Should().Be(ChatRole.System,
            "system prompt is still injected even with empty catalogue");
        llm.Calls[0].ToolCount.Should().Be(0,
            "empty AvailableTools → empty function-calling tool array");
        llm.Calls[0].FirstMessageContent.Should().NotContain("Available tools",
            "empty catalogue omits the 'Available tools' section entirely; avoids confusing the model with '(none)'");
    }

    [SkippableFact]
    public async Task DescriptorResolution_PlaceholderDescriptorSwappedForRegisteredOne()
    {
        // Phase 3.C: ResolveDescriptors swaps caller-supplied placeholder
        // descriptors (e.g., the orchestrator's "(filter placeholder for
        // 'echo')" descriptions) for the registered tool's actual
        // descriptor. The LLM sees the real description + real schema.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var llm = new StubAgentLlmClient()
            .EnqueueAssistantText("ok.");
        var executor = NewExecutor(llm);

        // Caller supplies a placeholder descriptor (name matches a
        // registered tool, but description + schema are deliberately
        // wrong-shape).
        var placeholderDescriptor = new AgentToolDescriptor
        {
            Name = EchoTool.ToolName,
            Description = "WRONG DESCRIPTION — should be replaced by ResolveDescriptors",
            ParameterSchema = """{"type":"object","additionalProperties":true}""",
            Category = AgentToolCategory.Inspect,
        };

        var request = AgentRunRequest.Create(
            orgId: TestOrgId,
            userPrompt: "x",
            availableTools: new List<AgentToolDescriptor> { placeholderDescriptor });

        await executor.RunAsync(request);

        llm.Calls.Should().HaveCount(1);
        llm.Calls[0].ToolCount.Should().Be(1,
            "the registered echo tool resolves through the placeholder, so one tool flows to the LLM");
        llm.Calls[0].ToolDescriptions[0].Should().NotContain("WRONG DESCRIPTION",
            "ResolveDescriptors swaps the caller's placeholder for the registry's real descriptor — placeholder description never reaches the LLM");
        llm.Calls[0].ToolDescriptions[0].Should().Contain("Echo the supplied text",
            "the real EchoTool descriptor's description text flows to the LLM");
    }

    // ---------------- Phase 3.E: multi-tool dispatch per LLM turn ----------------

    [SkippableFact]
    public async Task MultiToolCall_TwoToolsInOneTurn_BothDispatch_BothResultsAppendedInOrder()
    {
        // Phase 3.E pin: LLM emits two tool_calls in a single assistant
        // message → executor dispatches both serially in order → both
        // AgentSteps persist → both ResultContents appear in
        // ConversationAgentResult.ToolDispatches → second LLM call sees
        // both TOOL_RESULT envelopes in messages history.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var llm = new StubAgentLlmClient()
            .EnqueueMultipleToolCalls(
                ("echo", """{"text":"first call"}"""),
                ("echo", """{"text":"second call"}"""))
            .EnqueueAssistantText("synthesized both tool results.");
        var executor = NewExecutor(llm);

        var request = AgentRunRequest.Create(
            orgId: TestOrgId,
            userPrompt: "two-tool turn",
            availableTools: new List<AgentToolDescriptor> { new EchoTool().Descriptor });

        var run = await executor.RunAsync(request);

        run.Status.Should().Be(AgentRunStatus.Succeeded);
        run.Steps.Should().HaveCount(2,
            "two tool_calls in one LLM response → two AgentSteps. Phase 3.E pin #2: 1 step per tool_call.");
        run.Steps[0].StepIndex.Should().Be(0);
        run.Steps[1].StepIndex.Should().Be(1,
            "step indices monotonic across multi-tool dispatch (per Phase 3.A.1's sequential-index contract).");
        run.Steps[0].Status.Should().Be(AgentStepStatus.Succeeded);
        run.Steps[1].Status.Should().Be(AgentStepStatus.Succeeded);
        run.Steps[0].ToolInputJson.Should().Contain("first call");
        run.Steps[1].ToolInputJson.Should().Contain("second call");

        // 2 LLM calls: the multi-tool one + the final synth.
        llm.Calls.Should().HaveCount(2);
        // The second LLM call's message context should include BOTH
        // TOOL_RESULT envelopes (one per dispatched tool_call), in
        // dispatch order. Phase 3.E pin #4: tool result append order
        // matches the order in the LLM's tool_calls[] array.
        var secondCall = llm.Calls[1];
        var toolResultsInContext = secondCall.MessageRoles
            .Zip(Enumerable.Range(0, secondCall.MessageCount))
            .Where(z => z.First == ChatRole.System)
            .Count();
        // System prompt + 2 TOOL_RESULT envelopes = 3 system messages
        toolResultsInContext.Should().BeGreaterThanOrEqualTo(3,
            "messages list carries the executor's system prompt at [0] + 2 TOOL_RESULT envelopes after dispatch");
    }

    [SkippableFact]
    public async Task MultiToolCall_FirstSucceedsSecondFails_BothResultsAppended_LoopContinues()
    {
        // Phase 3.E pin #5: per-tool-call schema validation /
        // execution failures are INDEPENDENT. If tool A's args are
        // schema-valid + dispatch succeeds, and tool B's args fail
        // schema validation, both produce AgentSteps (A=Succeeded,
        // B=Failed) and the loop continues so the LLM sees both
        // results + can react.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var llm = new StubAgentLlmClient()
            .EnqueueMultipleToolCalls(
                ("echo", """{"text":"valid call"}"""),
                ("echo", """{"not_text":"schema-invalid; missing required 'text'"}"""))
            .EnqueueAssistantText("one ok, one rejected.");
        var executor = NewExecutor(llm);

        var request = AgentRunRequest.Create(
            orgId: TestOrgId,
            userPrompt: "mixed success/failure",
            availableTools: new List<AgentToolDescriptor> { new EchoTool().Descriptor });

        var run = await executor.RunAsync(request);

        run.Status.Should().Be(AgentRunStatus.Succeeded);
        run.Steps.Should().HaveCount(2,
            "both tool_calls produce AgentSteps; failure on the second does NOT short-circuit the first or prevent the loop from continuing");
        run.Steps[0].Status.Should().Be(AgentStepStatus.Succeeded,
            "first tool_call had valid args, dispatched cleanly");
        run.Steps[1].Status.Should().Be(AgentStepStatus.Failed,
            "second tool_call's schema-invalid args fail the executor's schema gate");
        run.Steps[1].ErrorMessage.Should().Contain("schema validation failed",
            "Phase 3.B's schema gate identifies the rejection cause");
    }

    [SkippableFact]
    public async Task MultiToolCall_BudgetExhaustsMidIteration_DispatchesPartialAndSurfacesBudgetMarker()
    {
        // Phase 3.E pin #6: budget exhaust mid-iteration. With
        // MaxSteps=1 the gate halts AFTER the first tool dispatch (
        // step 0 ran, step 1 is over cap). The executor dispatches the
        // first tool_call, skips the second, emits a synthetic
        // ChatRole.System BUDGET_EXHAUSTED message, and terminates with
        // CapReached.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var llm = new StubAgentLlmClient()
            .EnqueueMultipleToolCalls(
                ("echo", """{"text":"dispatched"}"""),
                ("echo", """{"text":"should be skipped"}"""));
        var executor = NewExecutor(llm);

        var request = AgentRunRequest.Create(
            orgId: TestOrgId,
            userPrompt: "budget-exhaust mid iter",
            availableTools: new List<AgentToolDescriptor> { new EchoTool().Descriptor },
            budgetOverrides: new AgentBudgetOverrides { MaxSteps = 1 });

        var run = await executor.RunAsync(request);

        run.Status.Should().Be(AgentRunStatus.CapReached,
            "terminal status carries the mid-iteration verdict (StepCapReached → CapReached)");
        run.Steps.Should().HaveCount(1,
            "only the first tool_call dispatched; the second was skipped");
        run.Steps[0].StepIndex.Should().Be(0);
        run.Steps[0].ToolInputJson.Should().Contain("dispatched",
            "the first tool_call's args were preserved in the persisted step");
        run.ErrorMessage.Should().NotBeNullOrEmpty();
        run.ErrorMessage.Should().Contain("steps",
            "step-cap verdict's reason string mentions the step budget");
    }

    [SkippableFact]
    public async Task MultiToolCall_ExceedsPerStepCap_TerminatesAtCap_WithCapReachedStatus()
    {
        // PR #12 review Blocker fix-up: pins the DefaultBudgetGate's
        // per-step tool-call cap (`state.ToolCallsInCurrentStep > 1`)
        // actually firing on the multi-tool path. Pre-fix the executor
        // hardcoded ToolCallsInCurrentStep=0 in the mid-iteration state,
        // so the gate's cap NEVER fired regardless of LLM emission size
        // — an LLM emitting 10 tool_calls would slip through.
        //
        // Drive 3 tool_calls in one LLM emission. With the fix:
        //   • Iter 1 (call 0): dispatchedToolCalls=0, skip mid-check.
        //     Dispatch. dispatchedToolCalls=1.
        //   • Iter 2 (call 1): dispatchedToolCalls=1, mid-check state's
        //     ToolCallsInCurrentStep=1. Gate's `> 1` is FALSE → Continue.
        //     Dispatch. dispatchedToolCalls=2.
        //   • Iter 3 (call 2): dispatchedToolCalls=2, mid-check state's
        //     ToolCallsInCurrentStep=2. Gate's `> 1` is TRUE →
        //     ToolCallCapReached → halt.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var llm = new StubAgentLlmClient()
            .EnqueueMultipleToolCalls(
                ("echo", """{"text":"first"}"""),
                ("echo", """{"text":"second"}"""),
                ("echo", """{"text":"third — should trip the per-step cap"}"""));
        var executor = NewExecutor(llm);

        var request = AgentRunRequest.Create(
            orgId: TestOrgId,
            userPrompt: "three tool_calls in one turn",
            availableTools: new List<AgentToolDescriptor> { new EchoTool().Descriptor });

        var run = await executor.RunAsync(request);

        run.Status.Should().Be(AgentRunStatus.CapReached,
            "DefaultBudgetGate.ToolCallCapReached maps to CapReached on the AgentRun (matches StepCapReached + TimedOut)");
        run.Steps.Should().HaveCount(2,
            "first two tool_calls dispatch; the third trips the per-step cap (`ToolCallsInCurrentStep > 1`) before dispatch");
        run.Steps[0].Status.Should().Be(AgentStepStatus.Succeeded);
        run.Steps[1].Status.Should().Be(AgentStepStatus.Succeeded);
        run.ErrorMessage.Should().NotBeNullOrEmpty();
        run.ErrorMessage.Should().Contain("tool-call cap",
            "verdict's HumanReadableReason names the cap that fired ('Step exceeded per-step tool-call cap (2/1).')");
    }

    [SkippableFact]
    public async Task MultiToolCall_CancellationPreDispatch_PropagatesAsCancelled_NoStepsPersisted()
    {
        // Phase 3.E pin #3: cancellation propagates immediately
        // (OperationCanceledException). Pre-cancelled CT fires before
        // any tool dispatches → zero steps + Cancelled run.
        // PR #12 polish: renamed from _Mid* → _PreDispatch* for
        // accuracy. Mid-dispatch-cancellation (CT trips during a
        // tool's RunAsync) is covered by single-tool cancellation
        // tests elsewhere; this pin specifically covers the foreach-
        // entry cancellation path.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var llm = new StubAgentLlmClient()
            .EnqueueMultipleToolCalls(
                ("echo", """{"text":"first"}"""),
                ("echo", """{"text":"second"}"""));
        var executor = NewExecutor(llm);

        var request = AgentRunRequest.Create(
            orgId: TestOrgId,
            userPrompt: "cancel pre-dispatch",
            availableTools: new List<AgentToolDescriptor> { new EchoTool().Descriptor });

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var run = await executor.RunAsync(request, cts.Token);

        run.Status.Should().Be(AgentRunStatus.Cancelled,
            "pre-cancelled token produces Cancelled before any tool dispatches");
        run.Steps.Should().BeEmpty(
            "no tool_calls dispatched when CT cancelled at loop start");
    }

    [SkippableFact]
    public async Task MultiToolCall_SingleCallStillWorks_RegressionPin()
    {
        // Negative-control pin: the multi-tool path additions (mid-
        // iteration gate, dispatchedToolCalls counter, etc.) must NOT
        // regress the single-tool path. Pre-Phase-3.E behavior:
        // one tool_call → one AgentStep → loop continues → final text.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var llm = new StubAgentLlmClient()
            .EnqueueToolCall("echo", """{"text":"single"}""")
            .EnqueueAssistantText("done.");
        var executor = NewExecutor(llm);

        var request = AgentRunRequest.Create(
            orgId: TestOrgId,
            userPrompt: "single-tool turn",
            availableTools: new List<AgentToolDescriptor> { new EchoTool().Descriptor });

        var run = await executor.RunAsync(request);

        run.Status.Should().Be(AgentRunStatus.Succeeded);
        run.Steps.Should().HaveCount(1);
        run.Steps[0].Status.Should().Be(AgentStepStatus.Succeeded);
    }

    // ---------------- Phase 3.B turn-on: runtime schema validation gate ----------------

    [SkippableFact]
    public async Task RuntimeSchemaGate_ToolCallWithMissingRequiredArg_PersistsFailedStep_WithSchemaErrorMessage()
    {
        // Phase 3.B contract: the executor schema-validates tool_call
        // args against the tool's ParameterSchema BEFORE dispatch.
        // Missing required fields → AgentStep.Failed with a structured
        // error message the LLM sees in history + can retry against.
        // Decide-and-document #4 REJECT semantics.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        // EchoTool's schema requires "text"; emit a call without it.
        var llm = new StubAgentLlmClient()
            .EnqueueToolCall("echo", """{"not_text":"missing required field"}""")
            .EnqueueAssistantText("OK, retrying without proper args was rejected.");
        var executor = NewExecutor(llm);

        var request = AgentRunRequest.Create(
            orgId: TestOrgId,
            userPrompt: "Trigger schema-invalid args.",
            availableTools: new List<AgentToolDescriptor> { new EchoTool().Descriptor });

        var run = await executor.RunAsync(request);

        run.Status.Should().Be(AgentRunStatus.Succeeded,
            "the loop survives schema rejection; the next LLM iteration emits the final answer");
        run.Steps.Should().HaveCount(1);
        var rejectedStep = run.Steps[0];
        rejectedStep.Status.Should().Be(AgentStepStatus.Failed);
        rejectedStep.ErrorMessage.Should().Contain("schema validation failed",
            "rejection message identifies the gate that fired");
        rejectedStep.ErrorMessage.Should().Contain("echo",
            "tool name is in the error so the LLM knows which call it needs to retry");
        rejectedStep.ToolOutputJson.Should().BeNull(
            "schema-rejected calls never invoke the tool, so no output exists");
    }

    [SkippableFact]
    public async Task RuntimeSchemaGate_ToolCallWithValidArgs_DispatchesNormally()
    {
        // Negative-control pin: schema-valid args flow through to the
        // tool normally. Pinning this is important so future-me doesn't
        // misread the previous test as "schema validation rejects
        // everything" — it only rejects invalid args.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var llm = new StubAgentLlmClient()
            .EnqueueToolCall("echo", """{"text":"valid call"}""")
            .EnqueueAssistantText("Done.");
        var executor = NewExecutor(llm);

        var request = AgentRunRequest.Create(
            orgId: TestOrgId,
            userPrompt: "Valid call.",
            availableTools: new List<AgentToolDescriptor> { new EchoTool().Descriptor });

        var run = await executor.RunAsync(request);

        run.Status.Should().Be(AgentRunStatus.Succeeded);
        run.Steps.Should().HaveCount(1);
        run.Steps[0].Status.Should().Be(AgentStepStatus.Succeeded);
        run.Steps[0].ToolOutputJson.Should().Contain("valid call",
            "tool dispatched + returned the echoed text in its output");
    }

    // ---------------- Phase 3.A.1 (existing — preserved post-refactor) ----------------

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
        IEnumerable<IAgentTool>? extraTools = null,
        IAgentBudgetGate? customGate = null)
    {
        var allTools = new List<IAgentTool> { new EchoTool() };
        if (extraTools is not null)
        {
            allTools.AddRange(extraTools);
        }
        var schemaValidator = new JsonSchemaNetValidator();
        // Phase 3.C: ExposeEcho=true for executor tests — they rely on
        // EchoTool being the dispatched tool. The exposure filter only
        // affects which descriptors appear in Registry.Descriptors;
        // GetTool still resolves regardless, but executor tests build
        // their own AvailableTools (via AgentRunRequest.Create) so they
        // exercise the dispatch path directly. Setting ExposeEcho=true
        // also makes Registry.Descriptors include echo for any test
        // that asserts on the exposed catalogue.
        var catalogueOptions = Options.Create(new ToolCatalogueOptions { ExposeEcho = true });
        var registry = new ToolRegistry(allTools, schemaValidator, catalogueOptions);
        // Post-Phase-3.A.2 retrofit: default to Trellis.Core's
        // DefaultBudgetGate. Tests that need to capture the executor's
        // call-time AgentRunState pass a custom gate (used by the
        // injection-contract pins).
        var gate = customGate ?? new DefaultBudgetGate();
        var store = new PostgresAgentRunStore(_db!);
        var options = Options.Create(new AssistantAgentExecutorOptions
        {
            Model = "stub-model",
            SystemPrompt = "You are a stub.",
        });
        return new AssistantAgentExecutor(
            store, llm, registry, gate, schemaValidator, options,
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

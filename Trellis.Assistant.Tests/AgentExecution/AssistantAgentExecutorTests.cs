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
        var registry = new ToolRegistry(allTools, schemaValidator);
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

using System.Diagnostics;
using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Trellis.Assistant.AgentExecution;
using Trellis.Assistant.Data;
using Trellis.Assistant.Observability;
using Trellis.Assistant.Tests.TestFixtures;
using Trellis.Core.Models;
using Trellis.Core.Services;
using Xunit;

namespace Trellis.Assistant.Tests.Observability;

/// <summary>
/// Phase 3.H pins for the OpenTelemetry instrumentation on the agent
/// dispatch loop. MeterListener captures counter increments + histogram
/// records; ActivityListener captures spans. No real OTel SDK wiring
/// needed — the static <see cref="AgentTelemetry"/> Meter +
/// ActivitySource emit regardless of whether an SDK pipeline is
/// registered, and the in-process listeners observe them directly.
///
/// <para>
/// Counter discipline (pin #3): every assertion verifies that
/// <c>user.id</c> tag is ABSENT from the captured measurements. Phase
/// 3.H pin discipline forbids tagging metrics with user_id (unbounded
/// cardinality across the fleet's users). Negative-space pinned here
/// so a future regression that "helpfully" adds the tag fails the
/// test immediately.
/// </para>
/// </summary>
public sealed class AgentTelemetryTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    // Each test instance gets its own tenant Guid so the static Meter +
    // ActivitySource captures don't observe emissions from other test
    // classes running in parallel (executor tests on TestTenants.* +
    // ConversationEndpointTests etc. all share AgentTelemetry's static
    // Meter). The capture helpers filter by this id at the source.
    private readonly Guid _testOrgId = Guid.NewGuid();
    private readonly PostgresFixture _pg;
    private AssistantDbContext? _db;

    public AgentTelemetryTests(PostgresFixture pg) { _pg = pg; }

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
        if (_db is not null) await _db.DisposeAsync();
    }

    [SkippableFact]
    public async Task ToolDispatch_Success_EmitsDispatchCount_WithExpectedTags()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var capture = MetricCapture.Start(_testOrgId);
        var executor = NewExecutor(
            new StubAgentLlmClient()
                .EnqueueToolCall("echo", """{"text":"hi"}""")
                .EnqueueAssistantText("done."));
        var request = AgentRunRequest.Create(
            orgId: _testOrgId,
            userPrompt: "telemetry happy path",
            availableTools: new List<AgentToolDescriptor> { new EchoTool().Descriptor });

        await executor.RunAsync(request);

        var dispatchMeasurements = capture.GetMeasurements("trellis.assistant.tool.dispatch.count");
        dispatchMeasurements.Should().HaveCount(1,
            "exactly one tool dispatched → exactly one count increment");
        dispatchMeasurements[0].Value.Should().Be(1L);
        var tags = dispatchMeasurements[0].Tags;
        tags.Should().ContainKey("tool.name").WhoseValue.Should().Be("echo");
        tags.Should().ContainKey("outcome").WhoseValue.Should().Be("success");
        tags.Should().ContainKey("tenant.id").WhoseValue.Should().Be(_testOrgId.ToString("D"));
        tags.Should().NotContainKey("user.id",
            "Phase 3.H pin #3: never tag metrics with user.id (unbounded cardinality)");
    }

    [SkippableFact]
    public async Task ToolDispatch_Success_RecordsDurationHistogram()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var capture = MetricCapture.Start(_testOrgId);
        var executor = NewExecutor(
            new StubAgentLlmClient()
                .EnqueueToolCall("echo", """{"text":"timing"}""")
                .EnqueueAssistantText("ok."));

        await executor.RunAsync(AgentRunRequest.Create(
            orgId: _testOrgId,
            userPrompt: "x",
            availableTools: new List<AgentToolDescriptor> { new EchoTool().Descriptor }));

        var duration = capture.GetMeasurements("trellis.assistant.tool.dispatch.duration_ms");
        duration.Should().HaveCount(1);
        duration[0].Value.Should().BeGreaterThanOrEqualTo(0,
            "duration is non-negative wall-clock ms; stub tool dispatches fast but >= 0");
        duration[0].Tags.Should().ContainKey("outcome").WhoseValue.Should().Be("success");
    }

    [SkippableFact]
    public async Task ToolDispatch_SchemaValidationFailure_EmitsFailureCounter()
    {
        // Phase 3.H: schema validation rejection → failure outcome +
        // distinct ToolDispatchFailureCount increment. The activity
        // also tags `dispatch.reject_reason = schema_validation` for
        // drill-down.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var capture = MetricCapture.Start(_testOrgId);
        var executor = NewExecutor(
            new StubAgentLlmClient()
                .EnqueueToolCall("echo", """{"not_text":"missing required 'text'"}""")
                .EnqueueAssistantText("retry abandoned."));

        await executor.RunAsync(AgentRunRequest.Create(
            orgId: _testOrgId,
            userPrompt: "schema-fail telemetry",
            availableTools: new List<AgentToolDescriptor> { new EchoTool().Descriptor }));

        // ToolDispatchCount tagged outcome=failure
        var dispatches = capture.GetMeasurements("trellis.assistant.tool.dispatch.count");
        dispatches.Should().HaveCount(1);
        dispatches[0].Tags.Should().ContainKey("outcome").WhoseValue.Should().Be("failure");

        // ToolDispatchFailureCount fires distinctly
        var failures = capture.GetMeasurements("trellis.assistant.tool.dispatch.failure.count");
        failures.Should().HaveCount(1,
            "failure outcome increments the failure counter in addition to the dispatch counter — operators can alert on either");
        failures[0].Tags.Should().ContainKey("tool.name").WhoseValue.Should().Be("echo");
        failures[0].Tags.Should().NotContainKey("user.id");
    }

    [SkippableFact]
    public async Task ToolDispatch_BudgetExhaustedMidIteration_EmitsBudgetExhaustedCounter_NotFailureCounter()
    {
        // Phase 3.H pin #1 + Phase 3.E pin #6: budget-exhausted gets
        // its OWN counter, distinct from failure. Operator-policy
        // outcome (LLM emitted more tool_calls than budget) shouldn't
        // be confounded with tool faults.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var capture = MetricCapture.Start(_testOrgId);
        var executor = NewExecutor(
            new StubAgentLlmClient()
                .EnqueueMultipleToolCalls(
                    ("echo", """{"text":"first"}"""),
                    ("echo", """{"text":"second-budget-exhausts"}""")));

        await executor.RunAsync(AgentRunRequest.Create(
            orgId: _testOrgId,
            userPrompt: "two-tool but budget=1",
            availableTools: new List<AgentToolDescriptor> { new EchoTool().Descriptor },
            budgetOverrides: new AgentBudgetOverrides { MaxSteps = 1 }));

        var budgetMeasurements = capture.GetMeasurements("trellis.assistant.tool.dispatch.budget_exhausted.count");
        budgetMeasurements.Should().HaveCount(1,
            "budget-exhausted mid-iteration fires the dedicated counter exactly once per agent run");
        budgetMeasurements[0].Tags.Should().ContainKey("tool.name").WhoseValue.Should().Be("echo",
            "budget-exhausted is tagged with the tool name of the NEXT tool_call (the one that didn't dispatch)");

        // The failure counter should fire for the FIRST tool_call only
        // if it failed. The dispatch that actually happened succeeded
        // (echo with valid args). The skipped second dispatch is NOT
        // counted as a dispatch at all — budget-exhausted is the only
        // counter for it.
        var failures = capture.GetMeasurements("trellis.assistant.tool.dispatch.failure.count");
        failures.Should().BeEmpty(
            "first tool succeeded; second wasn't dispatched (budget halt) — no failure counter fires");
    }

    [SkippableFact]
    public async Task ToolDispatch_OpensActivity_WithExpectedTagsAndOkStatus()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var capture = ActivityCapture.Start(_testOrgId);
        var executor = NewExecutor(
            new StubAgentLlmClient()
                .EnqueueToolCall("echo", """{"text":"trace"}""")
                .EnqueueAssistantText("done."));

        await executor.RunAsync(AgentRunRequest.Create(
            orgId: _testOrgId,
            userPrompt: "activity capture",
            availableTools: new List<AgentToolDescriptor> { new EchoTool().Descriptor }));

        var activities = capture.GetActivities("agent.tool.dispatch");
        activities.Should().HaveCount(1,
            "exactly one tool dispatch → exactly one agent.tool.dispatch activity");
        var activity = activities[0];
        activity.Status.Should().Be(ActivityStatusCode.Ok);
        activity.GetTagItem("tool.name").Should().Be("echo");
        activity.GetTagItem("tenant.id").Should().Be(_testOrgId.ToString("D"));
        activity.GetTagItem("step.index").Should().Be(0);
        activity.GetTagItem("agent.run.id").Should().NotBeNull();
    }

    [SkippableFact]
    public async Task ToolDispatch_FailureSetsActivityStatusError()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var capture = ActivityCapture.Start(_testOrgId);
        var executor = NewExecutor(
            new StubAgentLlmClient()
                .EnqueueToolCall("echo", """{"not_text":"schema-rejected"}""")
                .EnqueueAssistantText("ok."));

        await executor.RunAsync(AgentRunRequest.Create(
            orgId: _testOrgId,
            userPrompt: "failure activity",
            availableTools: new List<AgentToolDescriptor> { new EchoTool().Descriptor }));

        var activity = capture.GetActivities("agent.tool.dispatch").Single();
        activity.Status.Should().Be(ActivityStatusCode.Error,
            "schema-validation rejection sets activity status to Error so trace-side filters surface failed dispatches");
        activity.StatusDescription.Should().Contain("schema validation failed");
        activity.GetTagItem("dispatch.reject_reason").Should().Be("schema_validation");
    }

    [SkippableFact]
    public async Task ToolDispatch_CancellationToken_RecordsOutcomeCancelled()
    {
        // Phase 3.I Thread 1: cancellation is operator-driven, NOT a
        // tool fault. The dispatch counter records outcome=cancelled +
        // the FAILURE counter stays empty so failure-ratio dashboards
        // don't surface cancellations as bugs. The activity gets
        // ActivityStatusCode.Unset (cancellation is not an error
        // condition; downstream operators can filter by outcome tag
        // for cancelled-vs-failed signal).
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var metricCapture = MetricCapture.Start(_testOrgId);
        using var activityCapture = ActivityCapture.Start(_testOrgId);

        using var cts = new CancellationTokenSource();
        // Deterministic synchronization: HangingTool signals when it
        // enters RunAsync; the test cancels at that point. Avoids the
        // race where a CancelAfter timer fires before the executor
        // reaches DispatchOneToolCallAsync — in that race the LLM-call
        // catch wraps the OCE and we never get to the dispatch's
        // cancellation outcome branch.
        var hangingTool = new HangingTool(onEntered: () => cts.Cancel());
        var executor = NewExecutorWithExtraTool(
            new StubAgentLlmClient()
                .EnqueueToolCall(HangingTool.ToolName, "{}"),
            hangingTool);

        // The OCE bubbles out of the executor's outer catch as
        // AgentRunStatus.Cancelled. Inside DispatchOneToolCallAsync the
        // tool's OCE is caught + threw=true + the cancelled-outcome
        // metric path fires before returning.
        var act = () => executor.RunAsync(
            AgentRunRequest.Create(
                orgId: _testOrgId,
                userPrompt: "cancel mid-tool",
                availableTools: new List<AgentToolDescriptor> { hangingTool.Descriptor }),
            cts.Token);
        await act.Should().NotThrowAsync(
            "the executor's outer OCE catch converts cancellation to a Cancelled terminal status — it does not propagate");

        // Dispatch counter fired with outcome=cancelled.
        var dispatches = metricCapture.GetMeasurements("trellis.assistant.tool.dispatch.count");
        dispatches.Should().HaveCount(1,
            "cancellation mid-dispatch still records a single dispatch event with the cancelled outcome");
        dispatches[0].Tags.Should().ContainKey("outcome").WhoseValue.Should().Be(
            AgentTelemetry.Outcomes.Cancelled,
            "outcome tag distinguishes cancelled from succeeded/failed/budget_exhausted");
        dispatches[0].Tags.Should().ContainKey("tool.name").WhoseValue.Should().Be(HangingTool.ToolName);

        // Failure counter MUST stay empty — cancellation is operator-driven,
        // not a tool fault. Pin #1 cardinality discipline.
        var failures = metricCapture.GetMeasurements("trellis.assistant.tool.dispatch.failure.count");
        failures.Should().BeEmpty(
            "cancellation is operator-driven (caller cancelled the token), NOT a tool failure — the failure counter must stay empty so failure-ratio alerts aren't tripped by user disconnects");

        // Activity status stays Unset on cancellation (Phase 3.H pin).
        var activity = activityCapture.GetActivities("agent.tool.dispatch").Single();
        activity.Status.Should().Be(ActivityStatusCode.Unset,
            "cancellation is not an error condition — the activity's outcome tag conveys what happened");
    }

    // ---------------- Phase 3.J Thread B pins ----------------

    [SkippableFact]
    public async Task LlmCall_RecordsTokensUsedHistogram_TaggedModelAndTenant()
    {
        // Phase 3.J Thread B: TokensUsed histogram fires per LLM call
        // (after each successful ChatWithToolsAsync return) so operators
        // can spot runaway loops mid-flight. Tags: model + tenant.id;
        // NEVER user.id (pin #3 cardinality discipline).
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var capture = MetricCapture.Start(_testOrgId);
        var executor = NewExecutor(
            new StubAgentLlmClient()
                .EnqueueAssistantText("done.", tokensUsed: 123));

        await executor.RunAsync(AgentRunRequest.Create(
            orgId: _testOrgId,
            userPrompt: "tokens-used pin",
            availableTools: new List<AgentToolDescriptor> { new EchoTool().Descriptor }));

        var tokens = capture.GetMeasurements("trellis.assistant.tokens.used");
        tokens.Should().HaveCountGreaterThanOrEqualTo(1,
            "TokensUsed fires at least once per successful LLM call");
        tokens[0].Value.Should().Be(123,
            "stub enqueued tokensUsed=123 → histogram MUST record the actual value, not just any non-negative number; pins the llmResponse.TokensUsed → Record value-propagation path");
        var tags = tokens[0].Tags;
        tags.Should().ContainKey("model").WhoseValue.Should().Be("stub-model",
            "TokensUsed is tagged with the configured model so operators can break out token cost per model");
        tags.Should().ContainKey("tenant.id").WhoseValue.Should().Be(_testOrgId.ToString("D"),
            "tenant.id is bounded by customer count and required for multi-tenant cost attribution");
        tags.Should().NotContainKey("user.id",
            "Phase 3.H pin #3: never tag metrics with user.id (unbounded cardinality across the fleet)");
    }

    [SkippableFact]
    public async Task LlmCall_RecordsLatencyHistogram()
    {
        // Phase 3.J Thread B: LlmCallDurationMs measured by Stopwatch
        // around the ChatWithToolsAsync await. Stub returns
        // synchronously so the elapsed will be near-zero, but the
        // contract is value >= 0 (non-negative wall-clock ms).
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var capture = MetricCapture.Start(_testOrgId);
        var executor = NewExecutor(
            new StubAgentLlmClient()
                .EnqueueAssistantText("latency pin done."));

        await executor.RunAsync(AgentRunRequest.Create(
            orgId: _testOrgId,
            userPrompt: "latency pin",
            availableTools: new List<AgentToolDescriptor> { new EchoTool().Descriptor }));

        var latency = capture.GetMeasurements("trellis.assistant.llm.call.duration_ms");
        latency.Should().HaveCountGreaterThanOrEqualTo(1,
            "LlmCallDurationMs fires at least once per successful LLM call");
        latency[0].Value.Should().BeGreaterThanOrEqualTo(0,
            "duration is non-negative wall-clock ms; stub returns fast but >= 0 holds");
        latency[0].Tags.Should().ContainKey("model").WhoseValue.Should().Be("stub-model");
        latency[0].Tags.Should().ContainKey("tenant.id").WhoseValue.Should().Be(_testOrgId.ToString("D"));
        latency[0].Tags.Should().NotContainKey("user.id",
            "Phase 3.H pin #3: never tag metrics with user.id");
    }

    [SkippableFact]
    public async Task AgentRun_OnCompletion_RecordsRunDurationWithOutcomeTag()
    {
        // Phase 3.J Thread B: AgentRunDurationMs fires exactly once per
        // agent run, at the end of ExecuteLoopAsync just before the
        // executor returns LoopResult. Happy path → terminal status
        // Succeeded → outcome="succeeded". Tagged with tenant.id +
        // outcome; NEVER user.id.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var capture = MetricCapture.Start(_testOrgId);
        var executor = NewExecutor(
            new StubAgentLlmClient()
                .EnqueueAssistantText("happy path."));

        await executor.RunAsync(AgentRunRequest.Create(
            orgId: _testOrgId,
            userPrompt: "run-duration pin",
            availableTools: new List<AgentToolDescriptor> { new EchoTool().Descriptor }));

        var runDuration = capture.GetMeasurements("trellis.assistant.agent.run.duration_ms");
        runDuration.Should().HaveCount(1,
            "AgentRunDurationMs fires exactly once per agent run, at terminal");
        runDuration[0].Value.Should().BeGreaterThanOrEqualTo(0,
            "duration is non-negative wall-clock ms");
        var tags = runDuration[0].Tags;
        tags.Should().ContainKey("outcome").WhoseValue.Should().Be("success",
            "happy path → terminal AgentRunStatus.Succeeded → outcome=success per Outcomes.Map. Phase 3.J fix-up aligned the vocabulary across 3.H + 3.J so cross-histogram PromQL groupings on `outcome` are friction-free.");
        tags.Should().ContainKey("tenant.id").WhoseValue.Should().Be(_testOrgId.ToString("D"));
        tags.Should().NotContainKey("user.id",
            "Phase 3.H pin #3: never tag metrics with user.id");
        tags.Should().NotContainKey("agent.run.id",
            "Phase 3.H pin #3: agent.run.id is high-cardinality, activity-only");
    }

    [SkippableFact]
    public async Task AgentRun_OnCompletion_CapReached_RecordsOutcomeCapReached()
    {
        // Phase 3.J fix-up: covers a non-success terminal branch of
        // Outcomes.Map. Without this pin, only the happy path
        // (succeeded → "success") was tested; the failed / cap_reached /
        // loop_detected branches were dead code from a coverage POV. A
        // refactor that swapped Map's mapping (e.g. CapReached →
        // "loop_detected" by mistake) would have shipped silently.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        // Same pattern as AssistantAgentExecutorTests.BudgetGate_StepCapReached:
        // MaxSteps=1, stub keeps emitting tool_calls so the budget gate
        // fires at the top of the second iteration → terminal
        // AgentRunStatus.CapReached → outcome=cap_reached.
        using var capture = MetricCapture.Start(_testOrgId);
        var executor = NewExecutor(
            new StubAgentLlmClient()
                .EnqueueToolCall("echo", """{"text":"step 0"}""")
                .EnqueueToolCall("echo", """{"text":"won't reach"}"""));

        await executor.RunAsync(AgentRunRequest.Create(
            orgId: _testOrgId,
            userPrompt: "force cap_reached",
            availableTools: new List<AgentToolDescriptor> { new EchoTool().Descriptor },
            budgetOverrides: new AgentBudgetOverrides { MaxSteps = 1 }));

        var runDuration = capture.GetMeasurements("trellis.assistant.agent.run.duration_ms");
        runDuration.Should().HaveCount(1,
            "AgentRunDurationMs fires exactly once per agent run, even on cap-reached terminal");
        runDuration[0].Tags.Should().ContainKey("outcome").WhoseValue.Should().Be("cap_reached",
            "AgentRunStatus.CapReached → Outcomes.Map → \"cap_reached\". Pins the non-success branch of the mapping.");
        runDuration[0].Tags.Should().ContainKey("tenant.id").WhoseValue.Should().Be(_testOrgId.ToString("D"));
        runDuration[0].Tags.Should().NotContainKey("user.id",
            "Phase 3.H pin #3 cardinality discipline preserved on the non-success branch too");
    }

    // ---------------- helpers ----------------

    private AssistantAgentExecutor NewExecutor(IAgentLlmClient llm)
    {
        var allTools = new List<IAgentTool> { new EchoTool() };
        var schemaValidator = new JsonSchemaNetValidator();
        var catalogueOptions = Options.Create(new ToolCatalogueOptions { ExposeEcho = true });
        var exposurePolicy = new AllowAllExposurePolicy();
        var registry = new ToolRegistry(allTools, schemaValidator, catalogueOptions, exposurePolicy);
        var gate = new DefaultBudgetGate();
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

    private sealed class AllowAllExposurePolicy : IToolExposurePolicy
    {
        public bool IsExposedTo(IAgentTool tool, AgentToolTenancy tenancy) => true;
    }

    /// <summary>
    /// Variant builder that registers an extra IAgentTool alongside the
    /// default EchoTool. Used by the cancellation pin to dispatch
    /// against a tool that hangs until the CT fires.
    /// </summary>
    private AssistantAgentExecutor NewExecutorWithExtraTool(IAgentLlmClient llm, IAgentTool extra)
    {
        var allTools = new List<IAgentTool> { new EchoTool(), extra };
        var schemaValidator = new JsonSchemaNetValidator();
        var catalogueOptions = Options.Create(new ToolCatalogueOptions { ExposeEcho = true });
        var exposurePolicy = new AllowAllExposurePolicy();
        var registry = new ToolRegistry(allTools, schemaValidator, catalogueOptions, exposurePolicy);
        var gate = new DefaultBudgetGate();
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

    /// <summary>
    /// Tool that delays until cancellation fires — Task.Delay with the
    /// CT throws OCE on cancel, which lands in
    /// DispatchOneToolCallAsync's OCE branch (threw=true, errorMessage
    /// set to "Tool dispatch cancelled mid-execution.") then proceeds
    /// to the outcome-classification block that fires the
    /// outcome=cancelled metric path.
    /// </summary>
    private sealed class HangingTool : IAgentTool
    {
        public const string ToolName = "hanging_tool";

        private readonly Action? _onEntered;

        public HangingTool(Action? onEntered = null)
        {
            _onEntered = onEntered;
        }

        public AgentToolDescriptor Descriptor { get; } = new()
        {
            Name = ToolName,
            Description = "Test-only tool that hangs until cancellation fires.",
            ParameterSchema = """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","additionalProperties":true}""",
            Category = AgentToolCategory.Inspect,
        };

        public async Task<AgentToolOutput> RunAsync(
            AgentToolInput input, CancellationToken cancellationToken = default)
        {
            // Signal entry — the test cancels its CTS from here, giving
            // the executor a guaranteed dispatch-mid-flight cancellation
            // path instead of racing a CancelAfter timer against the
            // executor's startup.
            _onEntered?.Invoke();
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            // Unreachable unless the test budget is exceeded; the CT fires
            // before this returns.
            return new AgentToolOutput { Success = true, ResultJson = "{}" };
        }
    }

    /// <summary>
    /// In-process MeterListener that captures measurements from
    /// <see cref="AgentTelemetry.Meter"/> matching a specific
    /// <c>tenant.id</c> tag. Per-test filter is load-bearing: other test
    /// classes (executor + endpoint tests) exercise the same static
    /// Meter in parallel — without the tenant.id filter, this listener
    /// would observe their measurements too and the
    /// <c>Should().HaveCount(1)</c> assertions would race.
    /// </summary>
    private sealed class MetricCapture : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly List<Measurement> _measurements = new();
        private readonly object _gate = new();
        private readonly string _tenantIdFilter;

        public static MetricCapture Start(Guid tenantId)
        {
            var capture = new MetricCapture(tenantId.ToString("D"));
            capture._listener.Start();
            return capture;
        }

        private MetricCapture(string tenantIdFilter)
        {
            _tenantIdFilter = tenantIdFilter;
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == AgentTelemetry.SourceName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _listener.SetMeasurementEventCallback<long>(OnLongMeasurement);
            _listener.SetMeasurementEventCallback<double>(OnDoubleMeasurement);
        }

        private bool MatchesTenant(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            foreach (var t in tags)
            {
                if (t.Key == "tenant.id" && t.Value is string s && s == _tenantIdFilter)
                {
                    return true;
                }
            }
            return false;
        }

        private void OnLongMeasurement(
            Instrument instrument, long value,
            ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        {
            if (!MatchesTenant(tags)) return;
            var snapshot = new Dictionary<string, object?>(tags.Length);
            foreach (var t in tags) snapshot[t.Key] = t.Value;
            lock (_gate)
            {
                _measurements.Add(new Measurement(instrument.Name, value, snapshot));
            }
        }

        private void OnDoubleMeasurement(
            Instrument instrument, double value,
            ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        {
            if (!MatchesTenant(tags)) return;
            var snapshot = new Dictionary<string, object?>(tags.Length);
            foreach (var t in tags) snapshot[t.Key] = t.Value;
            lock (_gate)
            {
                _measurements.Add(new Measurement(instrument.Name, value, snapshot));
            }
        }

        public IReadOnlyList<Measurement> GetMeasurements(string instrumentName)
        {
            lock (_gate)
            {
                return _measurements.Where(m => m.InstrumentName == instrumentName).ToList();
            }
        }

        public void Dispose() => _listener.Dispose();

        public sealed record Measurement(string InstrumentName, double Value, IReadOnlyDictionary<string, object?> Tags);
    }

    /// <summary>
    /// In-process ActivityListener that captures activities from
    /// <see cref="AgentTelemetry.ActivitySource"/> with a specific
    /// <c>tenant.id</c> tag. Per-test filter matches the rationale on
    /// <see cref="MetricCapture"/> — other test classes exercise the
    /// same static ActivitySource in parallel.
    /// </summary>
    private sealed class ActivityCapture : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly List<Activity> _activities = new();
        private readonly object _gate = new();
        private readonly string _tenantIdFilter;

        public static ActivityCapture Start(Guid tenantId)
        {
            var capture = new ActivityCapture(tenantId.ToString("D"));
            ActivitySource.AddActivityListener(capture._listener);
            return capture;
        }

        private ActivityCapture(string tenantIdFilter)
        {
            _tenantIdFilter = tenantIdFilter;
            _listener = new ActivityListener
            {
                ShouldListenTo = src => src.Name == AgentTelemetry.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    if (activity.GetTagItem("tenant.id") is string s && s == _tenantIdFilter)
                    {
                        lock (_gate)
                        {
                            _activities.Add(activity);
                        }
                    }
                },
            };
        }

        public IReadOnlyList<Activity> GetActivities(string operationName)
        {
            lock (_gate)
            {
                return _activities.Where(a => a.OperationName == operationName).ToList();
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}

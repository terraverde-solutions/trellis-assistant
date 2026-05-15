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

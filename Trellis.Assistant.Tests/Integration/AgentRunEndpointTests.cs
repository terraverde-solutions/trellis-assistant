using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Trellis.Assistant.Endpoints;
using Trellis.Assistant.Middleware;
using Trellis.Assistant.Tests.TestFixtures;
using Xunit;

namespace Trellis.Assistant.Tests.Integration;

/// <summary>
/// End-to-end integration tests for the Phase 3.A.1 agent-run surface.
/// Stub-driven: the <see cref="AssistantWebApplicationFactory"/> swaps
/// both <c>IOllamaClient</c> and <c>IAgentLlmClient</c> with
/// test-controlled stubs. Real Postgres via Testcontainers; the agent
/// run + step persistence path is exercised end-to-end.
///
/// X1 split: stub-driven tests verify the executor + endpoint surface
/// invariants; the OLLAMA_BASE_URL-gated <c>RealOllamaSmokeTests</c>
/// verifies the real-LLM path.
/// </summary>
public sealed class AgentRunEndpointTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private AssistantWebApplicationFactory? _factory;

    public AgentRunEndpointTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    public Task InitializeAsync()
    {
        if (_pg.IsAvailable)
        {
            _factory = new AssistantWebApplicationFactory(_pg.ConnectionString);
        }
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    private HttpClient NewClient(
        string? tenantId = TestTenants.TenantA,
        string? userId = TestTenants.UserA)
    {
        var client = _factory!.CreateClient();
        if (tenantId is not null)
        {
            client.DefaultRequestHeaders.Add(TenantClaimsMiddleware.TenantHeaderName, tenantId);
        }
        if (userId is not null)
        {
            client.DefaultRequestHeaders.Add(TenantClaimsMiddleware.UserHeaderName, userId);
        }
        return client;
    }

    [SkippableFact]
    public async Task PostAgentRuns_ValidRequest_ReturnsRunWithSucceededStatus()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        _factory!.AgentLlmStub.EnqueueAssistantText("Final answer.", tokensUsed: 50);

        using var client = NewClient();
        var resp = await client.PostAsJsonAsync(
            "/api/agent-runs",
            new AgentRunEndpoints.CreateAgentRunRequest(
                UserPrompt: "What's the capital of France?",
                ToolNames: null,
                MaxSteps: null));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<AgentRunEndpoints.CreateAgentRunResponse>();
        body.Should().NotBeNull();
        body!.Status.Should().Be("succeeded");
        body.Plan.Should().Contain("LLM-driven");
        body.TokensUsed.Should().Be(50);
        body.Steps.Should().BeEmpty(
            "no tool_calls from the stub means no steps dispatched");
        body.Id.Should().NotBeNullOrEmpty();
        Ulid.TryParse(body.Id, out _).Should().BeTrue("agent run id is wire-encoded as 26-char ulid");
    }

    [SkippableFact]
    public async Task PostAgentRuns_ToolCallThenAssistantText_PersistsStepInResponse()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        _factory!.AgentLlmStub
            .EnqueueToolCall("echo", """{"text":"hello from stub"}""", tokensUsed: 30)
            .EnqueueAssistantText("Done.", tokensUsed: 20);

        using var client = NewClient();
        var resp = await client.PostAsJsonAsync(
            "/api/agent-runs",
            new AgentRunEndpoints.CreateAgentRunRequest(
                UserPrompt: "Echo something.",
                ToolNames: null,
                MaxSteps: null));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<AgentRunEndpoints.CreateAgentRunResponse>();
        body!.Status.Should().Be("succeeded");
        body.Steps.Should().HaveCount(1);
        body.Steps[0].ToolName.Should().Be("echo");
        body.Steps[0].Status.Should().Be("succeeded");
        body.Steps[0].StepIndex.Should().Be(0);
        body.Steps[0].ToolOutputJson.Should().Contain("\"output\":\"hello from stub\"");
        body.TokensUsed.Should().Be(50);
    }

    [SkippableFact]
    public async Task PostAgentRuns_MaxStepsOverride_HaltsWithCapReached()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        _factory!.AgentLlmStub
            .EnqueueToolCall("echo", """{"text":"step 0"}""")
            .EnqueueToolCall("echo", """{"text":"step 1 different"}""")
            .EnqueueToolCall("echo", """{"text":"step 2 also different"}""")
            .EnqueueAssistantText("won't reach");

        using var client = NewClient();
        var resp = await client.PostAsJsonAsync(
            "/api/agent-runs",
            new AgentRunEndpoints.CreateAgentRunRequest(
                UserPrompt: "Loop a bit.",
                ToolNames: null,
                MaxSteps: 2));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<AgentRunEndpoints.CreateAgentRunResponse>();
        body!.Status.Should().Be("cap_reached",
            "MaxSteps=2 override + 3 tool calls queued → gate halts after step 2 with cap_reached wire status");
        body.Steps.Should().HaveCount(2);
    }

    [SkippableFact]
    public async Task PostAgentRuns_MissingPrompt_Returns400()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var client = NewClient();
        var resp = await client.PostAsJsonAsync(
            "/api/agent-runs",
            new AgentRunEndpoints.CreateAgentRunRequest(UserPrompt: "", ToolNames: null, MaxSteps: null));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [SkippableFact]
    public async Task PostAgentRuns_MissingTenantHeader_Returns401()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var client = NewClient(tenantId: null, userId: TestTenants.UserA);
        var resp = await client.PostAsJsonAsync(
            "/api/agent-runs",
            new AgentRunEndpoints.CreateAgentRunRequest(UserPrompt: "Hi", ToolNames: null, MaxSteps: null));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [SkippableFact]
    public async Task PostAgentRuns_NonUuidTenantId_Returns400()
    {
        // Phase 3.A C1 contract: agent runs require uuid-shaped tenant
        // identifiers. A non-uuid tenant slipping through TenantClaimsMiddleware
        // (which only checks non-blank) gets 400 here rather than silently
        // mis-mapping to a random Guid via Guid.Parse failure.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var client = NewClient(tenantId: "not-a-uuid", userId: TestTenants.UserA);
        var resp = await client.PostAsJsonAsync(
            "/api/agent-runs",
            new AgentRunEndpoints.CreateAgentRunRequest(UserPrompt: "Hi", ToolNames: null, MaxSteps: null));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [SkippableFact]
    public async Task PostAgentRuns_ToolNamesFilter_RestrictsCatalogue()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        // Filter out the only registered tool — the stub LLM still emits
        // a tool_call for "echo" but the executor's resolvableTools list
        // is empty, so the call lands as a Failed step (tool not in
        // filtered catalogue → not in registry from executor's POV).
        // Actually: the registry-level GetTool still resolves "echo";
        // the executor passes ONLY filtered descriptors to the LLM but
        // dispatches via the registry. So a model emitting a non-filtered
        // tool name still dispatches against the registry. Either way,
        // this test pins that the filter passes through to the planner —
        // by asserting the AgentLlmStub's recorded tool count.
        _factory!.AgentLlmStub.EnqueueAssistantText("nothing to do");

        using var client = NewClient();
        var resp = await client.PostAsJsonAsync(
            "/api/agent-runs",
            new AgentRunEndpoints.CreateAgentRunRequest(
                UserPrompt: "Don't call any tools.",
                ToolNames: new[] { "nonexistent_tool" },
                MaxSteps: null));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        // Filter resolves to empty descriptor list (no registered tool
        // matches the filter). Stub recorded the call with 0 tools.
        _factory.AgentLlmStub.Calls.Should().HaveCountGreaterOrEqualTo(1);
        _factory.AgentLlmStub.Calls[0].ToolCount.Should().Be(0,
            "the filter intersected with registered tools = empty descriptor list = planner sees 0 tools");
    }

    [SkippableFact]
    public async Task PostAgentRuns_PlanField_IsHumanReadableString_NotJson()
    {
        // Phase 3.A C2 ratification mirror: Plan field stays a string at
        // the wire layer too — the JSON response carries it as a string.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        _factory!.AgentLlmStub.EnqueueAssistantText("done");

        using var client = NewClient();
        var resp = await client.PostAsJsonAsync(
            "/api/agent-runs",
            new AgentRunEndpoints.CreateAgentRunRequest(
                UserPrompt: "Test plan shape.",
                ToolNames: null,
                MaxSteps: null));

        var body = await resp.Content.ReadFromJsonAsync<AgentRunEndpoints.CreateAgentRunResponse>();
        body!.Plan.Should().Contain("LLM-driven");
        body.Plan.Should().Contain("user prompt:");
        // Plan must NOT parse as JSON — same anti-mutation pin as the
        // executor-tier test.
        var act = () => System.Text.Json.JsonDocument.Parse(body.Plan);
        act.Should().Throw<System.Text.Json.JsonException>();
    }
}

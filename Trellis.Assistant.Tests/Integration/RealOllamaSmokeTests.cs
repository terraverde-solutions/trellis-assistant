using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Trellis.Assistant.Endpoints;
using Trellis.Assistant.Middleware;
using Trellis.Assistant.Tests.TestFixtures;
using Xunit;

namespace Trellis.Assistant.Tests.Integration;

/// <summary>
/// Phase 2 end-to-end smoke against a real Ollama server. Gated on the
/// <c>OLLAMA_BASE_URL</c> environment variable — local-dev runs this
/// against laptop Ollama; CI skips unless the env var is set. Runs
/// against a Testcontainers-backed Postgres so the persistence path is
/// real too.
///
/// The X1-confirmed split: stub-driven tests in
/// <see cref="ConversationEndpointTests"/> verify orchestrator/store/
/// lock invariants WITHOUT real Ollama. This file verifies the
/// real-LLM path:
///   - ConversationOrchestrator's read-history → call-LLM → persist
///     loop end-to-end with the real Ollama streaming pipeline
///   - The conv.Model selection (Phase 2 Q4=B) actually reaches
///     OllamaClient.StreamChatAsync
///   - The 180s TurnRequestTimeoutSeconds is generous enough for a
///     cold-loaded model on the test box
///
/// Configuration:
///   - <c>OLLAMA_BASE_URL</c> (required to run): URL of the Ollama
///     server, e.g. <c>http://localhost:11434/</c> or
///     <c>http://gb10:11434/</c>. The trailing slash matches
///     OllamaClient's URI-relative-path expectations.
///   - <c>OLLAMA_TEST_MODEL</c> (optional): model tag to test
///     against. Defaults to <c>mistral-small:24b</c> — matches the
///     Phase 2 default + GB10's loaded model set. Override to the
///     smallest model your dev box has loaded for faster iterations.
///
/// Upper-bound runtime: 240s — covers a cold 70B model load (60-90s)
/// + token-heavy response (60-90s) + 60s margin for test-machine
/// variability. Faster boxes complete in seconds.
/// </summary>
public sealed class RealOllamaSmokeTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private RealOllamaWebApplicationFactory? _factory;

    public RealOllamaSmokeTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    public Task InitializeAsync()
    {
        var ollamaBaseUrl = Environment.GetEnvironmentVariable("OLLAMA_BASE_URL");
        if (_pg.IsAvailable && !string.IsNullOrWhiteSpace(ollamaBaseUrl))
        {
            _factory = new RealOllamaWebApplicationFactory(_pg.ConnectionString, ollamaBaseUrl);
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

    [SkippableFact]
    public async Task PostTurns_AgainstRealOllama_ReturnsNonEmptyAssistantReply()
    {
        var ollamaBaseUrl = Environment.GetEnvironmentVariable("OLLAMA_BASE_URL");
        Skip.If(string.IsNullOrWhiteSpace(ollamaBaseUrl),
            "OLLAMA_BASE_URL not set; real-Ollama integration smoke skipped. Set the env var to a reachable Ollama (e.g. http://localhost:11434/) to run this test.");
        Skip.IfNot(_pg.IsAvailable,
            "Docker not available; Testcontainers integration test skipped.");

        var testModel = Environment.GetEnvironmentVariable("OLLAMA_TEST_MODEL")
            ?? "mistral-small:24b";

        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Add(TenantClaimsMiddleware.TenantHeaderName, TestTenants.TenantRealLlm);
        client.DefaultRequestHeaders.Add(TenantClaimsMiddleware.UserHeaderName, TestTenants.UserRealLlm);

        // Create a conversation with the test model pinned.
        var convResp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api", Model: testModel));
        convResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var conv = await convResp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();
        conv!.Model.Should().Be(testModel,
            "the conversation must pin the caller-supplied model — orchestrator reads conv.Model when calling Ollama");

        // Single-turn request. The prompt is intentionally short so
        // even a slow box returns within the 240s upper bound. We don't
        // assert on the exact response content (LLMs are non-deterministic);
        // we assert on the path: status 200 + non-empty content + role
        // alternation + duration cap.
        var sw = Stopwatch.StartNew();
        var turnResp = await client.PostAsJsonAsync(
            $"/api/conversations/{conv.Id}/turns",
            new ConversationEndpoints.AppendTurnRequest(Content: "Reply with the single word: pong"));
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(240),
            $"real-Ollama integration smoke must complete within the 240s upper bound (180s server-side timeout + 60s margin); actual was {sw.Elapsed.TotalSeconds:0.0}s");

        turnResp.StatusCode.Should().Be(HttpStatusCode.OK,
            "real Ollama call must return 200 — a 504 means the 180s server-side timeout fired (Ollama too slow on this box?), a 502/500 means OllamaClient surfaced an upstream error");

        var turnBody = await turnResp.Content.ReadFromJsonAsync<ConversationEndpoints.AppendTurnResponse>();
        turnBody.Should().NotBeNull();

        // Role + position invariants — same shape as the stub-driven
        // tests, verifying the orchestrator's contract holds against
        // real Ollama too.
        turnBody!.UserTurn.Role.Should().Be("user");
        turnBody.UserTurn.Content.Should().Be("Reply with the single word: pong");
        turnBody.UserTurn.Position.Should().Be(0);
        turnBody.AssistantTurn.Role.Should().Be("assistant");
        turnBody.AssistantTurn.Position.Should().Be(1);
        turnBody.AssistantTurn.Content.Should().NotBeNullOrEmpty(
            "real Ollama must return non-empty assistant content — the orchestrator's StringBuilder accumulated zero chunks would surface as empty content here");

        // GET reads back both turns in position order.
        var getResp = await client.GetAsync($"/api/conversations/{conv.Id}");
        getResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var getBody = await getResp.Content.ReadFromJsonAsync<ConversationEndpoints.GetConversationResponse>();
        getBody!.Turns.Should().HaveCount(2);
        getBody.Model.Should().Be(testModel);
    }

    [SkippableFact]
    public async Task PostAgentRuns_WithEchoTool_DispatchesAndReturnsSucceeded()
    {
        // Phase 3.A.1 real-LLM smoke. Gated on OLLAMA_BASE_URL +
        // (optionally) OLLAMA_TEST_MODEL — defaults to qwen2.5:72b per
        // Phase 3.A C3 ratification (known tool-supporting model on
        // GB10). Verifies the full real-LLM agentic loop: prompt → LLM
        // emits a tool_call → executor dispatches EchoTool → next LLM
        // call sees the tool result → emits final assistant text → run
        // terminates Succeeded.
        //
        // The prompt is engineered to nudge tool use (asks the model
        // to use a specific tool by name); LLMs are non-deterministic,
        // so the assertion shape allows either:
        //   - At least one EchoTool dispatch persisted (the happy path
        //     this test is built for)
        //   - Zero dispatches but Succeeded status (model decided not
        //     to use the tool — also a legal outcome; still proves the
        //     loop ran end-to-end)
        // The test FAILS only if the run errors or the loop never
        // terminates within the 240s upper bound.
        var ollamaBaseUrl = Environment.GetEnvironmentVariable("OLLAMA_BASE_URL");
        Skip.If(string.IsNullOrWhiteSpace(ollamaBaseUrl),
            "OLLAMA_BASE_URL not set; real-Ollama agent-run smoke skipped.");
        Skip.IfNot(_pg.IsAvailable,
            "Docker not available; Testcontainers integration test skipped.");

        var agentModel = Environment.GetEnvironmentVariable("OLLAMA_TEST_MODEL")
            ?? "qwen2.5:72b";

        await using var factory = new RealOllamaWebApplicationFactory(
            _pg.ConnectionString,
            ollamaBaseUrl,
            agentModel: agentModel);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TenantClaimsMiddleware.TenantHeaderName, TestTenants.TenantRealLlm);
        client.DefaultRequestHeaders.Add(TenantClaimsMiddleware.UserHeaderName, TestTenants.UserRealLlm);

        var sw = Stopwatch.StartNew();
        var resp = await client.PostAsJsonAsync(
            "/api/agent-runs",
            new AgentRunEndpoints.CreateAgentRunRequest(
                UserPrompt: "Use the echo tool to repeat the word 'pong' back to me. Then give a final assistant message confirming you did it.",
                ToolNames: null,
                MaxSteps: 5));
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(240),
            $"real-Ollama agent-run smoke must complete within the 240s upper bound; actual was {sw.Elapsed.TotalSeconds:0.0}s");

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "the agent run endpoint returns 200 even when the underlying run halts on cap_reached / loop_detected — only outright errors surface as non-2xx");

        var body = await resp.Content.ReadFromJsonAsync<AgentRunEndpoints.CreateAgentRunResponse>();
        body.Should().NotBeNull();

        // Acceptable terminal statuses: succeeded (model used or skipped
        // the tool + emitted final text) OR cap_reached (model loop —
        // budget gate halted, also valid for the smoke since it pins
        // the loop ran). loop_detected is also valid if the model
        // emitted the same call repeatedly. Outright failed = real
        // problem.
        body!.Status.Should().BeOneOf(new[] { "succeeded", "cap_reached", "loop_detected" },
            $"real-LLM agent run terminated with status='{body.Status}', plan='{body.Plan}', error='{body.ErrorMessage ?? "(none)"}'");
        body.Plan.Should().Contain("LLM-driven");

        // Steps may be empty (model went straight to final text) or
        // populated (model used echo at least once). Both shapes pass.
        // If populated, every step must have a valid status string.
        foreach (var step in body.Steps)
        {
            step.Status.Should().BeOneOf(new[] { "succeeded", "failed", "skipped" });
            step.ToolName.Should().NotBeNullOrWhiteSpace();
        }
    }

    [SkippableFact]
    public async Task PostTurns_AgentPath_WithEchoTool_PersistsToolTurnAndAssistantReply()
    {
        // Phase 3.A.2 conversation-integrated agent-path real-LLM smoke.
        // Gated on OLLAMA_BASE_URL + OLLAMA_TEST_MODEL (defaults to
        // qwen2.5:72b — known tool-supporting model on GB10 per
        // Phase 3.A C3). Verifies the full conversation→executor→
        // tool dispatch→tool turn persistence→final assistant turn flow
        // end-to-end against a real LLM.
        //
        // Mutation pin per Phase 3.A.2 non-negotiables: tool turn
        // appears inline in the conversation Turns array on GET
        // (Option A wire shape) when the agent path runs.
        var ollamaBaseUrl = Environment.GetEnvironmentVariable("OLLAMA_BASE_URL");
        Skip.If(string.IsNullOrWhiteSpace(ollamaBaseUrl),
            "OLLAMA_BASE_URL not set; real-Ollama agent-path conversation smoke skipped.");
        Skip.IfNot(_pg.IsAvailable,
            "Docker not available; Testcontainers integration test skipped.");

        var agentModel = Environment.GetEnvironmentVariable("OLLAMA_TEST_MODEL")
            ?? "qwen2.5:72b";

        await using var factory = new RealOllamaWebApplicationFactory(
            _pg.ConnectionString,
            ollamaBaseUrl,
            agentModel: agentModel);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TenantClaimsMiddleware.TenantHeaderName, TestTenants.TenantRealLlm);
        client.DefaultRequestHeaders.Add(TenantClaimsMiddleware.UserHeaderName, TestTenants.UserRealLlm);

        // Create the conversation. Phase 2's per-conversation Model
        // is set; the agent path uses Assistant:Agent:Model (overridden
        // to OLLAMA_TEST_MODEL above) for the tool-using LLM call.
        var convResp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        var conv = await convResp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();
        conv!.Id.Should().NotBeNullOrEmpty();

        // POST /turns with Tools field — routes through the executor.
        // Prompt nudges tool use but allows model latitude (LLMs are
        // non-deterministic).
        var sw = Stopwatch.StartNew();
        var turnResp = await client.PostAsJsonAsync(
            $"/api/conversations/{conv.Id}/turns",
            new ConversationEndpoints.AppendTurnRequest(
                "Use the echo tool to repeat the word 'pong' back, then confirm in a final assistant message.")
            {
                Tools = new[] { "echo" },
            });
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(240),
            $"real-LLM agent-path smoke must complete within 240s; actual was {sw.Elapsed.TotalSeconds:0.0}s");
        turnResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var turnBody = await turnResp.Content.ReadFromJsonAsync<ConversationEndpoints.AppendTurnResponse>();
        turnBody!.UserTurn.Role.Should().Be("user");
        turnBody.AssistantTurn.Role.Should().Be("assistant");
        turnBody.AssistantTurn.Content.Should().NotBeNullOrEmpty();

        // Acceptable: ToolTurns null (model decided not to use the tool;
        // also valid agent-path outcome) OR populated (model used echo
        // at least once). If populated, each tool turn must have
        // role=tool + ToolCallId + ToolName.
        if (turnBody.ToolTurns is { Count: > 0 } toolTurns)
        {
            foreach (var tt in toolTurns)
            {
                tt.Role.Should().Be("tool");
                tt.ToolCallId.Should().NotBeNullOrEmpty();
                tt.ToolName.Should().NotBeNullOrEmpty();
            }

            // GET pin: full chain inline (Option A).
            var getResp = await client.GetAsync($"/api/conversations/{conv.Id}");
            var getBody = await getResp.Content.ReadFromJsonAsync<ConversationEndpoints.GetConversationResponse>();
            var roles = getBody!.Turns.Select(t => t.Role).ToList();
            roles.Should().Contain("tool",
                "Option A wire shape: tool turns appear inline in GET conversation Turns array when agent path ran");
        }
    }

    [SkippableFact]
    public async Task PostTurns_WithInvalidModel_ReturnsUpstream502()
    {
        // Phase 2 ships no allowlist on the model parameter. An invalid
        // model tag (one Ollama doesn't have loaded) surfaces as a
        // 4xx/5xx from Ollama on the first turn — OllamaClient's
        // EnsureSuccessStatusCode throws HttpRequestException; the
        // endpoint catches that and maps to 502 Bad Gateway with the
        // upstream message in the problem detail body.
        //
        // The 502 mapping is the "upstream gateway returned an error"
        // contract — operators pattern-match journalctl on it to
        // distinguish "Ollama said no" from "Assistant crashed."
        // Without this catch, the failure would leak as a 500 + a
        // stack trace, which is both noisier + harder to diagnose.
        var ollamaBaseUrl = Environment.GetEnvironmentVariable("OLLAMA_BASE_URL");
        Skip.If(string.IsNullOrWhiteSpace(ollamaBaseUrl),
            "OLLAMA_BASE_URL not set; real-Ollama upstream-502 pin skipped.");
        Skip.IfNot(_pg.IsAvailable,
            "Docker not available; Testcontainers integration test skipped.");

        // Intentionally bogus model tag — guaranteed to not be loaded
        // on any reasonable Ollama setup. Ollama returns 404 for
        // unknown model tags on /api/chat.
        const string invalidModel = "nonexistent-model:phase2-pin-9999";

        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Add(TenantClaimsMiddleware.TenantHeaderName, TestTenants.TenantBadModel);
        client.DefaultRequestHeaders.Add(TenantClaimsMiddleware.UserHeaderName, TestTenants.UserBadModel);

        var convResp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api", Model: invalidModel));
        // Conversation create succeeds — no allowlist at create time
        // is the v0 design. The 502 surfaces only when the orchestrator
        // tries to call StreamChatAsync with the invalid tag on the
        // first turn.
        convResp.StatusCode.Should().Be(HttpStatusCode.Created,
            "Phase 2 ships no model allowlist at create time; the bogus model tag is accepted here and only fails on the first turn");

        var conv = await convResp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();
        conv!.Model.Should().Be(invalidModel);

        var turnResp = await client.PostAsJsonAsync(
            $"/api/conversations/{conv.Id}/turns",
            new ConversationEndpoints.AppendTurnRequest(Content: "this should fail"));

        ((int)turnResp.StatusCode).Should().Be(502,
            "invalid model tag → Ollama returns 4xx → OllamaClient.EnsureSuccessStatusCode throws HttpRequestException → endpoint catches + maps to 502 Bad Gateway");
    }
}

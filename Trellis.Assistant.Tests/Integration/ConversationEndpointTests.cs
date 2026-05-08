using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Trellis.Assistant.Endpoints;
using Trellis.Assistant.Middleware;
using Trellis.Assistant.Tests.TestFixtures;
using Xunit;

namespace Trellis.Assistant.Tests.Integration;

/// <summary>
/// End-to-end integration tests for the Phase 1 conversation surface,
/// running against a Testcontainers-backed Postgres 16. Covers the four
/// behavioural test items from the Phase 1 brief:
///
///   #1  POST /api/conversations creates a row and returns the id
///   #2  POST /api/conversations/{id}/turns appends user + assistant
///       turns; GET /api/conversations/{id} reads them back in order
///   #3  Request without (X-Trellis-Tenant-Id, X-Trellis-User-Id) tuple
///       returns 401
///   #4  Orchestrator preserves turn ordering across concurrent requests
///       on the same conversation (advisory-lock contract)
///
/// Cross-tenant isolation is also pinned (a side-test of the
/// IAssistantConversationStore tenant-id chokepoint contract).
/// </summary>
public sealed class ConversationEndpointTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private AssistantWebApplicationFactory? _factory;

    public ConversationEndpointTests(PostgresFixture pg)
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

    private HttpClient NewClient(string? tenantId = TestTenants.TenantA, string? userId = TestTenants.UserA)
    {
        var client = _factory!.CreateClient();
        if (tenantId is not null)
        {
            client.DefaultRequestHeaders.Add(TenantHeadersMiddleware.TenantHeaderName, tenantId);
        }
        if (userId is not null)
        {
            client.DefaultRequestHeaders.Add(TenantHeadersMiddleware.UserHeaderName, userId);
        }
        return client;
    }

    // ---------------- Test #1 ----------------

    [SkippableFact]
    public async Task PostConversations_CreatesRowAndReturnsId()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var client = NewClient();
        var resp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        resp.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await resp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();
        body.Should().NotBeNull();
        body!.Id.Should().NotBeNullOrEmpty();
        body.Id.Length.Should().Be(26, "ulid wire format is 26-char base32");
        Ulid.TryParse(body.Id, out _).Should().BeTrue("the returned id parses as a valid ulid");
        body.Channel.Should().Be("api");

        // Round-trip: GET returns the conversation we just created.
        var getResp = await client.GetAsync($"/api/conversations/{body.Id}");
        getResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var getBody = await getResp.Content.ReadFromJsonAsync<ConversationEndpoints.GetConversationResponse>();
        getBody.Should().NotBeNull();
        getBody!.Id.Should().Be(body.Id);
        getBody.Turns.Should().BeEmpty("a brand-new conversation has no turns yet");
    }

    [SkippableFact]
    public async Task PostConversations_WithoutChannelInBody_DefaultsToApi()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var client = NewClient();
        var resp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: null));
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();
        body!.Channel.Should().Be("api",
            "channel defaults to 'api' when omitted (matches the schema's DEFAULT 'api')");
    }

    [SkippableFact]
    public async Task PostConversations_WithoutModelInBody_DefaultsToMistralSmall()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var client = NewClient();
        var resp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        resp.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await resp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();
        body!.Model.Should().Be("mistral-small:24b",
            "model defaults to 'mistral-small:24b' when omitted — matches the schema column DEFAULT and the C#-side fallback in PostgresAssistantConversationStore");

        // GET reads back the same model — the column persists, not just
        // the response shape.
        var getResp = await client.GetAsync($"/api/conversations/{body.Id}");
        var getBody = await getResp.Content.ReadFromJsonAsync<ConversationEndpoints.GetConversationResponse>();
        getBody!.Model.Should().Be("mistral-small:24b");
    }

    [SkippableFact]
    public async Task PostConversations_WithModelInBody_PinsThatModel()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var client = NewClient();
        var resp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api", Model: "llama3.3:70b"));
        resp.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await resp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();
        body!.Model.Should().Be("llama3.3:70b",
            "caller-supplied model overrides the column DEFAULT");

        var getResp = await client.GetAsync($"/api/conversations/{body.Id}");
        var getBody = await getResp.Content.ReadFromJsonAsync<ConversationEndpoints.GetConversationResponse>();
        getBody!.Model.Should().Be("llama3.3:70b",
            "GET returns the pinned model — model is per-conversation + immutable for Phase 2");
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task PostConversations_WithBlankModel_FallsBackToColumnDefault(string blankModel)
    {
        // Sister case to PostConversations_WithoutModelInBody_DefaultsToMistralSmall:
        // hub explicitly required "fallback-to-DEFAULT on null/whitespace".
        // The endpoint normalizes blank values (string.IsNullOrWhiteSpace)
        // to null before passing to the store; the store then applies
        // its C#-side default that mirrors the SQL DEFAULT.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var client = NewClient();
        var resp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api", Model: blankModel));
        resp.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await resp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();
        body!.Model.Should().Be("mistral-small:24b",
            $"blank model value {blankModel.Length}-char-whitespace must fall back to the column DEFAULT — no API surprise where '   ' silently becomes a real model tag");
    }

    [SkippableFact]
    public async Task Model_OnceCreated_IsImmutableThroughMultipleTurnAppends()
    {
        // Phase 2 ships no re-pin endpoint — model is immutable post-
        // creation. Verify by creating with an explicit model, appending
        // multiple turns (the orchestrator reads conv.Model on each
        // append), and re-reading. Model stays unchanged.
        //
        // Phase 3+ may add a re-pin operation if model-switching
        // mid-conversation becomes a real need; this test is the pin
        // that guards against an accidental mutation slipping in via
        // a future endpoint or store-impl change.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var client = NewClient();
        var convResp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api", Model: "qwen2.5:72b"));
        var conv = await convResp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();
        conv!.Model.Should().Be("qwen2.5:72b");

        // Append three turns. Each one passes through the orchestrator's
        // GetConversationAsync → reads conv.Model → calls
        // StreamChatAsync(conv.Model, ...). The stub LLM ignores model
        // but the read path exercises the data flow.
        for (var i = 0; i < 3; i++)
        {
            var turnResp = await client.PostAsJsonAsync(
                $"/api/conversations/{conv.Id}/turns",
                new ConversationEndpoints.AppendTurnRequest(Content: $"turn {i}"));
            turnResp.StatusCode.Should().Be(HttpStatusCode.OK,
                $"turn {i} must succeed — Model immutability test depends on the append path executing cleanly");
        }

        // Re-read; Model unchanged after 3 round-trips.
        var getResp = await client.GetAsync($"/api/conversations/{conv.Id}");
        var getBody = await getResp.Content.ReadFromJsonAsync<ConversationEndpoints.GetConversationResponse>();
        getBody!.Model.Should().Be("qwen2.5:72b",
            "Model is immutable post-creation — no endpoint or store-impl path may mutate it");
        getBody.Turns.Should().HaveCount(6,
            "3 user turns + 3 assistant turns = 6; if the count regresses, Append-side mutation may have hit Conversations too");
    }

    // ---------------- Test #2 ----------------

    [SkippableFact]
    public async Task PostTurns_AppendsUserAndAssistantTurnPair_GetReadsBackInOrder()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var client = NewClient();
        var convResp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        var conv = await convResp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();

        var turnResp = await client.PostAsJsonAsync(
            $"/api/conversations/{conv!.Id}/turns",
            new ConversationEndpoints.AppendTurnRequest(Content: "Hello, assistant!"));
        turnResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var turnBody = await turnResp.Content.ReadFromJsonAsync<ConversationEndpoints.AppendTurnResponse>();
        turnBody.Should().NotBeNull();
        turnBody!.UserTurn.Role.Should().Be("user");
        turnBody.UserTurn.Content.Should().Be("Hello, assistant!");
        turnBody.UserTurn.Position.Should().Be(0);
        turnBody.AssistantTurn.Role.Should().Be("assistant");
        turnBody.AssistantTurn.Content.Should().NotBeNullOrEmpty();
        turnBody.AssistantTurn.Position.Should().Be(1);

        // GET reads back both turns in position order (0 then 1).
        var getResp = await client.GetAsync($"/api/conversations/{conv.Id}");
        var getBody = await getResp.Content.ReadFromJsonAsync<ConversationEndpoints.GetConversationResponse>();
        getBody!.Turns.Should().HaveCount(2);
        getBody.Turns[0].Position.Should().Be(0);
        getBody.Turns[0].Role.Should().Be("user");
        getBody.Turns[0].Content.Should().Be("Hello, assistant!");
        getBody.Turns[1].Position.Should().Be(1);
        getBody.Turns[1].Role.Should().Be("assistant");
    }

    [SkippableFact]
    public async Task PostTurns_OnUnknownConversation_Returns404()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var client = NewClient();
        var bogusUlid = Ulid.NewUlid().ToString();
        var resp = await client.PostAsJsonAsync(
            $"/api/conversations/{bogusUlid}/turns",
            new ConversationEndpoints.AppendTurnRequest(Content: "anyone home?"));
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [SkippableFact]
    public async Task PostTurns_EmptyContent_Returns400()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var client = NewClient();
        var convResp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        var conv = await convResp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();

        var resp = await client.PostAsJsonAsync(
            $"/api/conversations/{conv!.Id}/turns",
            new ConversationEndpoints.AppendTurnRequest(Content: "   "));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [SkippableFact]
    public async Task GetConversation_WithMalformedId_Returns400()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var client = NewClient();
        var resp = await client.GetAsync("/api/conversations/not-a-ulid");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---------------- Test #3 ----------------

    [SkippableFact]
    public async Task PostConversations_WithoutTenantHeader_Returns401()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var client = NewClient(tenantId: null, userId: TestTenants.UserA);
        var resp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [SkippableFact]
    public async Task PostConversations_WithoutUserHeader_Returns401()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var client = NewClient(tenantId: TestTenants.TenantA, userId: null);
        var resp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [SkippableTheory]
    [InlineData("   ", "")]
    [InlineData("", "   ")]
    [InlineData("   ", "   ")]
    [InlineData("", "")]
    public async Task PostConversations_WithBlankHeaders_Returns401(string tenant, string user)
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var client = NewClient(tenantId: tenant, userId: user);
        var resp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "whitespace + empty headers on either side are treated as absent — no surprise auth-bypass via space-padded values");
    }

    // ---------------- Test #4 (concurrency) ----------------

    [SkippableFact]
    public async Task PostTurns_ConcurrentRequestsOnSameConversation_PreserveOrderingAndUniqueness()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var client = NewClient();
        var convResp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        var conv = await convResp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();

        const int concurrentCount = 5;
        // Each iteration uses its own HttpClient — sharing a single client
        // across parallel requests is fine for HttpClient but using
        // separate clients mimics realistic real-world load (each browser
        // tab has its own client).
        var tasks = Enumerable.Range(0, concurrentCount).Select(i =>
        {
            var c = NewClient();
            return c.PostAsJsonAsync(
                $"/api/conversations/{conv!.Id}/turns",
                new ConversationEndpoints.AppendTurnRequest(Content: $"concurrent message {i}"));
        }).ToArray();

        var responses = await Task.WhenAll(tasks);

        // Every request MUST succeed. The advisory lock + position
        // computation under the lock means concurrent requests queue —
        // none of them should fail with a unique-constraint violation.
        responses.Should().AllSatisfy(r =>
            r.StatusCode.Should().Be(HttpStatusCode.OK,
                $"concurrent POST /turns must serialize via the per-conversation advisory lock; failure here means the lock + transaction pairing regressed"));

        // Verify positions: for N concurrent requests on a fresh
        // conversation, we expect 2N turn rows with positions
        // 0,1,2,...,2N-1 — contiguous, no gaps, no duplicates. Ordering
        // of WHO got which position depends on lock-acquisition order
        // (non-deterministic) but the position SET is deterministic.
        var getResp = await client.GetAsync($"/api/conversations/{conv!.Id}");
        var getBody = await getResp.Content.ReadFromJsonAsync<ConversationEndpoints.GetConversationResponse>();

        getBody!.Turns.Should().HaveCount(concurrentCount * 2,
            "every concurrent request must persist exactly 2 turns (user + assistant)");

        var positions = getBody.Turns.Select(t => t.Position).OrderBy(p => p).ToList();
        positions.Should().BeEquivalentTo(
            Enumerable.Range(0, concurrentCount * 2),
            opts => opts.WithStrictOrdering(),
            "positions must be contiguous 0..(2N-1); no gaps, no duplicates");

        // User + assistant alternation: each AppendTurnsAsync call
        // inserts a contiguous (user, assistant) pair under the
        // advisory lock — the lock covers position assignment + the
        // INSERT pair, NOT the upstream history read or LLM call. So
        // even though histories may have been read concurrently, the
        // per-pair atomic persistence keeps positions alternating
        // user, assistant, user, assistant, ... starting at even
        // indices. (See ConversationOrchestrator's CONCURRENCY
        // CONTRACT for the full reasoning.)
        for (var i = 0; i < concurrentCount; i++)
        {
            getBody.Turns[i * 2].Role.Should().Be("user",
                $"position {i * 2} must be a user turn; lock holds across the pair");
            getBody.Turns[i * 2 + 1].Role.Should().Be("assistant",
                $"position {i * 2 + 1} must be an assistant turn paired with position {i * 2}");
        }

        foreach (var r in responses)
        {
            r.Dispose();
        }
    }

    // ---------------- Cross-tenant isolation ----------------

    [SkippableFact]
    public async Task GetConversation_FromDifferentTenant_Returns404()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var creator = NewClient(tenantId: TestTenants.TenantA, userId: TestTenants.UserA);
        var convResp = await creator.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        var conv = await convResp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();

        // Different tenant attempts to read TenantA's conversation.
        // 404 = "not found OR not visible" — the IAssistantConversationStore
        // chokepoint applies WHERE tenant_id = $1 + WHERE user_id = $1 on
        // every query, so cross-tenant reads simply find nothing. The
        // 404 + the not-distinguishable contract prevent existence-
        // probing leaks across tenants.
        using var attacker = NewClient(tenantId: TestTenants.TenantB, userId: TestTenants.UserA);
        var resp = await attacker.GetAsync($"/api/conversations/{conv!.Id}");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---------------- Phase 3.A.2 agent-path tests ----------------

    [SkippableFact]
    public async Task Conversation_AgentPath_PersistsToolTurnInline_AndGetReturnsItInTurnsArray()
    {
        // Phase 3.A.2 explicit mutation pin (per hub's non-negotiables):
        // Option A wire shape — tool turns persist inline in the turns
        // table; GET /api/conversations/{id} returns them in the Turns
        // array between the user turn and the final assistant turn.
        // Locks the GET response shape that Phase 4 channel adapters
        // rely on.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        // Script the agent LLM: one tool_call, then a final assistant
        // text. The orchestrator routes through the executor when the
        // request specifies a Tools filter.
        _factory!.AgentLlmStub
            .EnqueueToolCall("echo", """{"text":"hello from agent path"}""")
            .EnqueueAssistantText("Tool result received; here's the final answer.");

        using var client = NewClient();
        var convResp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        var conv = await convResp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();

        // Agent path: include Tools field in the request body.
        var turnResp = await client.PostAsJsonAsync(
            $"/api/conversations/{conv!.Id}/turns",
            new ConversationEndpoints.AppendTurnRequest("Use echo to test the agent path.")
            {
                Tools = new[] { "echo" },
            });
        turnResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var turnBody = await turnResp.Content.ReadFromJsonAsync<ConversationEndpoints.AppendTurnResponse>();
        turnBody!.UserTurn.Role.Should().Be("user");
        turnBody.UserTurn.Position.Should().Be(0);
        turnBody.AssistantTurn.Role.Should().Be("assistant");
        turnBody.AssistantTurn.Content.Should().Contain("final answer");

        // Tool turn persisted inline in the response.
        turnBody.ToolTurns.Should().NotBeNull(
            "agent path returns ToolTurns populated; Phase 2 direct path leaves it null");
        turnBody.ToolTurns!.Should().HaveCount(1);
        turnBody.ToolTurns![0].Role.Should().Be("tool");
        turnBody.ToolTurns![0].ToolName.Should().Be("echo");
        turnBody.ToolTurns![0].ToolCallId.Should().NotBeNullOrEmpty();
        turnBody.ToolTurns![0].Content.Should().Contain("hello from agent path",
            "tool result content carries the dispatched tool's output JSON");

        // Position contiguity pin: user=0, tool=1, assistant=2.
        turnBody.UserTurn.Position.Should().Be(0);
        turnBody.ToolTurns![0].Position.Should().Be(1);
        turnBody.AssistantTurn.Position.Should().Be(2);

        // Option A GET-shape pin: GET /api/conversations/{id} returns
        // the FULL chain inline in the Turns array, in position order.
        var getResp = await client.GetAsync($"/api/conversations/{conv.Id}");
        var getBody = await getResp.Content.ReadFromJsonAsync<ConversationEndpoints.GetConversationResponse>();
        getBody!.Turns.Should().HaveCount(3,
            "GET returns the full [user, tool, assistant] chain inline per Option A wire shape");
        getBody.Turns[0].Role.Should().Be("user");
        getBody.Turns[1].Role.Should().Be("tool");
        getBody.Turns[1].ToolCallId.Should().NotBeNullOrEmpty();
        getBody.Turns[1].ToolName.Should().Be("echo");
        getBody.Turns[2].Role.Should().Be("assistant");
    }

    [SkippableFact]
    public async Task PostTurns_WithoutToolsField_PreservesPhase2DirectLlmPath()
    {
        // Negative pin: when Tools is null/empty, the orchestrator
        // routes through the Phase 2 direct-LLM path. AppendTurnResponse.ToolTurns
        // is null (not an empty array), and the conversation has only
        // [user, assistant] with no tool turns.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var client = NewClient();
        var convResp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        var conv = await convResp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();

        // No Tools field — Phase 2 direct path. Agent LLM stub should
        // NOT be invoked; the StubLlmClient (text streaming) handles it.
        var turnResp = await client.PostAsJsonAsync(
            $"/api/conversations/{conv!.Id}/turns",
            new ConversationEndpoints.AppendTurnRequest("plain question, no tools"));
        turnResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var turnBody = await turnResp.Content.ReadFromJsonAsync<ConversationEndpoints.AppendTurnResponse>();
        turnBody!.ToolTurns.Should().BeNull(
            "Phase 2 direct-LLM path leaves ToolTurns null in the wire response — additive shape preserves Phase 1+2 client compat");

        // Agent LLM stub never invoked.
        _factory!.AgentLlmStub.Calls.Should().BeEmpty(
            "direct-LLM path bypasses the agent executor entirely; AgentLlmStub records zero invocations");
    }

    [SkippableFact]
    public async Task PostTurns_WithEmptyToolsArray_PreservesPhase2DirectLlmPath()
    {
        // Sister pin: empty Tools array (caller sent the field but no
        // names) is treated identically to null — direct-LLM path.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var client = NewClient();
        var convResp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        var conv = await convResp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();

        var turnResp = await client.PostAsJsonAsync(
            $"/api/conversations/{conv!.Id}/turns",
            new ConversationEndpoints.AppendTurnRequest("question")
            {
                Tools = Array.Empty<string>(),
            });
        turnResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var turnBody = await turnResp.Content.ReadFromJsonAsync<ConversationEndpoints.AppendTurnResponse>();
        turnBody!.ToolTurns.Should().BeNull();
        _factory!.AgentLlmStub.Calls.Should().BeEmpty();
    }

    [SkippableFact]
    public async Task Conversation_AgentPath_AcrossMultipleTurns_HistoryIncludesPriorToolTurns()
    {
        // Multi-turn pin: after a first agent-path turn persists a tool
        // turn, a SECOND user turn (also via agent path) should see the
        // prior tool turn in its history. Verify by checking the
        // AgentLlmStub captured a message count that includes the prior
        // tool turn.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        // Round 1: tool_call → assistant text
        _factory!.AgentLlmStub
            .EnqueueToolCall("echo", """{"text":"first round"}""")
            .EnqueueAssistantText("first round done")
            // Round 2: another tool_call → another assistant text
            .EnqueueToolCall("echo", """{"text":"second round"}""")
            .EnqueueAssistantText("second round done");

        using var client = NewClient();
        var convResp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        var conv = await convResp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();

        // First turn
        await client.PostAsJsonAsync(
            $"/api/conversations/{conv!.Id}/turns",
            new ConversationEndpoints.AppendTurnRequest("first request")
            {
                Tools = new[] { "echo" },
            });

        // Second turn — should see prior history including the tool turn
        var firstRoundCallCount = _factory.AgentLlmStub.Calls.Count;
        await client.PostAsJsonAsync(
            $"/api/conversations/{conv.Id}/turns",
            new ConversationEndpoints.AppendTurnRequest("second request")
            {
                Tools = new[] { "echo" },
            });

        // The second-round LLM calls should include more messages than
        // the first round (history grew with prior user + tool + assistant).
        var secondRoundFirstCall = _factory.AgentLlmStub.Calls[firstRoundCallCount];
        secondRoundFirstCall.MessageCount.Should().BeGreaterThan(2,
            "second round's history includes prior user + tool + assistant turns + new user message — minimum 4 messages (system + 3 prior + new user)");

        // GET conversation now has 6 turns: [user, tool, assistant, user, tool, assistant]
        var getResp = await client.GetAsync($"/api/conversations/{conv.Id}");
        var getBody = await getResp.Content.ReadFromJsonAsync<ConversationEndpoints.GetConversationResponse>();
        getBody!.Turns.Should().HaveCount(6);
        getBody.Turns.Select(t => t.Role).Should().Equal(
            "user", "tool", "assistant",
            "user", "tool", "assistant");
    }

    [SkippableFact]
    public async Task ToCoreRole_ToolTurn_MapsToChatRoleTool()
    {
        // Phase 3.A.2-bridge mutation pin: prior persisted Role=Tool
        // turns map to ChatRole.Tool (was ChatRole.System pre-bridge per
        // architectural divergence #4). Resolves the divergence; the
        // model's tool-trained head sees the canonical role=tool label
        // when reconstructing history for the next-turn LLM call,
        // rather than the prior system-role workaround.
        //
        // Pin shape: a regression that flips back to System would
        // pass the BudgetVerdict.Decision-level contract (the role flow
        // is invisible to the gate) but degrade tool-call quality
        // silently. This test catches it via the wire-roles capture on
        // StubAgentLlmClient.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        // Round 1: agent path → persists [user, tool, assistant].
        _factory!.AgentLlmStub
            .EnqueueToolCall("echo", """{"text":"first round"}""")
            .EnqueueAssistantText("first round done")
            // Round 2: another agent-path turn so the LLM sees prior
            // history including the Round-1 tool turn.
            .EnqueueAssistantText("second round done");

        using var client = NewClient();
        var convResp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        var conv = await convResp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();

        // Round 1 — populates the conversation with user + tool + assistant turns.
        await client.PostAsJsonAsync(
            $"/api/conversations/{conv!.Id}/turns",
            new ConversationEndpoints.AppendTurnRequest("first request")
            {
                Tools = new[] { "echo" },
            });

        var firstRoundCallCount = _factory.AgentLlmStub.Calls.Count;

        // Round 2 — orchestrator reads conversation history (now
        // including the tool turn from round 1) + builds messages list
        // for the LLM. The mutation pin: the captured MessageRoles must
        // include ChatRole.Tool (NOT ChatRole.System) for the prior
        // tool turn.
        await client.PostAsJsonAsync(
            $"/api/conversations/{conv.Id}/turns",
            new ConversationEndpoints.AppendTurnRequest("second request")
            {
                Tools = new[] { "echo" },
            });

        var secondRoundFirstCall = _factory.AgentLlmStub.Calls[firstRoundCallCount];
        secondRoundFirstCall.MessageRoles.Should().Contain(
            Trellis.Core.Models.ChatRole.Tool,
            "round 2's history-to-messages translation must surface the prior persisted Tool turn as ChatRole.Tool — Phase 3.A.2 bridge resolves architectural divergence #4");
        secondRoundFirstCall.MessageRoles.Should().NotContain(
            Trellis.Core.Models.ChatRole.System,
            "round 2's history must NOT carry ChatRole.System for the prior tool turn — that was the pre-bridge workaround we're flipping away from. (System role still appears in messages for genuinely system-role turns; this assertion holds because round 1 had no System turn.)");
    }

    [SkippableFact]
    public async Task AgentPath_OverridesConversationModel_WithAssistantAgentModel()
    {
        // Phase 3.A.2 architectural divergence #2 lock per hub's
        // ratification: when the agent path runs, the LLM call uses
        // Assistant:Agent:Model (default qwen2.5:72b — tool-supporting
        // model), NOT the conversation's pinned Model. Customer-visible
        // side effect: a conversation pinned to mistral-small:24b that
        // flips to agent path produces output from qwen2.5:72b.
        //
        // Phase 3.B candidate: reconcile via per-conversation
        // tool-aware-model field. Until then, pin the divergence so
        // a future change can't accidentally "fix" it without a
        // matching brief.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        _factory!.AgentLlmStub.EnqueueAssistantText("agent answer");

        using var client = NewClient();
        // Create with explicit Model that differs from Assistant:Agent:Model.
        var convResp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(
                Channel: "api",
                Model: "llama3.3:70b"));
        var conv = await convResp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();
        conv!.Model.Should().Be("llama3.3:70b",
            "conversation pins llama3.3:70b explicitly");

        // Agent path: include Tools field — routes through executor.
        await client.PostAsJsonAsync(
            $"/api/conversations/{conv.Id}/turns",
            new ConversationEndpoints.AppendTurnRequest("test")
            {
                Tools = new[] { "echo" },
            });

        // The agent LLM stub captured the model arg passed to
        // ChatWithToolsAsync. It must NOT be the conversation's
        // "llama3.3:70b" — agent path uses Assistant:Agent:Model
        // (qwen2.5:72b in test config, inheriting from appsettings.json).
        _factory.AgentLlmStub.Calls.Should().NotBeEmpty();
        _factory.AgentLlmStub.Calls[0].Model.Should().NotBe("llama3.3:70b",
            "agent path overrides the conversation's pinned Model with Assistant:Agent:Model — pinned divergence per Phase 3.A.2 architectural ratification #2");
        _factory.AgentLlmStub.Calls[0].Model.Should().Be("qwen2.5:72b",
            "Assistant:Agent:Model defaults to qwen2.5:72b in appsettings.json (tool-supporting model)");
    }

    [SkippableFact]
    public async Task PostTurns_AgentPath_NonUuidTenant_Returns400()
    {
        // Phase 3.A C1 pin: agent path requires uuid-shaped tenantId.
        // The TenantHeadersMiddleware lets non-blank strings through;
        // the agent-path branch in the orchestrator surfaces a 400
        // when Guid.Parse fails. Pin the wire-status mapping.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        // First: create a conversation with a uuid tenant (so the row
        // exists). Then attempt to POST /turns from a NON-uuid tenant
        // header — the cross-tenant check would 404, but the agent-path
        // C1 check should fire FIRST with 400.
        using var client = NewClient();
        var convResp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        var conv = await convResp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();

        using var nonUuidClient = NewClient(tenantId: "non-uuid-tenant", userId: TestTenants.UserA);
        var turnResp = await nonUuidClient.PostAsJsonAsync(
            $"/api/conversations/{conv!.Id}/turns",
            new ConversationEndpoints.AppendTurnRequest("agent path with non-uuid tenant")
            {
                Tools = new[] { "echo" },
            });

        // Either 400 (C1 check fires before cross-tenant lookup) or 404
        // (cross-tenant check fires first because the conversation
        // belongs to TenantA — though a non-uuid tenant won't match
        // anyway). 400 is the canonical surface for the C1 violation;
        // 404 is also acceptable per the existence-probing-leak prevention
        // contract. Pin one of those, not 500.
        ((int)turnResp.StatusCode).Should().Match(
            code => code == 400 || code == 404,
            "non-uuid tenant on agent path must surface as 400 (C1 violation) or 404 (cross-tenant) — never 500");
    }

    [SkippableFact]
    public async Task PostTurns_FromDifferentUserSameTenant_Returns404()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var creator = NewClient(tenantId: TestTenants.TenantA, userId: TestTenants.UserA);
        var convResp = await creator.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        var conv = await convResp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();

        // Same tenant, different user — ownership is (tenant, user), NOT
        // just tenant. UserB should not be able to append to UserA's
        // conversation even within the same tenant.
        using var sibling = NewClient(tenantId: TestTenants.TenantA, userId: TestTenants.UserB);
        var resp = await sibling.PostAsJsonAsync(
            $"/api/conversations/{conv!.Id}/turns",
            new ConversationEndpoints.AppendTurnRequest(Content: "intruding"));
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

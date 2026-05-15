using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Trellis.Assistant.Endpoints;
using Trellis.Assistant.Middleware;
using Trellis.Assistant.Services;
using Trellis.Assistant.Tests.TestFixtures;
using Trellis.Core.Services;
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

    private HttpClient NewClient(
        string? tenantId = TestTenants.TenantA,
        string? userId = TestTenants.UserA,
        string? tenantRole = null)
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
        // Phase 3.F: optional X-Trellis-Tenant-Role header for E2E tests
        // that exercise the IToolExposurePolicy RequiredRole gate. Null
        // (default) → no header → tenancy.TenantRole resolves to null
        // at the middleware → fail-closed on any RequiredRole gate.
        if (tenantRole is not null)
        {
            client.DefaultRequestHeaders.Add(TenantClaimsMiddleware.TenantRoleHeaderName, tenantRole);
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
                new ConversationEndpoints.AppendTurnRequest(Content: $"turn {i}")
                {
                    // Phase 3.C: explicit Tools=[] opts into direct-LLM path
                    // (preserves Phase 2 per-conversation-model semantics this
                    // test was written against). Null would route through the
                    // agent path under the new default.
                    Tools = Array.Empty<string>(),
                });
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
            new ConversationEndpoints.AppendTurnRequest(Content: "Hello, assistant!")
            {
                Tools = Array.Empty<string>(),  // Phase 3.C: opt into direct-LLM path
            });
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
            new ConversationEndpoints.AppendTurnRequest(Content: "anyone home?")
            {
                Tools = Array.Empty<string>(),  // Phase 3.C: opt into direct-LLM path
            });
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
            new ConversationEndpoints.AppendTurnRequest(Content: "   ")
            {
                Tools = Array.Empty<string>(),  // Phase 3.C: opt into direct-LLM path
            });
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

    // ---------------- Phase 3.G: middleware-level UUID validation ----------------

    [SkippableTheory]
    [InlineData("not-a-uuid")]
    [InlineData("12345")]
    [InlineData("abcdef")]
    [InlineData("00000000-0000-0000-0000-zzzzzzzzzzzz")]
    public async Task PostConversations_NonUuidTenantId_Returns401_FromMiddleware(string nonUuidTenant)
    {
        // Phase 3.G middleware-level UUID validation: non-UUID
        // tenant_id is rejected at the request boundary with 401
        // (auth-failure shape — invalid claim per Phase 3.A C1).
        // Pre-3.G this slipped past the middleware (which only checked
        // non-blank) and surfaced as 400/404 at downstream layers; the
        // brief window of "non-UUID tenant accepted by middleware"
        // was the security gap Phase 3.F's design doc forward-flagged.
        // Pin all four representative non-UUID forms.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var client = NewClient(tenantId: nonUuidTenant, userId: TestTenants.UserA);
        var resp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            $"non-UUID tenant_id '{nonUuidTenant}' must 401 at middleware before reaching the endpoint");
    }

    [SkippableFact]
    public async Task PostConversations_NonUuidTenantId_ResponseShape_IsProblemDetails()
    {
        // Phase 3.G pin #2: 401 body is RFC 7807 ProblemDetails (not
        // bespoke {"error": "..."}). Operators integrating with
        // Trellis services parse type/title/status/detail from the
        // problem+json content type; this pin protects the wire
        // contract.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var client = NewClient(tenantId: "not-a-uuid", userId: TestTenants.UserA);
        var resp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        resp.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json",
            "Phase 3.G upgraded Write401Async to ProblemDetails — content-type is application/problem+json per RFC 7807");

        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("\"status\":401");
        body.Should().Contain("\"title\":\"Unauthorized\"");
        body.Should().Contain("tenant_id",
            "Detail string identifies the rejected claim — operators can diagnose without strace");
    }

    [SkippableFact]
    public async Task PostConversations_NonUuidTenantId_DeprecatedHeaderPath_AlsoReturns401()
    {
        // Phase 3.G pin #5: deprecated-header-path callers get the
        // same UUID validation gate as JWT callers. In PRODUCTION, an
        // unauthenticated request with X-Trellis-Tenant-Id headers
        // takes the middleware's Path 2 (header-fallback) block —
        // that path also Guid.TryParse's the tenant value and 401s on
        // non-UUID. In the TEST ENVIRONMENT,
        // TestAuthenticationHandler synthesizes a ClaimsPrincipal
        // from the X-Trellis-* headers BEFORE TenantClaimsMiddleware
        // runs, so middleware actually exercises Path 1's JWT gate.
        // Either way the 401 fires for non-UUID; the assertion holds
        // for both paths. Operators reading this test should know it
        // pins the contract end-to-end without isolating which
        // middleware branch ran.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var client = NewClient(tenantId: "deprecated-non-uuid", userId: TestTenants.UserA);
        var resp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "deprecated-header callers get the same UUID validation gate as JWT callers");
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
                new ConversationEndpoints.AppendTurnRequest(Content: $"concurrent message {i}")
                {
                    Tools = Array.Empty<string>(),  // Phase 3.C: opt into direct-LLM path
                });
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

    // ---------------- Phase 3.C: routing defaults + live agent loop ----------------

    [SkippableFact]
    public async Task PostTurns_NullToolsFilter_RoutesThroughAgentPath_WithFullExposedCatalogue()
    {
        // Phase 3.C Q1 Option A: null Tools (omitted from request body)
        // routes to the agent path with the registry's full exposed
        // catalogue. Test env has ExposeEcho=true so both EchoTool +
        // SearchDocumentsTool are exposed. The agent LLM stub receives
        // tools.Count == 2; the conversation orchestrator did NOT
        // require the caller to pass Tools.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        _factory!.AgentLlmStub
            .EnqueueAssistantText("Default-route answer.");

        using var client = NewClient();
        var convResp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        var conv = await convResp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();

        // Note: Tools field deliberately OMITTED — null route.
        var turnResp = await client.PostAsJsonAsync(
            $"/api/conversations/{conv!.Id}/turns",
            new ConversationEndpoints.AppendTurnRequest("Question without explicit tools filter."));
        turnResp.StatusCode.Should().Be(HttpStatusCode.OK);

        _factory.AgentLlmStub.Calls.Should().HaveCount(1,
            "null Tools routes through the agent path → IAgentLlmClient.ChatWithToolsAsync invoked");
        _factory.AgentLlmStub.Calls[0].ToolCount.Should().BeGreaterThan(0,
            "exposed catalogue (EchoTool + SearchDocumentsTool in test env) flows to the LLM");
    }

    [SkippableFact]
    public async Task PostTurns_NullToolsFilter_DefaultRoute_LLMSeesSearchDocumentsInCatalogue()
    {
        // Phase 3.C hub example: "a conversation turn asking 'what's our
        // refund policy?' should know search_documents is available."
        // Pin: default-route system prompt enumerates search_documents
        // + the function-calling tools array carries it. The model
        // could then emit a tool_call (in a full integration with a
        // real tool-aware LLM); the stub returns text directly, but
        // we pin that the catalogue WAS surfaced.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        _factory!.AgentLlmStub
            .EnqueueAssistantText("(answer)");

        using var client = NewClient();
        var convResp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        var conv = await convResp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();

        await client.PostAsJsonAsync(
            $"/api/conversations/{conv!.Id}/turns",
            new ConversationEndpoints.AppendTurnRequest("What's our refund policy?"));

        var call = _factory.AgentLlmStub.Calls[0];
        call.FirstMessageContent.Should().Contain("search_documents",
            "system prompt enumerates the registered search_documents tool — LLM knows it's available without caller telling it");
        call.ToolDescriptions.Should().Contain(d => d.Contains("indexed document corpus"),
            "function-calling tools array carries search_documents's actual description for the LLM to reason about");
    }

    [SkippableFact]
    public async Task ConversationTurn_AskingForDocumentLookup_InvokesSearchDocumentsAndReturnsAugmentedReply()
    {
        // PR #10 review Blocker 3: the real Phase 3.C E2E. Hub's brief
        // explicitly required exercising the full dispatch chain:
        //   stub LLM emits tool_call("search_documents", {...}) →
        //   executor dispatches SearchDocumentsTool →
        //   ISearchClient (stubbed) returns canned chunks →
        //   second LLM turn returns augmented text →
        //   AgentRun persisted with 2 LLM calls + 1 step + 3 turns.
        // The original PR landed without the ISearchClient stub wired in
        // (a live dispatch would have tried HTTP to a dummy host) so this
        // chain was never exercised under test. This is the pin that
        // closes that gap.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        _factory!.SearchClientStub.NextResult = new SearchClientResult
        {
            Success = true,
            ResponseBodyJson = """[{"DocumentId":"doc1","ChunkContent":"Refunds accepted within 30 days of purchase.","Score":0.97}]""",
        };
        _factory.AgentLlmStub
            .EnqueueToolCall("search_documents", """{"query":"refund policy"}""")
            .EnqueueAssistantText("Refunds within 30 days.");

        using var client = NewClient();
        var convResp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        var conv = await convResp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();

        // Tools deliberately OMITTED — default route. The LLM stub emits
        // search_documents on the first call because we scripted it; in a
        // real LLM the system prompt's tool catalogue is what guides the
        // model to pick search_documents (see
        // PostTurns_NullToolsFilter_DefaultRoute_LLMSeesSearchDocumentsInCatalogue).
        var turnResp = await client.PostAsJsonAsync(
            $"/api/conversations/{conv!.Id}/turns",
            new ConversationEndpoints.AppendTurnRequest("What is the refund policy?"));
        turnResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var turnBody = await turnResp.Content.ReadFromJsonAsync<ConversationEndpoints.AppendTurnResponse>();
        turnBody!.AssistantTurn.Content.Should().Contain("Refunds within 30 days",
            "the second LLM call's assistant text — produced AFTER the tool result was inserted into the loop's message history — surfaces as the conversation's assistant turn");
        turnBody.ToolTurns.Should().NotBeNull("agent path populated ToolTurns");
        turnBody.ToolTurns!.Should().HaveCount(1, "exactly one tool dispatch was scripted");
        turnBody.ToolTurns![0].ToolName.Should().Be("search_documents",
            "the LLM's emitted tool_call name flowed through to the persisted Tool turn");
        turnBody.ToolTurns![0].Content.Should().Contain("Refunds accepted within 30 days",
            "the ISearchClient stub's canned response flowed through SearchDocumentsTool → AgentToolOutput → ToolTurn.Content");

        // Position contiguity pin: user=0, tool=1, assistant=2.
        turnBody.UserTurn.Position.Should().Be(0);
        turnBody.ToolTurns![0].Position.Should().Be(1);
        turnBody.AssistantTurn.Position.Should().Be(2);

        // GET reads the full chain inline.
        var getBody = (await (await client.GetAsync($"/api/conversations/{conv.Id}"))
            .Content.ReadFromJsonAsync<ConversationEndpoints.GetConversationResponse>())!;
        getBody.Turns.Should().HaveCount(3,
            "GET returns the full [user, tool, assistant] chain in position order");

        // 2 LLM calls: first emitted tool_call, second consumed the
        // tool result + emitted final assistant text.
        _factory.AgentLlmStub.Calls.Should().HaveCount(2,
            "agent loop iterated twice: tool_call dispatch → result back into history → final assistant text");

        // The stub ISearchClient captured the LLM-emitted query verbatim.
        _factory.SearchClientStub.LastQuery.Should().NotBeNull();
        _factory.SearchClientStub.LastQuery!.Q.Should().Be("refund policy",
            "the LLM's tool_call arguments flowed through MapToSearchQuery → ISearchClient.SearchAsync with the query preserved");
    }

    // ---------------- Phase 3.F: per-tenant tool exposure gating ----------------

    [SkippableFact]
    public async Task PostTurns_RoleGatedTool_HiddenWhenRoleClaimAbsent()
    {
        // Phase 3.F E2E pin: when PerTool config gates a tool by
        // RequiredRole, callers without the role claim don't see it
        // in the LLM-visible catalogue. Middleware extracts null (no
        // X-Trellis-Tenant-Role header) → policy fail-closes →
        // registry omits the tool from GetExposedDescriptorsFor.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        // Override the test factory's options to gate chat_recent
        // behind RequiredRole=admin. Apply per-test via WithWebHostBuilder
        // so the global factory's behavior is preserved for other tests.
        await using var customFactory = _factory!.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Assistant:Tools:PerTool:chat_recent:RequiredRole"] = "admin",
                });
            });
        });

        // Default user — no role header → tenancy.TenantRole = null.
        using var client = customFactory.CreateClient();
        client.DefaultRequestHeaders.Add(TenantClaimsMiddleware.TenantHeaderName, TestTenants.TenantA);
        client.DefaultRequestHeaders.Add(TenantClaimsMiddleware.UserHeaderName, TestTenants.UserA);

        _factory.AgentLlmStub.EnqueueAssistantText("ok");

        var convResp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        var conv = await convResp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();

        await client.PostAsJsonAsync(
            $"/api/conversations/{conv!.Id}/turns",
            new ConversationEndpoints.AppendTurnRequest("default route — catalogue should omit chat_recent"));

        var call = _factory!.AgentLlmStub.Calls[^1];
        call.ToolDescriptions.Should().NotContain(d => d.Contains("recent conversation history"),
            "chat_recent gated to role=admin; null-role caller does NOT see it in the LLM catalogue");
        call.ToolDescriptions.Should().Contain(d => d.Contains("indexed document corpus"),
            "search_documents is not role-gated; still exposed");
    }

    [SkippableFact]
    public async Task PostTurns_RoleGatedTool_VisibleWhenRoleMatches()
    {
        // Companion pin: same RequiredRole=admin gate, but caller now
        // carries X-Trellis-Tenant-Role=admin → tenancy.TenantRole=admin
        // → policy passes → tool visible.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        await using var customFactory = _factory!.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Assistant:Tools:PerTool:chat_recent:RequiredRole"] = "admin",
                });
            });
        });

        using var client = customFactory.CreateClient();
        client.DefaultRequestHeaders.Add(TenantClaimsMiddleware.TenantHeaderName, TestTenants.TenantA);
        client.DefaultRequestHeaders.Add(TenantClaimsMiddleware.UserHeaderName, TestTenants.UserA);
        // The role header is the load-bearing addition here.
        client.DefaultRequestHeaders.Add(TenantClaimsMiddleware.TenantRoleHeaderName, "admin");

        _factory.AgentLlmStub.EnqueueAssistantText("ok");

        var convResp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        var conv = await convResp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();

        await client.PostAsJsonAsync(
            $"/api/conversations/{conv!.Id}/turns",
            new ConversationEndpoints.AppendTurnRequest("admin caller — should see chat_recent"));

        var call = _factory!.AgentLlmStub.Calls[^1];
        call.ToolDescriptions.Should().Contain(d => d.Contains("recent conversation history"),
            "chat_recent gated to role=admin; admin caller DOES see it (policy passes)");
    }

    [SkippableFact]
    public async Task PostTurns_AllowedTenantsGate_HidesToolFromNonAllowedTenant()
    {
        // Phase 3.F E2E pin: AllowedTenants UUID gate. Configure
        // chat_recent to only allow TenantA. TenantB caller hits the
        // endpoint with otherwise-valid headers → policy hides
        // chat_recent → catalogue omits it.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        await using var customFactory = _factory!.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Assistant:Tools:PerTool:chat_recent:AllowedTenants:0"] = TestTenants.TenantA,
                });
            });
        });

        // TenantB caller — not in AllowedTenants → chat_recent hidden.
        using var clientB = customFactory.CreateClient();
        clientB.DefaultRequestHeaders.Add(TenantClaimsMiddleware.TenantHeaderName, TestTenants.TenantB);
        clientB.DefaultRequestHeaders.Add(TenantClaimsMiddleware.UserHeaderName, TestTenants.UserA);

        _factory.AgentLlmStub.EnqueueAssistantText("ok");

        var convResp = await clientB.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        var conv = await convResp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();

        await clientB.PostAsJsonAsync(
            $"/api/conversations/{conv!.Id}/turns",
            new ConversationEndpoints.AppendTurnRequest("TenantB request"));

        var denyCall = _factory!.AgentLlmStub.Calls[^1];
        denyCall.ToolDescriptions.Should().NotContain(d => d.Contains("recent conversation history"),
            "TenantB not in AllowedTenants → chat_recent hidden");

        // PR #13 review polish: positive-side assertion. Without this,
        // the test would pass even if the policy hid chat_recent from
        // ALL tenants (false-green on the mutual-exclusion property).
        // TenantA caller with otherwise-identical setup should see the
        // tool — falsifies the "policy too restrictive" failure mode.
        using var clientA = customFactory.CreateClient();
        clientA.DefaultRequestHeaders.Add(TenantClaimsMiddleware.TenantHeaderName, TestTenants.TenantA);
        clientA.DefaultRequestHeaders.Add(TenantClaimsMiddleware.UserHeaderName, TestTenants.UserA);

        _factory.AgentLlmStub.EnqueueAssistantText("ok");

        var convRespA = await clientA.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        var convA = await convRespA.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();
        await clientA.PostAsJsonAsync(
            $"/api/conversations/{convA!.Id}/turns",
            new ConversationEndpoints.AppendTurnRequest("TenantA request — should see chat_recent"));

        var allowCall = _factory.AgentLlmStub.Calls[^1];
        allowCall.ToolDescriptions.Should().Contain(d => d.Contains("recent conversation history"),
            "TenantA IS on the AllowedTenants list — must still see chat_recent");
    }

    // ---------------- Phase 3.E: multi-tool dispatch per LLM turn ----------------

    [SkippableFact]
    public async Task PostTurns_MultiToolCall_BothDispatchInOneTurn_FourTurnPersistence()
    {
        // Phase 3.E E2E pin: stub LLM emits TWO tool_calls in a single
        // assistant message → executor dispatches both serially →
        // orchestrator persists [user, tool A, tool B, assistant] as
        // a 4-turn batch. The full chain is reachable via GET in
        // position order.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        // Search stub returns canned chunks; chat_recent stub doesn't
        // need a seed (operates against the live store, which is empty
        // for this fresh tenant).
        _factory!.SearchClientStub.NextResult = new SearchClientResult
        {
            Success = true,
            ResponseBodyJson = """[{"DocumentId":"doc1","ChunkContent":"Refunds within 30 days.","Score":0.95}]""",
        };
        _factory.AgentLlmStub
            .EnqueueMultipleToolCalls(
                ("search_documents", """{"query":"refund policy"}"""),
                ("chat_recent", """{"query":"refund"}"""))
            .EnqueueAssistantText("Combining both sources: refunds within 30 days; no prior discussion found.");

        using var client = NewClient();
        var convResp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        var conv = await convResp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();

        // Tools = null → default agent path with full exposed catalogue.
        var turnResp = await client.PostAsJsonAsync(
            $"/api/conversations/{conv!.Id}/turns",
            new ConversationEndpoints.AppendTurnRequest(
                "What's the refund policy? Did we discuss it before?"));
        turnResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var turnBody = await turnResp.Content.ReadFromJsonAsync<ConversationEndpoints.AppendTurnResponse>();
        turnBody!.AssistantTurn.Content.Should().Contain("refunds within 30 days",
            "final assistant text emitted after both tool results landed in context");

        turnBody.ToolTurns.Should().NotBeNull();
        turnBody.ToolTurns!.Should().HaveCount(2,
            "two tool_calls in one LLM response → two persisted tool turns");
        turnBody.ToolTurns![0].ToolName.Should().Be("search_documents",
            "tool dispatches preserved in tool_calls[] order (Phase 3.E pin #4)");
        turnBody.ToolTurns![1].ToolName.Should().Be("chat_recent");

        // Position contiguity: user=0, tool A=1, tool B=2, assistant=3.
        turnBody.UserTurn.Position.Should().Be(0);
        turnBody.ToolTurns![0].Position.Should().Be(1);
        turnBody.ToolTurns![1].Position.Should().Be(2);
        turnBody.AssistantTurn.Position.Should().Be(3);

        // Search tool actually invoked with the LLM's args.
        _factory.SearchClientStub.LastQuery.Should().NotBeNull();
        _factory.SearchClientStub.LastQuery!.Q.Should().Be("refund policy");

        // 2 LLM calls: multi-tool dispatch turn + final synth.
        _factory.AgentLlmStub.Calls.Should().HaveCount(2,
            "agent loop iterated twice: 1 multi-tool turn + 1 final synth");

        // GET returns the full 4-turn chain.
        var getBody = (await (await client.GetAsync($"/api/conversations/{conv.Id}"))
            .Content.ReadFromJsonAsync<ConversationEndpoints.GetConversationResponse>())!;
        getBody.Turns.Should().HaveCount(4,
            "[user, tool search_documents, tool chat_recent, assistant] — contiguous position order");
        getBody.Turns[0].Role.Should().Be("user");
        getBody.Turns[1].Role.Should().Be("tool");
        getBody.Turns[1].ToolName.Should().Be("search_documents");
        getBody.Turns[2].Role.Should().Be("tool");
        getBody.Turns[2].ToolName.Should().Be("chat_recent");
        getBody.Turns[3].Role.Should().Be("assistant");
    }

    // ---------------- Phase 3.D: chat_recent live agent loop ----------------

    [SkippableFact]
    public async Task ConversationTurn_AskingAboutPriorDiscussion_InvokesChatRecentAndReturnsAugmentedReply()
    {
        // Phase 3.D primary E2E. Mirrors the Phase 3.C
        // search_documents E2E shape:
        //   1. Seed conversations + turns matching a query
        //   2. Stub LLM emits tool_call("chat_recent", {query: "..."}) on first call
        //   3. Executor dispatches → PostgresAssistantConversationStore
        //      finds the seeded turns + returns them as RecentTurnSummary
        //   4. Tool result lands in agent loop's message history
        //   5. Stub LLM emits final assistant text on second call
        //   6. Persistence: user turn + tool turn + assistant turn, 3 total
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var client = NewClient();

        // Seed a prior conversation with refund-related turns for this user.
        var seedConvResp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api")
            {
                Model = "mistral-small:24b",
            });
        var seedConv = await seedConvResp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();

        await client.PostAsJsonAsync(
            $"/api/conversations/{seedConv!.Id}/turns",
            new ConversationEndpoints.AppendTurnRequest("Tell me about the refund policy.")
            {
                Tools = Array.Empty<string>(),  // direct-LLM seed; we don't want the agent loop to fire on seeding
            });

        // Now: a fresh conversation. The user references the prior
        // discussion. The LLM is scripted to emit chat_recent on the
        // first turn + a final answer on the second.
        _factory!.AgentLlmStub
            .EnqueueToolCall("chat_recent", """{"query":"refund","limit":5}""")
            .EnqueueAssistantText("Earlier you asked about the refund policy. The answer: 30 days.");

        var freshConvResp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        var freshConv = await freshConvResp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();

        var turnResp = await client.PostAsJsonAsync(
            $"/api/conversations/{freshConv!.Id}/turns",
            new ConversationEndpoints.AppendTurnRequest("What did we discuss about refunds earlier?"));
        turnResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var turnBody = await turnResp.Content.ReadFromJsonAsync<ConversationEndpoints.AppendTurnResponse>();
        turnBody!.AssistantTurn.Content.Should().Contain("30 days",
            "the second LLM call's assistant text — produced AFTER chat_recent injected the seeded turn into history — surfaces as the conversation's assistant turn");
        turnBody.ToolTurns.Should().NotBeNull("agent path populated ToolTurns");
        turnBody.ToolTurns!.Should().HaveCount(1, "exactly one chat_recent dispatch was scripted");
        turnBody.ToolTurns![0].ToolName.Should().Be("chat_recent");
        turnBody.ToolTurns![0].Content.Should().Contain("refund policy",
            "the chat_recent result content (seeded turn's content) flows through SearchRecentTurnsAsync → AgentToolOutput → ToolTurn.Content");

        // Position contiguity pin: user=0, tool=1, assistant=2.
        turnBody.UserTurn.Position.Should().Be(0);
        turnBody.ToolTurns![0].Position.Should().Be(1);
        turnBody.AssistantTurn.Position.Should().Be(2);

        _factory.AgentLlmStub.Calls.Should().HaveCount(2,
            "agent loop iterated twice: tool_call dispatch → result back into history → final assistant text");
    }

    [SkippableFact]
    public async Task ChatRecent_TenantScoping_TenantBCannotSeeTenantATurns()
    {
        // Phase 3.D tenant-scoping E2E pin. Seed a refund turn in
        // TenantA's history; switch the X-Trellis-Tenant-Id header to
        // TenantB; chat_recent from TenantB must NOT see TenantA's
        // turn. Pins the chokepoint behavior end-to-end.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        // Seed TenantA's history.
        using (var clientA = NewClient())
        {
            var convResp = await clientA.PostAsJsonAsync("/api/conversations",
                new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
            var conv = await convResp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();
            await clientA.PostAsJsonAsync(
                $"/api/conversations/{conv!.Id}/turns",
                new ConversationEndpoints.AppendTurnRequest("TenantA secret: refund policy is 30 days")
                {
                    Tools = Array.Empty<string>(),
                });
        }

        // TenantB queries chat_recent for "refund" — should see nothing.
        _factory!.AgentLlmStub
            .EnqueueToolCall("chat_recent", """{"query":"refund"}""")
            .EnqueueAssistantText("I don't have any record of that — apologies.");

        using var clientB = new HttpClient(_factory.Server.CreateHandler())
        {
            BaseAddress = _factory.Server.BaseAddress,
        };
        clientB.DefaultRequestHeaders.Add("X-Trellis-Tenant-Id", TestTenants.TenantB);
        clientB.DefaultRequestHeaders.Add("X-Trellis-User-Id", TestTenants.UserA);

        var convResp2 = await clientB.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        var conv2 = await convResp2.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();

        var turnResp = await clientB.PostAsJsonAsync(
            $"/api/conversations/{conv2!.Id}/turns",
            new ConversationEndpoints.AppendTurnRequest("What do you know about refunds?"));
        turnResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var turnBody = await turnResp.Content.ReadFromJsonAsync<ConversationEndpoints.AppendTurnResponse>();
        turnBody!.ToolTurns.Should().NotBeNull();
        turnBody.ToolTurns!.Should().HaveCount(1);
        var toolContent = turnBody.ToolTurns![0].Content;
        toolContent.Should().NotContain("TenantA secret",
            "tenant scoping is hard-required — TenantA's turn is invisible to TenantB even when the LLM emits chat_recent");
        toolContent.Should().Be("[]",
            "empty result for TenantB's chat_recent — no rows under TenantB's scope match 'refund'");
    }

    [SkippableFact]
    public async Task PostTurns_EmptyToolsArray_PreservesDirectLlmPath_PhaseThreeC()
    {
        // Phase 3.C Q1 Option A: Tools = [] (explicit empty list) is the
        // direct-LLM opt-out. Caller doesn't want tool overhead → use
        // the conversation's pinned Model directly. IOllamaClient (the
        // direct-LLM stub) is called; IAgentLlmClient is NOT.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var agentCallsBefore = _factory!.AgentLlmStub.Calls.Count;

        using var client = NewClient();
        var convResp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        var conv = await convResp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();

        var turnResp = await client.PostAsJsonAsync(
            $"/api/conversations/{conv!.Id}/turns",
            new ConversationEndpoints.AppendTurnRequest("just chat, no tools")
            {
                Tools = Array.Empty<string>(),
            });
        turnResp.StatusCode.Should().Be(HttpStatusCode.OK);

        _factory.AgentLlmStub.Calls.Count.Should().Be(agentCallsBefore,
            "Tools = [] opts out of the agent path; IAgentLlmClient is NOT invoked");

        var turnBody = await turnResp.Content.ReadFromJsonAsync<ConversationEndpoints.AppendTurnResponse>();
        turnBody!.ToolTurns.Should().BeNull("direct-LLM path leaves ToolTurns null");
        turnBody.AssistantTurn.Content.Should().Contain("Stub assistant reply",
            "direct-LLM path returned the IOllamaClient stub's canned text");
    }

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
    public async Task PostTurns_EmptyToolsArray_OptsOutOfAgentPath_DirectLlmPath()
    {
        // Phase 3.C Q1 Option A: Tools = [] is the direct-LLM opt-out
        // ("just chat"). null routes the OTHER way — to the agent path
        // with the full exposed catalogue. This pin locks the [] half
        // of the contract. Renamed + comment-fixed in Phase 3.D Part E
        // per hub's PR #10 follow-up; pre-rename was
        // PostTurns_WithoutToolsField_PreservesPhase2DirectLlmPath
        // (stale Phase 3.A.2 naming).
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        // Snapshot the agent stub's call count BEFORE this test's
        // request. Other tests in this class can populate the stub
        // (it's a per-factory singleton); the brittle "should be empty"
        // anti-pattern fails non-deterministically as the test order
        // shifts. Snapshot-and-compare is the canonical fix —
        // PR #10 review surfaced this in Part E.
        var agentCallsBefore = _factory!.AgentLlmStub.Calls.Count;

        using var client = NewClient();
        var convResp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        var conv = await convResp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();

        var turnResp = await client.PostAsJsonAsync(
            $"/api/conversations/{conv!.Id}/turns",
            new ConversationEndpoints.AppendTurnRequest("plain question, no tools")
            {
                Tools = Array.Empty<string>(),  // direct-LLM opt-out
            });
        turnResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var turnBody = await turnResp.Content.ReadFromJsonAsync<ConversationEndpoints.AppendTurnResponse>();
        turnBody!.ToolTurns.Should().BeNull(
            "direct-LLM path leaves ToolTurns null in the wire response — additive shape preserves Phase 1+2 client compat");

        _factory.AgentLlmStub.Calls.Count.Should().Be(agentCallsBefore,
            "Tools = [] opts out of the agent path; IAgentLlmClient is NOT invoked");
    }

    [SkippableFact]
    public async Task PostTurns_FilterRouteWithAllUnknownNames_DegradesToDirectLlmPath()
    {
        // PR #10 review polish: pins the Blocker 1 fix. A filter of
        // all-unknown names pre-resolves to empty (the registry doesn't
        // know any of them), so the orchestrator degrades to the
        // direct-LLM path instead of running the executor with zero
        // tools.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var agentCallsBefore = _factory!.AgentLlmStub.Calls.Count;

        using var client = NewClient();
        var convResp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        var conv = await convResp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();

        var turnResp = await client.PostAsJsonAsync(
            $"/api/conversations/{conv!.Id}/turns",
            new ConversationEndpoints.AppendTurnRequest("question with ghost tools")
            {
                Tools = new[] { "ghost_tool", "phantom_tool" },
            });
        turnResp.StatusCode.Should().Be(HttpStatusCode.OK);

        _factory.AgentLlmStub.Calls.Count.Should().Be(agentCallsBefore,
            "all-unknown filter resolves to empty catalogue; orchestrator degrades to direct-LLM and never invokes IAgentLlmClient");

        var turnBody = await turnResp.Content.ReadFromJsonAsync<ConversationEndpoints.AppendTurnResponse>();
        turnBody!.ToolTurns.Should().BeNull("direct-LLM path leaves ToolTurns null");
        turnBody.AssistantTurn.Content.Should().Contain("Stub assistant reply",
            "direct-LLM path returned the IOllamaClient stub's canned text");
    }

    [SkippableFact]
    public async Task PostTurns_FilterWithSearchDocuments_RoutesAgentPathWithFilter()
    {
        // PR #10 review polish: filter route was only tested with
        // EchoTool. This pins SearchDocumentsTool through the same
        // filter route — the registry resolves it + the executor flows
        // it to the LLM. Stub LLM returns final text (no tool_call
        // emitted) so we don't need to script the full dispatch chain
        // here — the ConversationTurn_AskingForDocumentLookup_... test
        // above covers full dispatch.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        _factory!.AgentLlmStub
            .EnqueueAssistantText("(no tool needed for this one).");

        using var client = NewClient();
        var convResp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        var conv = await convResp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();

        var turnResp = await client.PostAsJsonAsync(
            $"/api/conversations/{conv!.Id}/turns",
            new ConversationEndpoints.AppendTurnRequest("filter route with search_documents")
            {
                Tools = new[] { "search_documents" },
            });
        turnResp.StatusCode.Should().Be(HttpStatusCode.OK);

        _factory.AgentLlmStub.Calls.Should().HaveCount(1);
        var call = _factory.AgentLlmStub.Calls[0];
        call.ToolCount.Should().Be(1, "filter resolved to exactly search_documents");
        call.ToolDescriptions[0].Should().Contain("indexed document corpus",
            "filter-route descriptor resolved through registry — LLM sees the real description");
    }

    [SkippableFact]
    public async Task PostTurns_NullTools_EmptyExposedCatalogue_DegradesToDirectLlmPath()
    {
        // PR #10 review polish: pins the null-route + empty-catalogue
        // degradation contract. Uses WithWebHostBuilder to override DI
        // for this test only — removes all IAgentTool registrations so
        // the registry's exposed Descriptors list is empty. The
        // orchestrator's null route then sees Descriptors.Count == 0
        // and falls back to the direct-LLM path.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        await using var customFactory = _factory!.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAgentTool>();
            });
        });

        using var client = customFactory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Trellis-Tenant-Id", TestTenants.TenantA);
        client.DefaultRequestHeaders.Add("X-Trellis-User-Id", TestTenants.UserA);

        var convResp = await client.PostAsJsonAsync("/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        var conv = await convResp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();

        var agentCallsBefore = _factory.AgentLlmStub.Calls.Count;

        var turnResp = await client.PostAsJsonAsync(
            $"/api/conversations/{conv!.Id}/turns",
            new ConversationEndpoints.AppendTurnRequest("question with no exposed tools available"));
        turnResp.StatusCode.Should().Be(HttpStatusCode.OK);

        _factory.AgentLlmStub.Calls.Count.Should().Be(agentCallsBefore,
            "null Tools + empty exposed catalogue degrades to direct-LLM; IAgentLlmClient is NOT invoked");

        var turnBody = await turnResp.Content.ReadFromJsonAsync<ConversationEndpoints.AppendTurnResponse>();
        turnBody!.ToolTurns.Should().BeNull("direct-LLM path leaves ToolTurns null");
        turnBody.AssistantTurn.Content.Should().Contain("Stub assistant reply");
    }

    // NOTE: The pre-Phase-3.C Phase 3.A.2 sister pin
    // `PostTurns_WithEmptyToolsArray_PreservesPhase2DirectLlmPath` was
    // consolidated into the Phase 3.C-named test
    // `PostTurns_EmptyToolsArray_PreservesDirectLlmPath_PhaseThreeC`
    // above (same shape, stronger assertions: explicit "no agent stub
    // calls" + canonical-text content check). Hub's PR #10 review
    // Blocker 4 #3 asked to rename + update the stale comment; rather
    // than carry two redundant pins, the cleaner consolidation is one
    // canonical Phase 3.C pin.

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

        // Phase 3.C: the executor now injects a tool-aware system prompt
        // at messages[0]. The prior tool turn must NOT be re-rendered as
        // ChatRole.System — that was the pre-bridge workaround. Pin the
        // negative assertion at the prior-turn slice (skip messages[0]
        // which is the legitimate Phase 3.C system-prompt injection).
        secondRoundFirstCall.MessageRoles.Skip(1).Should().NotContain(
            Trellis.Core.Models.ChatRole.System,
            "round 2's history must NOT carry ChatRole.System for the prior tool turn — that was the pre-bridge workaround we're flipping away from. messages[0] is the Phase 3.C executor-injected system prompt + is excluded from this check.");
        secondRoundFirstCall.MessageRoles[0].Should().Be(
            Trellis.Core.Models.ChatRole.System,
            "Phase 3.C: the executor injects a tool-aware system prompt at messages[0] for both the standalone + conversation-integrated agent paths");
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
    public async Task PostTurns_AgentPath_NonUuidTenant_Returns401_FromMiddleware()
    {
        // Phase 3.G: TenantClaimsMiddleware validates UUID-shape at the
        // request boundary + rejects 401 BEFORE the endpoint handler
        // OR the orchestrator sees the request. Pre-3.G this test
        // accepted 400 (orchestrator C1 throw) or 404 (cross-tenant
        // lookup); 3.G moves the gate up so non-UUID is now 401
        // (auth-failure shape — invalid claim). The orchestrator C1
        // throw becomes unreachable for the UUID-malformed case (the
        // Guid.Empty defense stays as a separate concern).
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

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

        turnResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "Phase 3.G middleware rejects non-UUID tenant_id at 401 before the orchestrator runs");
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
            new ConversationEndpoints.AppendTurnRequest(Content: "intruding")
            {
                Tools = Array.Empty<string>(),  // Phase 3.C: opt into direct-LLM path
            });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

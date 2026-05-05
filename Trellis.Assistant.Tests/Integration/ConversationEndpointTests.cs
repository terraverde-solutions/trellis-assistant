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

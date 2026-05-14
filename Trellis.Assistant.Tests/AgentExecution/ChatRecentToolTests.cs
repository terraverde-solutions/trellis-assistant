using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Trellis.Assistant.AgentExecution;
using Trellis.Assistant.Tests.TestFixtures;
using Trellis.Core.Models;
using Trellis.Core.Services;
using Xunit;

namespace Trellis.Assistant.Tests.AgentExecution;

/// <summary>
/// Pure-unit pins for <see cref="ChatRecentTool"/>. Stub
/// <see cref="IAssistantConversationStore"/> via a hand-rolled fake
/// (wired through a real <see cref="IServiceScopeFactory"/> from a
/// minimal <see cref="ServiceCollection"/>); no DB, no executor — just
/// the tool's behavior against the store boundary.
///
/// <para>
/// The Postgres-backed integration coverage for
/// <c>SearchRecentTurnsAsync</c> lives in
/// <see cref="Data.PostgresAssistantConversationStoreSearchTests"/>;
/// these tests pin the tool's mapping + edge cases (standalone sentinel,
/// schema validation bypass, store-failure mapping, snake_case wire
/// emit).
/// </para>
/// </summary>
public sealed class ChatRecentToolTests
{
    [Fact]
    public void Descriptor_HasExpectedShape()
    {
        var tool = NewTool(out _);
        tool.Descriptor.Name.Should().Be(ChatRecentTool.ToolName);
        tool.Descriptor.Category.Should().Be(AgentToolCategory.Search);
        tool.Descriptor.Description.Should().Contain("recent conversation history");
        tool.Descriptor.ParameterSchema.Should().Contain(@"""required"":");
        tool.Descriptor.ParameterSchema.Should().Contain("query");
    }

    [Fact]
    public void Descriptor_ParameterSchema_IsValidJsonSchema()
    {
        var tool = NewTool(out _);
        var validator = new JsonSchemaNetValidator();
        var act = () => validator.EnsureValidSchema(tool.Descriptor.ParameterSchema);
        act.Should().NotThrow(
            "ChatRecentTool's ParameterSchema must parse — startup ToolRegistry validation would crash the host otherwise");
    }

    [Fact]
    public async Task RunAsync_HappyPath_FlowsThroughTenantAndUserToStore_ReturnsSnakeCaseJson()
    {
        var tool = NewTool(out var stub);
        var convId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var turnId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var createdAt = new DateTime(2026, 5, 12, 14, 30, 0, DateTimeKind.Utc);
        stub.Results = new[]
        {
            new RecentTurnSummary(
                ConversationId: convId,
                TurnId: turnId,
                Position: 4,
                Role: AssistantTurnRole.User,
                Content: "What about the refund policy?",
                CreatedAt: createdAt),
        };

        var orgId = Guid.Parse(TestTenants.TenantA);
        var output = await tool.RunAsync(new AgentToolInput
        {
            AgentRunId = Guid.NewGuid(),
            StepIndex = 0,
            ParametersJson = """{"query":"refund","limit":5}""",
            OrgId = orgId,
            UserId = TestTenants.UserA,
        });

        output.Success.Should().BeTrue();
        output.ErrorMessage.Should().BeNull();
        output.ResultJson.Should().NotBeNull();

        // Tenant + user threaded through verbatim — tenantId is the
        // "D" form of orgId.
        stub.LastTenantId.Should().Be(orgId.ToString("D"));
        stub.LastUserId.Should().Be(TestTenants.UserA);
        stub.LastQuery.Should().Be("refund");
        stub.LastLimit.Should().Be(5);
        stub.LastSince.Should().BeNull();

        // snake_case wire emit. LLM sees conversation_id / turn_id /
        // created_at / position / role (lowercase enum wire string) /
        // content.
        output.ResultJson.Should().Contain("\"conversation_id\":");
        output.ResultJson.Should().Contain("\"turn_id\":");
        output.ResultJson.Should().Contain("\"created_at\":");
        output.ResultJson.Should().Contain("\"position\":4");
        output.ResultJson.Should().Contain("\"role\":\"user\"",
            "role serializes via Core's EnumMemberJsonConverter to the lowercase wire string");
        output.ResultJson.Should().Contain("What about the refund policy?");
    }

    [Fact]
    public async Task RunAsync_EmptyResult_ReturnsSuccessWithEmptyArray()
    {
        var tool = NewTool(out var stub);
        stub.Results = Array.Empty<RecentTurnSummary>();

        var output = await tool.RunAsync(NewInput("""{"query":"unmatched"}"""));

        output.Success.Should().BeTrue();
        output.ResultJson.Should().Be("[]",
            "empty result still surfaces as Success=true; LLM interprets 'no prior turns matched'");
    }

    [Fact]
    public async Task RunAsync_LimitOmitted_DefaultsTo10()
    {
        var tool = NewTool(out var stub);
        stub.Results = Array.Empty<RecentTurnSummary>();

        await tool.RunAsync(NewInput("""{"query":"x"}"""));

        stub.LastLimit.Should().Be(10,
            "Phase 3.D contract: omitting limit defaults to 10 (the v0 default; JSON Schema allows 1-50)");
    }

    [Fact]
    public async Task RunAsync_SincePassed_ForwardedToStore()
    {
        var tool = NewTool(out var stub);
        stub.Results = Array.Empty<RecentTurnSummary>();

        await tool.RunAsync(NewInput("""{"query":"x","since":"2026-05-10T00:00:00Z"}"""));

        stub.LastSince.Should().NotBeNull();
        stub.LastSince!.Value.Should().Be(new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task RunAsync_StandaloneSentinelUserId_ReturnsEmptyArrayWithoutStoreCall()
    {
        // PR #10 review pattern (Phase 3.C): operator-facing standalone
        // POST /api/agent-runs has no end-user identity. The sentinel
        // value "standalone" signals "skip user-scoped queries"; the
        // tool returns an empty array WITHOUT making a store call (no
        // hypothetical user named "standalone" to leak data from).
        var tool = NewTool(out var stub);
        stub.Results = new[]
        {
            new RecentTurnSummary(
                Guid.NewGuid(), Guid.NewGuid(), 0,
                AssistantTurnRole.User, "would leak if returned", DateTime.UtcNow),
        };

        var output = await tool.RunAsync(new AgentToolInput
        {
            AgentRunId = Guid.NewGuid(),
            StepIndex = 0,
            ParametersJson = """{"query":"anything"}""",
            OrgId = Guid.NewGuid(),
            UserId = AssistantAgentExecutor.AnonymousStandaloneUserId,
        });

        output.Success.Should().BeTrue();
        output.ResultJson.Should().Be("[]",
            "standalone sentinel returns empty array — no per-user history to surface");
        stub.LastQuery.Should().BeNull(
            "store is NOT consulted on the standalone sentinel — bypass is at the tool layer, no race for the store filter to leak");
    }

    [Fact]
    public async Task RunAsync_UnparseableArgsJson_ReturnsStructuredFailure()
    {
        var tool = NewTool(out _);
        var output = await tool.RunAsync(NewInput("{not json"));
        output.Success.Should().BeFalse();
        output.ErrorMessage.Should().Contain("failed to parse arguments JSON");
    }

    [Fact]
    public async Task RunAsync_MissingQuery_ReturnsStructuredFailure()
    {
        var tool = NewTool(out _);
        var output = await tool.RunAsync(NewInput("""{"limit":5}"""));
        output.Success.Should().BeFalse();
        output.ErrorMessage.Should().Contain("'query' was missing or empty");
    }

    [Fact]
    public async Task RunAsync_StoreThrows_ReturnsStructuredFailureNotPropagation()
    {
        // Per Core's IAgentTool contract: tools should NOT throw to
        // signal failure — return Success=false. Store-side failures
        // (Postgres connection drop, etc.) get caught + mapped at the
        // tool boundary so the executor's broad catch doesn't log
        // them as generic "tool threw" warnings.
        var tool = NewTool(out var stub);
        stub.ThrowOnNextCall = new InvalidOperationException("simulated store failure");

        var output = await tool.RunAsync(NewInput("""{"query":"x"}"""));

        output.Success.Should().BeFalse();
        output.ErrorMessage.Should().Contain("store-side failure");
        output.ErrorMessage.Should().Contain("simulated store failure");
    }

    [Fact]
    public async Task RunAsync_CancellationPropagatesAsOperationCanceled()
    {
        // Caller-CT cancellation rethrows (does NOT convert to a
        // structured failure) — matches the executor's OCE filter
        // upstack, same convention as HttpSearchClient + JsonSchemaNetValidator.
        var tool = NewTool(out _);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = () => tool.RunAsync(NewInput("""{"query":"x"}"""), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RunAsync_StoreCalled_WithTenantIdInCanonicalDForm()
    {
        // The tool converts AgentToolInput.OrgId (Guid) → tenantId
        // (string) for IAssistantConversationStore via the "D" format
        // (8-4-4-4-12, lowercase). This pins the format choice — the
        // store impl filters via case-sensitive Postgres column compare,
        // so format drift would silently return empty.
        var tool = NewTool(out var stub);
        stub.Results = Array.Empty<RecentTurnSummary>();
        var orgId = Guid.Parse("DEADBEEF-DEAD-BEEF-DEAD-BEEFDEADBEEF");

        await tool.RunAsync(new AgentToolInput
        {
            AgentRunId = Guid.NewGuid(),
            StepIndex = 0,
            ParametersJson = """{"query":"x"}""",
            OrgId = orgId,
            UserId = TestTenants.UserA,
        });

        stub.LastTenantId.Should().Be("deadbeef-dead-beef-dead-beefdeadbeef",
            "tenantId flows as the canonical lowercase 'D' format string");
    }

    // --------------- helpers ---------------

    private static ChatRecentTool NewTool(out StubAssistantConversationStore stub)
    {
        stub = new StubAssistantConversationStore();
        var services = new ServiceCollection();
        services.AddSingleton<IAssistantConversationStore>(stub);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new ChatRecentTool(scopeFactory, NullLogger<ChatRecentTool>.Instance);
    }

    private static AgentToolInput NewInput(string parametersJson) => new()
    {
        AgentRunId = Guid.NewGuid(),
        StepIndex = 0,
        ParametersJson = parametersJson,
        OrgId = Guid.Parse(TestTenants.TenantA),
        UserId = TestTenants.UserA,
    };

    /// <summary>
    /// Minimal IAssistantConversationStore stub — only
    /// SearchRecentTurnsAsync is exercised; other methods throw to
    /// catch accidental cross-method coupling. Stub captures the last
    /// (tenantId, userId, query, limit, since) call for assertions.
    /// </summary>
    private sealed class StubAssistantConversationStore : IAssistantConversationStore
    {
        public IReadOnlyList<RecentTurnSummary> Results { get; set; } = Array.Empty<RecentTurnSummary>();
        public Exception? ThrowOnNextCall { get; set; }

        public string? LastTenantId { get; private set; }
        public string? LastUserId { get; private set; }
        public string? LastQuery { get; private set; }
        public int? LastLimit { get; private set; }
        public DateTime? LastSince { get; private set; }

        public Task<IReadOnlyList<RecentTurnSummary>> SearchRecentTurnsAsync(
            string tenantId,
            string userId,
            string query,
            int limit,
            DateTime? since,
            CancellationToken cancellationToken = default)
        {
            LastTenantId = tenantId;
            LastUserId = userId;
            LastQuery = query;
            LastLimit = limit;
            LastSince = since;
            if (ThrowOnNextCall is { } ex)
            {
                ThrowOnNextCall = null;
                throw ex;
            }
            return Task.FromResult(Results);
        }

        // Other interface methods unused — throw to catch accidental dependency.
        public Task<Guid> CreateConversationAsync(string tenantId, string userId, string channel, string? model, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("ChatRecentToolTests stub doesn't implement this method");
        public Task<AssistantConversation?> GetConversationAsync(string tenantId, string userId, Guid conversationId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("ChatRecentToolTests stub doesn't implement this method");
        public Task<IReadOnlyList<AssistantTurn>> GetTurnsAsync(string tenantId, string userId, Guid conversationId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("ChatRecentToolTests stub doesn't implement this method");
        public Task<IReadOnlyList<AssistantTurn>> AppendTurnsAsync(string tenantId, string userId, Guid conversationId, IReadOnlyList<NewAssistantTurn> turns, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("ChatRecentToolTests stub doesn't implement this method");
    }
}

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Trellis.Assistant.Data;
using Trellis.Assistant.Tests.TestFixtures;
using Trellis.Core.Services;
using Xunit;

namespace Trellis.Assistant.Tests.Data;

/// <summary>
/// Postgres-backed integration tests for
/// <see cref="PostgresAssistantConversationStore.SearchRecentTurnsAsync"/>.
/// Pins the SQL semantics that <see cref="AgentExecution.ChatRecentTool"/>
/// depends on:
/// <list type="bullet">
/// <item>Tenant + user scope enforced via the conversation FK join —
/// turns from a different (tenant, user) tuple do NOT appear in the
/// results.</item>
/// <item><c>EF.Functions.ILike</c> compiles to PostgreSQL <c>ILIKE</c>
/// and the substring match is case-insensitive.</item>
/// <item><c>since</c> filters on <c>turns.created_at</c> (NOT
/// <c>conversations.updated_at</c>).</item>
/// <item>Results sorted newest-first by <c>turns.created_at</c>.</item>
/// <item>Limit clamp 1-50 applied defensively at the store layer.</item>
/// </list>
/// </summary>
public sealed class PostgresAssistantConversationStoreSearchTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private AssistantDbContext? _db;

    public PostgresAssistantConversationStoreSearchTests(PostgresFixture pg)
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
    public async Task SearchRecentTurnsAsync_TenantAndUserScope_FiltersOutOtherTenantsAndUsers()
    {
        // The pin that protects against cross-tenant leak. Three
        // conversations across two tenants; each has a turn whose
        // content matches the same query. The (TenantA, UserA) caller
        // should see ONLY (TenantA, UserA)'s turn.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var store = NewStore();
        var convA = await SeedConversationWithTurn(TestTenants.TenantA, TestTenants.UserA,
            "TenantA UserA refund inquiry");
        await SeedConversationWithTurn(TestTenants.TenantB, TestTenants.UserA,
            "TenantB UserA — SAME user id, DIFFERENT tenant — refund inquiry");
        await SeedConversationWithTurn(TestTenants.TenantA, TestTenants.UserB,
            "TenantA UserB — same tenant, DIFFERENT user — refund inquiry");

        var results = await store.SearchRecentTurnsAsync(
            TestTenants.TenantA, TestTenants.UserA, "refund", limit: 10, since: null);

        results.Should().HaveCount(1,
            "tenant + user scope is enforced — TenantA/UserA caller sees only their own turn");
        results[0].ConversationId.Should().Be(convA);
        results[0].Content.Should().Contain("TenantA UserA");
    }

    [SkippableFact]
    public async Task SearchRecentTurnsAsync_IlikeIsCaseInsensitive()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var store = NewStore();
        await SeedConversationWithTurn(TestTenants.TenantA, TestTenants.UserA,
            "We accept REFUNDS within thirty days.");

        var lowerResults = await store.SearchRecentTurnsAsync(
            TestTenants.TenantA, TestTenants.UserA, "refunds", limit: 10, since: null);
        var upperResults = await store.SearchRecentTurnsAsync(
            TestTenants.TenantA, TestTenants.UserA, "REFUNDS", limit: 10, since: null);

        lowerResults.Should().HaveCount(1,
            "ILIKE matches REFUNDS (uppercase content) for a 'refunds' (lowercase) query");
        upperResults.Should().HaveCount(1);
    }

    [SkippableFact]
    public async Task SearchRecentTurnsAsync_SinceFiltersByTurnCreatedAt()
    {
        // Phase 3.D Q4 ratify: since filters turn.created_at, NOT
        // conversation.updated_at. Pin this directly — seed two turns
        // in the same conversation with different created_at values.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        // Per-test unique token to avoid cross-test data accumulation
        // (the PostgresFixture is IClassFixture — data persists across
        // tests in the class; two tests sharing the same "marker" token
        // would see each other's seeded turns and break the exact-count
        // assertions).
        const string token = "since-token-rare";
        var store = NewStore();
        var (convId, oldTurn, newTurn) = await SeedTwoTurnsAtDifferentTimes(
            TestTenants.TenantA, TestTenants.UserA, token);

        // since = now - 1 hour → only the new turn matches
        var resultsRecent = await store.SearchRecentTurnsAsync(
            TestTenants.TenantA, TestTenants.UserA, token,
            limit: 10, since: DateTime.UtcNow.AddHours(-1));

        resultsRecent.Should().HaveCount(1,
            "since clamp excludes the old turn (created_at 2026-04-01)");
        resultsRecent[0].TurnId.Should().Be(newTurn);

        // since = null → both turns match
        var resultsAll = await store.SearchRecentTurnsAsync(
            TestTenants.TenantA, TestTenants.UserA, token,
            limit: 10, since: null);

        resultsAll.Should().HaveCount(2);
    }

    [SkippableFact]
    public async Task SearchRecentTurnsAsync_ResultsSortedNewestFirst()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        // Per-test unique token, distinct from the since-filter test —
        // see SearchRecentTurnsAsync_SinceFiltersByTurnCreatedAt for the
        // shared-fixture data-accumulation rationale.
        const string token = "sort-token-rare";
        var store = NewStore();
        await SeedTwoTurnsAtDifferentTimes(TestTenants.TenantA, TestTenants.UserA, token);

        var results = await store.SearchRecentTurnsAsync(
            TestTenants.TenantA, TestTenants.UserA, token,
            limit: 10, since: null);

        results.Should().HaveCount(2);
        results[0].CreatedAt.Should().BeAfter(results[1].CreatedAt,
            "results sorted by turn.created_at DESC — newest first");
    }

    [SkippableFact]
    public async Task SearchRecentTurnsAsync_LimitClampedAtFifty()
    {
        // Defensive store-layer clamp. Caller's JSON Schema enforces
        // 1-50 client-side, but the store doesn't trust callers per
        // the chokepoint discipline. Asking for 1000 still caps at 50.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var store = NewStore();
        await SeedNTurnsInOneConversation(TestTenants.TenantA, TestTenants.UserA, count: 60);

        var results = await store.SearchRecentTurnsAsync(
            TestTenants.TenantA, TestTenants.UserA, "burst-marker",
            limit: 1000, since: null);

        results.Should().HaveCount(50,
            "defensive limit clamp caps at 50 regardless of caller value");
    }

    [SkippableFact]
    public async Task SearchRecentTurnsAsync_EmptyQuery_Throws()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var store = NewStore();
        var act = () => store.SearchRecentTurnsAsync(
            TestTenants.TenantA, TestTenants.UserA, "",
            limit: 10, since: null);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [SkippableFact]
    public async Task SearchRecentTurnsAsync_CyrillicContent_RoundTripsAsSubstringMatch()
    {
        // PR #11 review v2 Blocker fix: Cyrillic UTF-8 round-trip pin.
        // Pins the load-bearing property — UTF-8 content stores +
        // retrieves through the EF/Postgres boundary without mojibake
        // or truncation, AND ILIKE matches a same-case Cyrillic
        // substring.
        //
        // Pre-fix this test asserted cross-case fold ("Возврат" content
        // vs "возврат" query). That dependency was wrong: PostgreSQL
        // ILIKE only folds ASCII A-Z under the default LC_CTYPE=C
        // locale, which the postgres:16 Testcontainers image uses.
        // Non-ASCII case-fold requires a UTF-8 locale (POSTGRES_INITDB_ARGS
        // --locale=en_US.UTF-8) which would couple the test fixture to
        // a locale that production Postgres may or may not match —
        // scope creep + over-fit. Same-case substring match preserves
        // the actual regression value we care about (storage + retrieval +
        // ILIKE works against non-ASCII bytes).
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var store = NewStore();
        await SeedConversationWithTurn(TestTenants.TenantA, TestTenants.UserA,
            "возврат средств возможен в течение 30 дней");

        var results = await store.SearchRecentTurnsAsync(
            TestTenants.TenantA, TestTenants.UserA, "возврат", limit: 10, since: null);

        results.Should().HaveCount(1,
            "ILIKE matches a same-case Cyrillic substring + the EF/Postgres boundary round-trips UTF-8 content end-to-end");
        results[0].Content.Should().Contain("возврат средств",
            "content stored as UTF-8 round-trips through the read path without mojibake or truncation");
    }

    [SkippableFact]
    public async Task SearchRecentTurnsAsync_NoMatches_ReturnsEmptyList()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var store = NewStore();
        await SeedConversationWithTurn(TestTenants.TenantA, TestTenants.UserA, "a different topic");

        var results = await store.SearchRecentTurnsAsync(
            TestTenants.TenantA, TestTenants.UserA, "unmatched-token",
            limit: 10, since: null);

        results.Should().BeEmpty();
    }

    // --------- helpers ---------

    private PostgresAssistantConversationStore NewStore()
    {
        return new PostgresAssistantConversationStore(_db!);
    }

    private async Task<Guid> SeedConversationWithTurn(string tenantId, string userId, string content)
    {
        var store = NewStore();
        var convId = await store.CreateConversationAsync(tenantId, userId, channel: "api", model: null);
        await store.AppendTurnsAsync(tenantId, userId, convId, new[]
        {
            new NewAssistantTurn(AssistantTurnRole.User, content),
        });
        return convId;
    }

    private async Task<(Guid ConvId, Guid OldTurnId, Guid NewTurnId)> SeedTwoTurnsAtDifferentTimes(
        string tenantId, string userId, string markerToken)
    {
        // Seed two turns through the store, then surgically backdate
        // one of them via direct EF update. The store's CreatedAt is
        // set to DateTime.UtcNow at write time; we can't pre-set it
        // through the public API.
        //
        // The markerToken parameter lets each caller use a unique
        // substring — the PostgresFixture is IClassFixture, so data
        // persists across tests in the class. Two tests sharing the
        // same token would see each other's seeded turns and break
        // exact-count assertions.
        var store = NewStore();
        var convId = await store.CreateConversationAsync(tenantId, userId, channel: "api", model: null);
        var persisted = await store.AppendTurnsAsync(tenantId, userId, convId, new[]
        {
            new NewAssistantTurn(AssistantTurnRole.User, $"{markerToken} — old turn"),
            new NewAssistantTurn(AssistantTurnRole.Assistant, $"{markerToken} — new turn"),
        });
        var oldTurnId = persisted[0].Id;
        var newTurnId = persisted[1].Id;

        // Backdate the first turn to 2026-04-01 so the since-filter
        // pins behave deterministically. Direct EF update — bypasses
        // the store's public API (which intentionally has no setter
        // for CreatedAt).
        var oldDate = new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc);
        await _db!.Turns
            .Where(t => t.Id == oldTurnId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.CreatedAt, oldDate));

        return (convId, oldTurnId, newTurnId);
    }

    private async Task SeedNTurnsInOneConversation(string tenantId, string userId, int count)
    {
        var store = NewStore();
        var convId = await store.CreateConversationAsync(tenantId, userId, channel: "api", model: null);
        var turns = Enumerable.Range(0, count)
            .Select(i => new NewAssistantTurn(
                i % 2 == 0 ? AssistantTurnRole.User : AssistantTurnRole.Assistant,
                $"burst-marker turn {i}"))
            .ToList();
        await store.AppendTurnsAsync(tenantId, userId, convId, turns);
    }
}

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Trellis.Assistant.Data;
using Trellis.Assistant.Tests.TestFixtures;
using Xunit;

namespace Trellis.Assistant.Tests.Integration;

/// <summary>
/// Pin the EF migration round-trips on an empty trellis_assistant_qa-
/// shaped DB. Verifies:
///   - <c>conversations</c> + <c>turns</c> tables exist with the
///     expected column shape (names, types, nullability)
///   - The 3 indexes land (<c>ix_conversations_tenant_user</c>,
///     <c>ix_conversations_tenant_user_channel</c>,
///     <c>ux_turns_conversation_position</c> with UNIQUE)
///   - The FK <c>turns.conversation_id → conversations.id</c> with
///     ON DELETE CASCADE applies — deleting a conversation purges
///     its turns
///   - The <c>channel</c> column DEFAULT 'api' applies on inserts
///     that omit the column
/// Skipped cleanly when Docker isn't available (Testcontainers
/// dependency).
/// </summary>
public sealed class EFMigrationSmokeTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _pg;

    public EFMigrationSmokeTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    [SkippableFact]
    public async Task Migration_AppliesToEmptyDatabase_CreatesExpectedSchema()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        // Apply the migration on a fresh container.
        await using var db = NewContext();
        await db.Database.MigrateAsync();

        // ---- conversations table shape ----
        await using var conn = new NpgsqlConnection(_pg.ConnectionString);
        await conn.OpenAsync();

        var conversationsColumns = await ReadColumnsAsync(conn, "conversations");
        conversationsColumns.Should().BeEquivalentTo(new[]
        {
            new ColumnShape("id", "uuid", IsNullable: false, ColumnDefault: null),
            new ColumnShape("tenant_id", "character varying", IsNullable: false, ColumnDefault: null),
            new ColumnShape("user_id", "character varying", IsNullable: false, ColumnDefault: null),
            new ColumnShape("channel", "character varying", IsNullable: false, ColumnDefault: "'api'::character varying"),
            new ColumnShape("created_at", "timestamp with time zone", IsNullable: false, ColumnDefault: null),
            new ColumnShape("updated_at", "timestamp with time zone", IsNullable: false, ColumnDefault: null),
        }, opts => opts.WithoutStrictOrdering(),
        "conversations table must carry exactly these columns + types + nullability + defaults");

        var turnsColumns = await ReadColumnsAsync(conn, "turns");
        turnsColumns.Should().BeEquivalentTo(new[]
        {
            new ColumnShape("id", "uuid", IsNullable: false, ColumnDefault: null),
            new ColumnShape("conversation_id", "uuid", IsNullable: false, ColumnDefault: null),
            new ColumnShape("position", "integer", IsNullable: false, ColumnDefault: null),
            new ColumnShape("role", "character varying", IsNullable: false, ColumnDefault: null),
            new ColumnShape("content", "text", IsNullable: false, ColumnDefault: null),
            new ColumnShape("created_at", "timestamp with time zone", IsNullable: false, ColumnDefault: null),
        }, opts => opts.WithoutStrictOrdering());

        // ---- indexes ----
        var indexes = await ReadIndexesAsync(conn);
        indexes.Should().Contain("ix_conversations_tenant_user");
        indexes.Should().Contain("ix_conversations_tenant_user_channel");
        indexes.Should().Contain("ux_turns_conversation_position");

        // The unique-on-(conversation_id, position) constraint is
        // load-bearing for the orchestrator's "concurrent appends
        // can't race the position assignment" invariant. Pin it
        // explicitly as an index-uniqueness check.
        var uniqueIndexes = await ReadUniqueIndexesAsync(conn);
        uniqueIndexes.Should().Contain("ux_turns_conversation_position");
    }

    [SkippableFact]
    public async Task Migration_FkCascade_DeletesTurnsWithConversation()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        await using var db = NewContext();
        await db.Database.MigrateAsync();

        var conv = new ConversationEntity
        {
            Id = Guid.NewGuid(),
            TenantId = "t1",
            UserId = "u1",
            Channel = "api",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Conversations.Add(conv);
        db.Turns.Add(new TurnEntity
        {
            Id = Guid.NewGuid(),
            ConversationId = conv.Id,
            Position = 0,
            Role = "user",
            Content = "hi",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        db.Conversations.Remove(conv);
        await db.SaveChangesAsync();

        var orphanCount = await db.Turns.CountAsync(t => t.ConversationId == conv.Id);
        orphanCount.Should().Be(0,
            "FK with ON DELETE CASCADE should drop turns when the parent conversation is deleted");
    }

    [SkippableFact]
    public async Task Migration_ChannelDefault_ApplyAtRawSqlInsert()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        await using var db = NewContext();
        await db.Database.MigrateAsync();

        // Raw SQL insert that omits the channel column — the DEFAULT 'api'
        // should apply at the DB layer. EF would always send the value
        // (it tracks every property), so this test pokes the DB directly.
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO conversations (id, tenant_id, user_id, created_at, updated_at)
            VALUES ({id}, 't1', 'u1', NOW(), NOW())");

        var inserted = await db.Conversations
            .AsNoTracking()
            .SingleAsync(c => c.Id == id);
        inserted.Channel.Should().Be("api",
            "DEFAULT 'api' on the channel column applies to inserts that don't specify channel");
    }

    private AssistantDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<AssistantDbContext>()
            .UseNpgsql(_pg.ConnectionString)
            .Options;
        return new AssistantDbContext(options);
    }

    // ---------------- Postgres metadata helpers ----------------

    private sealed record ColumnShape(string Name, string DataType, bool IsNullable, string? ColumnDefault);

    private static async Task<List<ColumnShape>> ReadColumnsAsync(NpgsqlConnection conn, string tableName)
    {
        var rows = new List<ColumnShape>();
        await using var cmd = new NpgsqlCommand(@"
            SELECT column_name, data_type, is_nullable, column_default
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @table
            ORDER BY ordinal_position", conn);
        cmd.Parameters.AddWithValue("@table", tableName);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new ColumnShape(
                Name: reader.GetString(0),
                DataType: reader.GetString(1),
                IsNullable: reader.GetString(2).Equals("YES", StringComparison.OrdinalIgnoreCase),
                ColumnDefault: reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        return rows;
    }

    private static async Task<List<string>> ReadIndexesAsync(NpgsqlConnection conn)
    {
        var rows = new List<string>();
        await using var cmd = new NpgsqlCommand(@"
            SELECT indexname FROM pg_indexes
            WHERE schemaname = 'public'
            ORDER BY indexname", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }
        return rows;
    }

    private static async Task<List<string>> ReadUniqueIndexesAsync(NpgsqlConnection conn)
    {
        var rows = new List<string>();
        await using var cmd = new NpgsqlCommand(@"
            SELECT i.relname
            FROM pg_index x
            JOIN pg_class i ON i.oid = x.indexrelid
            JOIN pg_class t ON t.oid = x.indrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            WHERE n.nspname = 'public' AND x.indisunique = true
            ORDER BY i.relname", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }
        return rows;
    }
}

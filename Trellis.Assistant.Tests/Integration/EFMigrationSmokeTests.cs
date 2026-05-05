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
            new ColumnShape("model", "character varying", IsNullable: false, ColumnDefault: "'mistral-small:24b'::character varying"),
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
            // Phase 3.A.2: nullable tool-turn metadata. Both null on
            // user/assistant/system turns; populated on Tool turns.
            new ColumnShape("tool_call_id", "character varying", IsNullable: true, ColumnDefault: null),
            new ColumnShape("tool_name", "character varying", IsNullable: true, ColumnDefault: null),
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
            TenantId = TestTenants.TenantRaw,
            UserId = TestTenants.UserRaw,
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
            VALUES ({id}, {TestTenants.TenantRaw}, {TestTenants.UserRaw}, NOW(), NOW())");

        var inserted = await db.Conversations
            .AsNoTracking()
            .SingleAsync(c => c.Id == id);
        inserted.Channel.Should().Be("api",
            "DEFAULT 'api' on the channel column applies to inserts that don't specify channel");
    }

    [SkippableFact]
    public async Task Migration_AddsAgentRunsAndAgentStepsTables_WithExpectedColumnShape()
    {
        // Phase 3.A.1 migration adds agent_runs + agent_steps. Pin the
        // column shape via information_schema so a future ModelSnapshot
        // drift surfaces here loud, not at first agent-run dispatch.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        await using var db = NewContext();
        await db.Database.MigrateAsync();

        await using var conn = new NpgsqlConnection(_pg.ConnectionString);
        await conn.OpenAsync();

        var agentRunsCols = await ReadColumnsAsync(conn, "agent_runs");
        agentRunsCols.Should().BeEquivalentTo(new[]
        {
            new ColumnShape("id", "uuid", IsNullable: false, ColumnDefault: null),
            new ColumnShape("org_id", "uuid", IsNullable: false, ColumnDefault: null),
            new ColumnShape("assistant_turn_id", "uuid", IsNullable: true, ColumnDefault: null),
            new ColumnShape("plan", "text", IsNullable: false, ColumnDefault: null),
            new ColumnShape("status", "character varying", IsNullable: false, ColumnDefault: null),
            new ColumnShape("started_at", "timestamp with time zone", IsNullable: false, ColumnDefault: null),
            new ColumnShape("completed_at", "timestamp with time zone", IsNullable: true, ColumnDefault: null),
            new ColumnShape("archived_at", "timestamp with time zone", IsNullable: true, ColumnDefault: null),
            new ColumnShape("tokens_used", "bigint", IsNullable: false, ColumnDefault: "0"),
            new ColumnShape("error_message", "text", IsNullable: true, ColumnDefault: null),
        }, opts => opts.WithoutStrictOrdering(),
        "agent_runs table must carry exactly these columns + types + nullability + defaults");

        var agentStepsCols = await ReadColumnsAsync(conn, "agent_steps");
        agentStepsCols.Should().BeEquivalentTo(new[]
        {
            new ColumnShape("id", "uuid", IsNullable: false, ColumnDefault: null),
            new ColumnShape("agent_run_id", "uuid", IsNullable: false, ColumnDefault: null),
            new ColumnShape("step_index", "integer", IsNullable: false, ColumnDefault: null),
            new ColumnShape("tool_name", "character varying", IsNullable: false, ColumnDefault: null),
            new ColumnShape("tool_input_json", "text", IsNullable: false, ColumnDefault: null),
            new ColumnShape("tool_output_json", "text", IsNullable: true, ColumnDefault: null),
            new ColumnShape("status", "character varying", IsNullable: false, ColumnDefault: null),
            new ColumnShape("started_at", "timestamp with time zone", IsNullable: false, ColumnDefault: null),
            new ColumnShape("completed_at", "timestamp with time zone", IsNullable: true, ColumnDefault: null),
            new ColumnShape("error_message", "text", IsNullable: true, ColumnDefault: null),
            new ColumnShape("duration_ms", "bigint", IsNullable: false, ColumnDefault: "0"),
        }, opts => opts.WithoutStrictOrdering());

        // Indexes from the Phase 3.A migration:
        // - ix_agent_runs_org_id_started_at (composite per hub's ratification)
        // - ux_agent_steps_run_step (unique)
        var indexes = await ReadIndexesAsync(conn);
        indexes.Should().Contain("ix_agent_runs_org_id_started_at",
            "composite (org_id, started_at) per Phase 3.A index ratification — dominant query pattern");
        indexes.Should().Contain("ux_agent_steps_run_step");

        var uniqueIndexes = await ReadUniqueIndexesAsync(conn);
        uniqueIndexes.Should().Contain("ux_agent_steps_run_step",
            "(agent_run_id, step_index) unique pin guards against the executor double-dispatching at the same index");
    }

    [SkippableFact]
    public async Task Migration_ToolCallIdAndToolName_BackwardsCompatWithExistingRows()
    {
        // Phase 3.A.2 strict-additive migration: existing user/assistant
        // turn rows from Phase 1+2 load with NULL for both tool_call_id +
        // tool_name. This pin guards against a future migration accidentally
        // making the columns NOT NULL or adding a default value, either
        // of which would break backwards-compat for the Phase 1+2 row
        // population.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        await using var db = NewContext();
        await db.Database.MigrateAsync();

        // Insert a Phase-1+2-shaped row without specifying tool_call_id /
        // tool_name. The migration's nullable columns mean no DEFAULT to
        // apply; both should land as NULL.
        var conv = new ConversationEntity
        {
            Id = Guid.NewGuid(),
            TenantId = TestTenants.TenantRaw,
            UserId = TestTenants.UserRaw,
            Channel = "api",
            Model = "mistral-small:24b",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Conversations.Add(conv);
        var legacyTurn = new TurnEntity
        {
            Id = Guid.NewGuid(),
            ConversationId = conv.Id,
            Position = 0,
            Role = "user",
            Content = "phase 1+2 user turn",
            CreatedAt = DateTime.UtcNow,
            // Intentionally NOT setting ToolCallId / ToolName — Phase 1+2
            // rows would have neither.
        };
        db.Turns.Add(legacyTurn);
        await db.SaveChangesAsync();

        var reloaded = await db.Turns.AsNoTracking().SingleAsync(t => t.Id == legacyTurn.Id);
        reloaded.ToolCallId.Should().BeNull(
            "Phase 3.A.2 migration adds tool_call_id as nullable; Phase 1+2 rows persist without a value");
        reloaded.ToolName.Should().BeNull(
            "Phase 3.A.2 migration adds tool_name as nullable; Phase 1+2 rows persist without a value");

        // And a Phase 3.A.2 Tool turn DOES populate both. Round-trip pin.
        var toolTurn = new TurnEntity
        {
            Id = Guid.NewGuid(),
            ConversationId = conv.Id,
            Position = 1,
            Role = "tool",
            Content = "{\"output\":\"hi\"}",
            ToolCallId = "call_abc",
            ToolName = "echo",
            CreatedAt = DateTime.UtcNow,
        };
        db.Turns.Add(toolTurn);
        await db.SaveChangesAsync();

        var reloadedTool = await db.Turns.AsNoTracking().SingleAsync(t => t.Id == toolTurn.Id);
        reloadedTool.Role.Should().Be("tool");
        reloadedTool.ToolCallId.Should().Be("call_abc");
        reloadedTool.ToolName.Should().Be("echo");
    }

    [SkippableFact]
    public async Task Migration_AgentSteps_FkCascade_DeletesStepsWithRun()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        await using var db = NewContext();
        await db.Database.MigrateAsync();

        var run = new AgentRunEntity
        {
            Id = Guid.NewGuid(),
            OrgId = Guid.Parse(TestTenants.TenantA),
            AssistantTurnId = null,
            Plan = "test",
            Status = "running",
            StartedAt = DateTime.UtcNow,
            TokensUsed = 0,
        };
        db.AgentRuns.Add(run);
        db.AgentSteps.Add(new AgentStepEntity
        {
            Id = Guid.NewGuid(),
            AgentRunId = run.Id,
            StepIndex = 0,
            ToolName = "echo",
            ToolInputJson = "{}",
            Status = "succeeded",
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
            DurationMs = 5,
        });
        await db.SaveChangesAsync();

        db.AgentRuns.Remove(run);
        await db.SaveChangesAsync();

        var orphanCount = await db.AgentSteps.CountAsync(s => s.AgentRunId == run.Id);
        orphanCount.Should().Be(0,
            "FK with ON DELETE CASCADE drops agent_steps when their parent agent_run is deleted");
    }

    [SkippableFact]
    public async Task Migration_AgentRuns_FkSetNull_PreservesRunWhenAssistantTurnDeleted()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        await using var db = NewContext();
        await db.Database.MigrateAsync();

        // Build a conversation + turn first; agent_run.assistant_turn_id
        // points at it.
        var conv = new ConversationEntity
        {
            Id = Guid.NewGuid(),
            TenantId = TestTenants.TenantRaw,
            UserId = TestTenants.UserRaw,
            Channel = "api",
            Model = "mistral-small:24b",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Conversations.Add(conv);
        var turn = new TurnEntity
        {
            Id = Guid.NewGuid(),
            ConversationId = conv.Id,
            Position = 0,
            Role = "assistant",
            Content = "agent reply",
            CreatedAt = DateTime.UtcNow,
        };
        db.Turns.Add(turn);
        await db.SaveChangesAsync();

        var run = new AgentRunEntity
        {
            Id = Guid.NewGuid(),
            OrgId = Guid.Parse(TestTenants.TenantA),
            AssistantTurnId = turn.Id,
            Plan = "linked-to-turn",
            Status = "succeeded",
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
            TokensUsed = 0,
        };
        db.AgentRuns.Add(run);
        await db.SaveChangesAsync();

        // Delete the parent conversation → cascades to the turn (existing
        // FK) → SET NULL on agent_runs.assistant_turn_id (Phase 3.A FK).
        db.Conversations.Remove(conv);
        await db.SaveChangesAsync();

        // Re-read the run; turn id is now NULL but the run survives.
        var reloaded = await db.AgentRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id);
        reloaded.AssistantTurnId.Should().BeNull(
            "FK with ON DELETE SET NULL clears the reference when the linked turn is removed");
        reloaded.Plan.Should().Be("linked-to-turn",
            "the agent run's audit history must outlive its linked conversation turn");
    }

    [SkippableFact]
    public async Task Migration_ModelDefault_ApplyAtRawSqlInsert()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        await using var db = NewContext();
        await db.Database.MigrateAsync();

        // Raw SQL insert that omits the model column — the DEFAULT
        // 'mistral-small:24b' (Phase 2 add) should apply at the DB layer.
        // Defense-in-depth for non-EF inserts; the C# side in
        // PostgresAssistantConversationStore.CreateConversationAsync also
        // applies the same default explicitly because EF tracks every
        // property and would send NULL otherwise.
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO conversations (id, tenant_id, user_id, created_at, updated_at)
            VALUES ({id}, {TestTenants.TenantRaw}, {TestTenants.UserRaw}, NOW(), NOW())");

        var inserted = await db.Conversations
            .AsNoTracking()
            .SingleAsync(c => c.Id == id);
        inserted.Model.Should().Be("mistral-small:24b",
            "DEFAULT 'mistral-small:24b' on the model column applies to inserts that don't specify model");
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

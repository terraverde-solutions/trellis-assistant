using Microsoft.EntityFrameworkCore;
using Trellis.Core.Services;

namespace Trellis.Assistant.Data;

/// <summary>
/// EF Core 10 + Npgsql provider DbContext for trellis_assistant_qa.
/// Two tables: <c>conversations</c> + <c>turns</c>. Schema is owned by
/// EF migrations under <c>Trellis.Assistant/Migrations/</c>; auto-applied
/// on startup when <c>Assistant:AutoMigrate=true</c> (the default —
/// matches trellis-trainer's pattern).
///
/// Entity types are deliberately separate from the Trellis.Core POCO
/// records (<see cref="AssistantConversation"/>, <see cref="AssistantTurn"/>).
/// The Core records are wire-shape contracts; the entities carry EF
/// tracking + column mappings. The impl
/// (<see cref="PostgresAssistantConversationStore"/>) maps between them
/// at the boundary so EF concerns don't bleed into Core consumers.
/// </summary>
public sealed class AssistantDbContext : DbContext
{
    public AssistantDbContext(DbContextOptions<AssistantDbContext> options)
        : base(options)
    {
    }

    public DbSet<ConversationEntity> Conversations => Set<ConversationEntity>();
    public DbSet<TurnEntity> Turns => Set<TurnEntity>();
    public DbSet<AgentRunEntity> AgentRuns => Set<AgentRunEntity>();
    public DbSet<AgentStepEntity> AgentSteps => Set<AgentStepEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ---- conversations ----
        var convo = modelBuilder.Entity<ConversationEntity>();
        convo.ToTable("conversations");
        convo.HasKey(c => c.Id);
        convo.Property(c => c.Id).HasColumnName("id");
        convo.Property(c => c.TenantId).HasColumnName("tenant_id").HasMaxLength(64).IsRequired();
        convo.Property(c => c.UserId).HasColumnName("user_id").HasMaxLength(64).IsRequired();
        // varchar(32) NOT NULL DEFAULT 'api'. No CHECK constraint enumerating
        // values today — Phase 4 (Slack), v0+ (web, desktop, mobile) add
        // values without a migration. Index below for the natural query
        // path "conversations on Slack for user X".
        convo.Property(c => c.Channel).HasColumnName("channel")
            .HasMaxLength(32)
            .IsRequired()
            .HasDefaultValue("api");
        // varchar(64) NOT NULL DEFAULT 'mistral-small:24b'. The C# layer
        // also applies this default in PostgresAssistantConversationStore
        // because EF tracks every property and would send NULL otherwise;
        // the SQL DEFAULT covers raw INSERTs (the EFMigrationSmokeTests
        // pattern). Phase 2 doesn't enforce an allowlist on this value;
        // an invalid model tag surfaces as a 502 from Ollama on first
        // turn, not a 400 at create time.
        convo.Property(c => c.Model).HasColumnName("model")
            .HasMaxLength(64)
            .IsRequired()
            .HasDefaultValue("mistral-small:24b");
        convo.Property(c => c.CreatedAt).HasColumnName("created_at").IsRequired();
        convo.Property(c => c.UpdatedAt).HasColumnName("updated_at").IsRequired();

        // (tenant_id, user_id) is the natural lookup key — every endpoint
        // filters by it. Add (tenant_id, user_id, channel) too for the
        // Phase 4 channel-scoped query path even though Phase 1 only
        // exercises channel='api'.
        convo.HasIndex(c => new { c.TenantId, c.UserId })
            .HasDatabaseName("ix_conversations_tenant_user");
        convo.HasIndex(c => new { c.TenantId, c.UserId, c.Channel })
            .HasDatabaseName("ix_conversations_tenant_user_channel");

        // ---- turns ----
        var turn = modelBuilder.Entity<TurnEntity>();
        turn.ToTable("turns");
        turn.HasKey(t => t.Id);
        turn.Property(t => t.Id).HasColumnName("id");
        turn.Property(t => t.ConversationId).HasColumnName("conversation_id").IsRequired();
        turn.Property(t => t.Position).HasColumnName("position").IsRequired();
        // Role stored as the lowercase wire string ("user", "assistant",
        // "system"). Future Phase 3 adds "tool" without a schema change.
        // Conversion lives in PostgresAssistantConversationStore — the EF
        // column is opaque text from the DB's perspective.
        turn.Property(t => t.Role).HasColumnName("role").HasMaxLength(32).IsRequired();
        turn.Property(t => t.Content).HasColumnName("content").IsRequired();
        turn.Property(t => t.CreatedAt).HasColumnName("created_at").IsRequired();

        // FK with ON DELETE CASCADE — deleting a conversation drops its
        // turns. Matches trellis-trainer's Documents -> DocumentChunks
        // pattern.
        turn.HasOne<ConversationEntity>()
            .WithMany()
            .HasForeignKey(t => t.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        // (conversation_id, position) is unique — pins the ordering
        // invariant at the DB level. The orchestrator's per-conversation
        // advisory lock + atomic position assignment under the lock are
        // what make insert-time uniqueness fail-safe; this constraint
        // catches a programming bug if either layer regresses.
        turn.HasIndex(t => new { t.ConversationId, t.Position })
            .IsUnique()
            .HasDatabaseName("ux_turns_conversation_position");

        // ---- agent_runs (Phase 3.A.1) ----
        // Persists Trellis.Core's AgentRun shape. Multi-tenant scope via
        // OrgId (Phase 0 PR #10's contract — distinct from the
        // conversations table's (TenantId, UserId) tuple). The
        // AssistantTurnId FK is ON DELETE SET NULL — a deleted
        // conversation turn shouldn't cascade-delete the agent run audit
        // log (operators may need to inspect the run after the
        // conversation is gone).
        var agentRun = modelBuilder.Entity<AgentRunEntity>();
        agentRun.ToTable("agent_runs");
        agentRun.HasKey(r => r.Id);
        agentRun.Property(r => r.Id).HasColumnName("id");
        agentRun.Property(r => r.OrgId).HasColumnName("org_id").IsRequired();
        agentRun.Property(r => r.AssistantTurnId).HasColumnName("assistant_turn_id");
        agentRun.Property(r => r.Plan).HasColumnName("plan").IsRequired();
        agentRun.Property(r => r.Status).HasColumnName("status").HasMaxLength(32).IsRequired();
        agentRun.Property(r => r.StartedAt).HasColumnName("started_at").IsRequired();
        agentRun.Property(r => r.CompletedAt).HasColumnName("completed_at");
        agentRun.Property(r => r.ArchivedAt).HasColumnName("archived_at");
        // bigint per hub's Phase 3.A ratification suggestion (long runs
        // could overflow int's 2.1B ceiling at 4k tokens/step × 100k steps).
        agentRun.Property(r => r.TokensUsed).HasColumnName("tokens_used").IsRequired().HasDefaultValue(0L);
        agentRun.Property(r => r.ErrorMessage).HasColumnName("error_message");
        agentRun.HasOne<TurnEntity>()
            .WithMany()
            .HasForeignKey(r => r.AssistantTurnId)
            .OnDelete(DeleteBehavior.SetNull);
        // Composite (org_id, started_at desc) per hub's Phase 3.A ratification
        // — dominant query pattern is "last N runs for tenant X" by recency.
        agentRun.HasIndex(r => new { r.OrgId, r.StartedAt })
            .HasDatabaseName("ix_agent_runs_org_id_started_at");

        // ---- agent_steps (Phase 3.A.1) ----
        // Persists Trellis.Core's AgentStep shape. ON DELETE CASCADE on
        // the parent agent_run — soft-delete + cascade-cleanup is the
        // 55-doc convention; the Core docstring on AgentRun.ArchivedAt
        // notes the parent is authoritative.
        var agentStep = modelBuilder.Entity<AgentStepEntity>();
        agentStep.ToTable("agent_steps");
        agentStep.HasKey(s => s.Id);
        agentStep.Property(s => s.Id).HasColumnName("id");
        agentStep.Property(s => s.AgentRunId).HasColumnName("agent_run_id").IsRequired();
        agentStep.Property(s => s.StepIndex).HasColumnName("step_index").IsRequired();
        agentStep.Property(s => s.ToolName).HasColumnName("tool_name").HasMaxLength(64).IsRequired();
        agentStep.Property(s => s.ToolInputJson).HasColumnName("tool_input_json").IsRequired();
        agentStep.Property(s => s.ToolOutputJson).HasColumnName("tool_output_json");
        agentStep.Property(s => s.Status).HasColumnName("status").HasMaxLength(32).IsRequired();
        agentStep.Property(s => s.StartedAt).HasColumnName("started_at").IsRequired();
        agentStep.Property(s => s.CompletedAt).HasColumnName("completed_at");
        agentStep.Property(s => s.ErrorMessage).HasColumnName("error_message");
        agentStep.Property(s => s.DurationMs).HasColumnName("duration_ms").IsRequired().HasDefaultValue(0L);
        agentStep.HasOne<AgentRunEntity>()
            .WithMany()
            .HasForeignKey(s => s.AgentRunId)
            .OnDelete(DeleteBehavior.Cascade);
        // (agent_run_id, step_index) unique — same pattern as
        // (conversation_id, position) on turns. The executor assigns
        // step_index sequentially under no advisory lock (one writer
        // per agent run, no concurrency at the step layer).
        agentStep.HasIndex(s => new { s.AgentRunId, s.StepIndex })
            .IsUnique()
            .HasDatabaseName("ux_agent_steps_run_step");
    }
}

/// <summary>
/// EF entity for a row in <c>conversations</c>.
/// </summary>
public sealed class ConversationEntity
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Channel { get; set; } = "api";
    public string Model { get; set; } = "mistral-small:24b";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// EF entity for a row in <c>turns</c>. <see cref="Role"/> is the wire-
/// string form ("user"|"assistant"|"system"|future "tool"); the impl
/// converts to/from <see cref="AssistantTurnRole"/> at the boundary.
/// </summary>
public sealed class TurnEntity
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public int Position { get; set; }
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// EF entity for a row in <c>agent_runs</c> (Phase 3.A.1). Mirrors
/// Trellis.Core's <see cref="Trellis.Core.Models.AgentRun"/> wire shape;
/// the store impl maps between this entity + the Core record at the
/// boundary, same pattern as <see cref="ConversationEntity"/> ↔
/// <see cref="AssistantConversation"/>.
///
/// Status is stored as the lowercase wire string from
/// <see cref="Trellis.Core.Models.AgentRunStatus"/> EnumMember values
/// (planning|running|succeeded|failed|cap_reached|loop_detected|cancelled).
/// String-on-disk + map-at-boundary keeps the schema future-proof: a
/// new Phase status enum value lands without an ALTER TYPE migration.
/// </summary>
public sealed class AgentRunEntity
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid? AssistantTurnId { get; set; }
    public string Plan { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? ArchivedAt { get; set; }
    public long TokensUsed { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// EF entity for a row in <c>agent_steps</c> (Phase 3.A.1). Mirrors
/// Trellis.Core's <see cref="Trellis.Core.Models.AgentStep"/>. Status
/// is the lowercase wire string from
/// <see cref="Trellis.Core.Models.AgentStepStatus"/> EnumMember values
/// (pending|succeeded|failed|skipped). DurationMs is denormalized at
/// write time so operators can sort/filter on it without recomputing
/// from StartedAt + CompletedAt.
/// </summary>
public sealed class AgentStepEntity
{
    public Guid Id { get; set; }
    public Guid AgentRunId { get; set; }
    public int StepIndex { get; set; }
    public string ToolName { get; set; } = "";
    public string ToolInputJson { get; set; } = "";
    public string? ToolOutputJson { get; set; }
    public string Status { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public long DurationMs { get; set; }
}

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

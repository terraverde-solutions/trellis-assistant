using Microsoft.EntityFrameworkCore;
using Trellis.Core.Services;

namespace Trellis.Assistant.Data;

/// <summary>
/// Postgres-backed <see cref="IAssistantConversationStore"/>. Single
/// chokepoint for tenant filtering: every operation takes
/// (tenantId, userId) as its first parameters and applies
/// <c>WHERE tenant_id = $1 AND user_id = $2</c> in every query. No
/// other layer in Trellis.Assistant builds raw conversation/turn
/// queries — orchestrator + endpoints go through this interface.
/// Phase 5+ pairs this with Postgres Row-Level Security policies on
/// the same tables for defense-in-depth (filed as v1 work).
///
/// Ulid-vs-Guid: IDs are generated via <see cref="Ulid.NewUlid"/> and
/// converted to <see cref="Guid"/> via <see cref="Ulid.ToGuid"/> for
/// the Postgres <c>uuid</c> column. The byte layout is preserved
/// across the conversion, so the time-sortable property of ulid
/// holds in the DB index — inserts land sequentially on the B-tree
/// page rather than randomly. Trellis.Core stays Ulid-free; the
/// Ulid type is purely an Assistant-internal detail.
///
/// AppendTurnsAsync acquires a transaction-scoped Postgres advisory
/// lock keyed on the conversation id before computing the next
/// position. The lock covers the position read + the INSERT pair —
/// it serializes position assignment and prevents unique-constraint
/// races on (conversation_id, position). It does NOT cover the
/// orchestrator's history read or LLM call (those run BEFORE this
/// method is entered). For Phase 1 that's acceptable: the stub LLM
/// is history-independent and each AppendTurnsAsync writes an atomic
/// user+assistant pair at unique positions. Phase 3+ tool-dispatch
/// MUST re-evaluate this boundary — if multiple LLM/tool round-trips
/// need to observe each other's intermediate turns, the lock window
/// expands to the orchestrator layer (orchestrator opens the
/// transaction, AppendTurnsAsync becomes a persist-only step inside
/// it).
/// </summary>
public sealed class PostgresAssistantConversationStore : IAssistantConversationStore
{
    private readonly AssistantDbContext _db;

    public PostgresAssistantConversationStore(AssistantDbContext db)
    {
        _db = db;
    }

    public async Task<Guid> CreateConversationAsync(
        string tenantId,
        string userId,
        string channel,
        CancellationToken cancellationToken = default)
    {
        ValidateTenantUser(tenantId, userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);

        var now = DateTime.UtcNow;
        var entity = new ConversationEntity
        {
            // Ulid -> Guid preserves the time-sortable byte layout so
            // inserts hit sequential B-tree pages.
            Id = Ulid.NewUlid().ToGuid(),
            TenantId = tenantId,
            UserId = userId,
            Channel = channel,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Conversations.Add(entity);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return entity.Id;
    }

    public async Task<AssistantConversation?> GetConversationAsync(
        string tenantId,
        string userId,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        ValidateTenantUser(tenantId, userId);

        var entity = await _db.Conversations
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.UserId == userId && c.Id == conversationId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return entity is null ? null : ToCore(entity);
    }

    public async Task<IReadOnlyList<AssistantTurn>> GetTurnsAsync(
        string tenantId,
        string userId,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        ValidateTenantUser(tenantId, userId);

        // Tenant-scoped: turns are reachable only via a conversation owned
        // by (tenantId, userId). A direct query on Turns alone would risk
        // a tenant-id-forgotten leak; the join-via-conversation form keeps
        // tenant filtering on the canonical owner row.
        var turns = await _db.Turns
            .AsNoTracking()
            .Join(
                _db.Conversations
                    .Where(c => c.TenantId == tenantId && c.UserId == userId && c.Id == conversationId),
                t => t.ConversationId,
                c => c.Id,
                (t, _) => t)
            .OrderBy(t => t.Position)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return turns.Select(ToCore).ToList();
    }

    public async Task<IReadOnlyList<AssistantTurn>> AppendTurnsAsync(
        string tenantId,
        string userId,
        Guid conversationId,
        IReadOnlyList<NewAssistantTurn> turns,
        CancellationToken cancellationToken = default)
    {
        ValidateTenantUser(tenantId, userId);
        ArgumentNullException.ThrowIfNull(turns);
        if (turns.Count == 0)
        {
            return Array.Empty<AssistantTurn>();
        }

        // The advisory lock + the position computation MUST happen in the
        // same transaction. pg_advisory_xact_lock is transaction-scoped:
        // released automatically on commit OR rollback, no leak risk.
        // Without the transaction wrapper, the lock would acquire-and-
        // release per statement and concurrent appends could see the same
        // "current max position" + race the unique constraint.
        await using var tx = await _db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // Re-verify ownership inside the transaction. A concurrent DELETE
        // (Phase 4+) could have removed the conversation between the
        // caller's last check and now; we want the failure to land here
        // as a clean "conversation not found" rather than a downstream
        // FK violation.
        var ownerCheck = await _db.Conversations
            .AsNoTracking()
            .AnyAsync(
                c => c.TenantId == tenantId
                    && c.UserId == userId
                    && c.Id == conversationId,
                cancellationToken)
            .ConfigureAwait(false);

        if (!ownerCheck)
        {
            // Roll back implicit on dispose; throw something the endpoint
            // layer can map to 404.
            throw new InvalidOperationException(
                $"Conversation {conversationId} not found for tenant/user.");
        }

        // Acquire the per-conversation advisory lock. hashtextextended +
        // the GUID-as-text form gives a stable 64-bit key that's the
        // same across reconnections — no risk of two callers hashing
        // to different keys. The interpolation hole binds as a
        // parameter; the trailing ::text cast disambiguates the
        // hashtextextended(text, bigint) overload so Postgres doesn't
        // have to resolve overloads from a CLR-string parameter type.
        var lockKey = conversationId.ToString();
        await _db.Database
            .ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}::text, 0))",
                cancellationToken)
            .ConfigureAwait(false);

        // Compute the next position under the lock. The bare WHERE on
        // conversationId is sufficient because ownerCheck above already
        // confirmed this conversationId belongs to (tenantId, userId)
        // — and we hold the advisory lock so no concurrent writer can
        // change ownership underneath us.
        //
        // MAX returns null on an empty turns set (first append). Read
        // it as int? then explicit (-1) sentinel + 1 gives 0 for the
        // empty case and (max + 1) otherwise — unambiguous in either
        // direction without leaning on null + 1 = null nullable arithmetic.
        var maxPosition = await _db.Turns
            .Where(t => t.ConversationId == conversationId)
            .Select(t => (int?)t.Position)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false);
        var nextPosition = (maxPosition ?? -1) + 1;

        var now = DateTime.UtcNow;
        var entities = new List<TurnEntity>(turns.Count);
        for (var i = 0; i < turns.Count; i++)
        {
            var nt = turns[i];
            ArgumentNullException.ThrowIfNull(nt);
            ArgumentException.ThrowIfNullOrEmpty(nt.Content);
            entities.Add(new TurnEntity
            {
                Id = Ulid.NewUlid().ToGuid(),
                ConversationId = conversationId,
                Position = nextPosition + i,
                Role = ToRoleString(nt.Role),
                Content = nt.Content,
                CreatedAt = now,
            });
        }
        _db.Turns.AddRange(entities);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Bump the parent conversation's UpdatedAt so a future "list
        // conversations newest-first" query has a single sortable column.
        // ExecuteUpdateAsync issues a single UPDATE without loading the
        // entity into the change tracker — under Phase 3+ load this
        // matters; in Phase 1 the win is just keeping the tenant filter
        // unmissable on the WHERE clause. The (tenantId, userId) filter
        // mirrors the chokepoint contract spelled out in this class's
        // XML doc — every query in this impl carries it.
        await _db.Conversations
            .Where(c => c.Id == conversationId
                     && c.TenantId == tenantId
                     && c.UserId == userId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(c => c.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        return entities.Select(ToCore).ToList();
    }

    private static void ValidateTenantUser(string tenantId, string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
    }

    // ---- entity <-> Core POCO mappers ----

    private static AssistantConversation ToCore(ConversationEntity e) => new(
        Id: e.Id,
        TenantId: e.TenantId,
        UserId: e.UserId,
        Channel: e.Channel,
        CreatedAt: e.CreatedAt,
        UpdatedAt: e.UpdatedAt);

    private static AssistantTurn ToCore(TurnEntity e) => new(
        Id: e.Id,
        ConversationId: e.ConversationId,
        Position: e.Position,
        Role: ToRoleEnum(e.Role),
        Content: e.Content,
        CreatedAt: e.CreatedAt);

    // ---- role string <-> enum ----
    //
    // The DB stores lowercase wire strings ("user", "assistant", "system").
    // Phase 3 adds "tool" without a schema change — new enum value lands
    // alongside in Trellis.Core, the parser picks it up automatically.
    // Unknown strings throw at the boundary so a typo'd row doesn't
    // silently render as the default enum value (which would be User and
    // very wrong from an audit perspective).
    private static string ToRoleString(AssistantTurnRole role) => role switch
    {
        AssistantTurnRole.User => "user",
        AssistantTurnRole.Assistant => "assistant",
        AssistantTurnRole.System => "system",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown role"),
    };

    private static AssistantTurnRole ToRoleEnum(string role) => role switch
    {
        "user" => AssistantTurnRole.User,
        "assistant" => AssistantTurnRole.Assistant,
        "system" => AssistantTurnRole.System,
        _ => throw new InvalidOperationException(
            $"Unrecognized role string '{role}' in turns table — schema migration may be needed."),
    };
}

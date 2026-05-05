using Microsoft.EntityFrameworkCore;
using Trellis.Core.Models;

namespace Trellis.Assistant.Data;

/// <summary>
/// Postgres-backed <see cref="IAgentRunStore"/>. Single chokepoint for
/// <c>org_id</c>-scoped tenant filtering: every operation takes
/// <c>orgId</c> as its first parameter and applies
/// <c>WHERE org_id = $1</c> in every query. No other layer in
/// Trellis.Assistant builds raw agent_run/agent_step queries — the
/// executor + endpoints go through this interface.
///
/// <para>
/// Same impl pattern as
/// <see cref="PostgresAssistantConversationStore"/>: entity ↔ Core POCO
/// mappers at the boundary, status string ↔ enum at the boundary,
/// Ulid → Guid for time-sortable byte layout on uuid columns.
/// </para>
///
/// <para>
/// No advisory lock here: agent_run dispatch is single-writer per run
/// (one executor instance owns one run end-to-end). The
/// <c>(agent_run_id, step_index)</c> unique constraint catches a
/// programming bug if a future executor accidentally double-dispatches
/// at the same index, but the lock-free path is fine for v0.
/// </para>
/// </summary>
public sealed class PostgresAgentRunStore : IAgentRunStore
{
    private readonly AssistantDbContext _db;

    public PostgresAgentRunStore(AssistantDbContext db)
    {
        _db = db;
    }

    public async Task<AgentRun> CreateRunAsync(
        AgentRun run,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.OrgId == Guid.Empty)
        {
            throw new ArgumentException(
                "AgentRun.OrgId must be non-empty (matches AgentRunRequest.Create's contract).",
                nameof(run));
        }

        var entity = new AgentRunEntity
        {
            Id = run.Id == Guid.Empty ? Ulid.NewUlid().ToGuid() : run.Id,
            OrgId = run.OrgId,
            AssistantTurnId = run.AssistantTurnId,
            Plan = run.Plan,
            Status = ToStatusString(run.Status),
            StartedAt = run.StartedAt,
            CompletedAt = run.CompletedAt,
            ArchivedAt = run.ArchivedAt,
            TokensUsed = run.TokensUsed,
            ErrorMessage = null,
        };
        _db.AgentRuns.Add(entity);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToCore(entity, steps: Array.Empty<AgentStep>());
    }

    public async Task<AgentRun?> GetRunAsync(
        Guid orgId,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        ValidateOrgId(orgId);

        var entity = await _db.AgentRuns
            .AsNoTracking()
            .Where(r => r.OrgId == orgId && r.Id == runId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return null;
        }

        var steps = await GetStepsInternalAsync(orgId, runId, cancellationToken).ConfigureAwait(false);
        return ToCore(entity, steps);
    }

    public async Task<AgentRun> CompleteRunAsync(
        Guid orgId,
        Guid runId,
        AgentRunStatus status,
        DateTime completedAt,
        long tokensUsed,
        string? errorMessage,
        CancellationToken cancellationToken = default)
    {
        ValidateOrgId(orgId);

        // ExecuteUpdateAsync issues a single UPDATE without loading the
        // entity into the change tracker. Tenant filter is unmissable on
        // the WHERE clause — same pattern as the UpdatedAt bump in
        // PostgresAssistantConversationStore.AppendTurnsAsync.
        var statusString = ToStatusString(status);
        var rowsAffected = await _db.AgentRuns
            .Where(r => r.OrgId == orgId && r.Id == runId)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(r => r.Status, statusString)
                    .SetProperty(r => r.CompletedAt, (DateTime?)completedAt)
                    .SetProperty(r => r.TokensUsed, tokensUsed)
                    .SetProperty(r => r.ErrorMessage, errorMessage),
                cancellationToken)
            .ConfigureAwait(false);
        if (rowsAffected == 0)
        {
            throw new InvalidOperationException(
                $"AgentRun {runId} not found for org {orgId}.");
        }

        var updated = await GetRunAsync(orgId, runId, cancellationToken).ConfigureAwait(false);
        return updated ?? throw new InvalidOperationException(
            $"AgentRun {runId} disappeared between UPDATE and re-read.");
    }

    public async Task<AgentStep> AppendStepAsync(
        Guid orgId,
        AgentStep step,
        CancellationToken cancellationToken = default)
    {
        ValidateOrgId(orgId);
        ArgumentNullException.ThrowIfNull(step);

        // Cross-check: the step's parent agent_run is owned by orgId.
        // Without this, a malicious caller could construct a step whose
        // AgentRunId points at a different tenant's run and the
        // (agent_run_id, step_index) unique constraint would only fire
        // on actual collision — letting the row land otherwise.
        var ownerCheck = await _db.AgentRuns
            .AsNoTracking()
            .AnyAsync(r => r.OrgId == orgId && r.Id == step.AgentRunId, cancellationToken)
            .ConfigureAwait(false);
        if (!ownerCheck)
        {
            throw new InvalidOperationException(
                $"AgentRun {step.AgentRunId} not found for org {orgId} — cannot append step.");
        }

        var entity = new AgentStepEntity
        {
            Id = step.Id == Guid.Empty ? Ulid.NewUlid().ToGuid() : step.Id,
            AgentRunId = step.AgentRunId,
            StepIndex = step.StepIndex,
            ToolName = step.ToolName,
            ToolInputJson = step.ToolInputJson,
            ToolOutputJson = step.ToolOutputJson,
            Status = ToStepStatusString(step.Status),
            StartedAt = step.StartedAt,
            CompletedAt = step.CompletedAt,
            ErrorMessage = step.ErrorMessage,
            DurationMs = step.DurationMs,
        };
        _db.AgentSteps.Add(entity);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToCoreStep(entity);
    }

    public async Task<IReadOnlyList<AgentStep>> GetStepsAsync(
        Guid orgId,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        ValidateOrgId(orgId);
        return await GetStepsInternalAsync(orgId, runId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<AgentStep>> GetStepsInternalAsync(
        Guid orgId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        // Tenant-scoped via the parent run's org_id — analogous to
        // PostgresAssistantConversationStore.GetTurnsAsync's
        // join-via-conversation pattern. Direct query on AgentSteps
        // alone would risk a tenant-id-forgotten leak.
        var entities = await _db.AgentSteps
            .AsNoTracking()
            .Join(
                _db.AgentRuns
                    .Where(r => r.OrgId == orgId && r.Id == runId),
                s => s.AgentRunId,
                r => r.Id,
                (s, _) => s)
            .OrderBy(s => s.StepIndex)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return entities.Select(ToCoreStep).ToList();
    }

    private static void ValidateOrgId(Guid orgId)
    {
        if (orgId == Guid.Empty)
        {
            throw new ArgumentException(
                "OrgId must be non-empty (matches AgentRunRequest.Create's contract).",
                nameof(orgId));
        }
    }

    // ---- entity <-> Core POCO mappers ----

    private static AgentRun ToCore(AgentRunEntity e, IReadOnlyList<AgentStep> steps) => new()
    {
        Id = e.Id,
        OrgId = e.OrgId,
        AssistantTurnId = e.AssistantTurnId,
        Plan = e.Plan,
        Status = ToStatusEnum(e.Status),
        StartedAt = e.StartedAt,
        CompletedAt = e.CompletedAt,
        ArchivedAt = e.ArchivedAt,
        Steps = steps,
        TokensUsed = checked((int)Math.Min(e.TokensUsed, int.MaxValue)),
    };

    private static AgentStep ToCoreStep(AgentStepEntity e) => new()
    {
        Id = e.Id,
        AgentRunId = e.AgentRunId,
        StepIndex = e.StepIndex,
        ToolName = e.ToolName,
        ToolInputJson = e.ToolInputJson,
        ToolOutputJson = e.ToolOutputJson,
        Status = ToStepStatusEnum(e.Status),
        StartedAt = e.StartedAt,
        CompletedAt = e.CompletedAt,
        ErrorMessage = e.ErrorMessage,
        DurationMs = e.DurationMs,
    };

    // ---- status string <-> enum ----
    //
    // Wire strings match the Core records' EnumMember values:
    //   AgentRunStatus  → planning | running | succeeded | failed | cap_reached | loop_detected | cancelled
    //   AgentStepStatus → pending | succeeded | failed | skipped
    // Unknown strings throw at the boundary so a typo'd row doesn't
    // silently render as the default enum value.

    private static string ToStatusString(AgentRunStatus status) => status switch
    {
        AgentRunStatus.Planning => "planning",
        AgentRunStatus.Running => "running",
        AgentRunStatus.Succeeded => "succeeded",
        AgentRunStatus.Failed => "failed",
        AgentRunStatus.CapReached => "cap_reached",
        AgentRunStatus.LoopDetected => "loop_detected",
        AgentRunStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown AgentRunStatus"),
    };

    private static AgentRunStatus ToStatusEnum(string s) => s switch
    {
        "planning" => AgentRunStatus.Planning,
        "running" => AgentRunStatus.Running,
        "succeeded" => AgentRunStatus.Succeeded,
        "failed" => AgentRunStatus.Failed,
        "cap_reached" => AgentRunStatus.CapReached,
        "loop_detected" => AgentRunStatus.LoopDetected,
        "cancelled" => AgentRunStatus.Cancelled,
        _ => throw new InvalidOperationException(
            $"Unrecognized AgentRunStatus wire string '{s}' — schema migration may be needed."),
    };

    private static string ToStepStatusString(AgentStepStatus status) => status switch
    {
        AgentStepStatus.Pending => "pending",
        AgentStepStatus.Succeeded => "succeeded",
        AgentStepStatus.Failed => "failed",
        AgentStepStatus.Skipped => "skipped",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown AgentStepStatus"),
    };

    private static AgentStepStatus ToStepStatusEnum(string s) => s switch
    {
        "pending" => AgentStepStatus.Pending,
        "succeeded" => AgentStepStatus.Succeeded,
        "failed" => AgentStepStatus.Failed,
        "skipped" => AgentStepStatus.Skipped,
        _ => throw new InvalidOperationException(
            $"Unrecognized AgentStepStatus wire string '{s}' — schema migration may be needed."),
    };
}

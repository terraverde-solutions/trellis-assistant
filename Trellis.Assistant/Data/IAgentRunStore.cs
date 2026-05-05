using Trellis.Core.Models;

namespace Trellis.Assistant.Data;

/// <summary>
/// Persistence surface for <see cref="AgentRun"/> + <see cref="AgentStep"/>
/// records. Single tenant-filtering chokepoint for the agent execution
/// surface — every operation takes <c>orgId</c> as its first parameter
/// and applies <c>WHERE org_id = $1</c> in every query. No other layer
/// in Trellis.Assistant builds raw agent_run/agent_step queries — the
/// executor + endpoints go through this interface.
///
/// <para>
/// Distinct from <see cref="IAssistantConversationStore"/>: that surface
/// scopes by <c>(TenantId, UserId)</c> per Phase 1's per-user
/// conversation ownership. The agent-execution surface scopes by
/// <c>OrgId</c> (Trellis.Core's <see cref="AgentRun.OrgId"/> contract;
/// Phase 0 PR #10). Phase 3.A's orchestrator derives OrgId from
/// <c>Guid.Parse(tenantId)</c> per ratified Phase 3.A C1 — production
/// JWT tenant claims are uuid-shaped strings.
/// </para>
///
/// <para>
/// Phase 3.A.1 ships only this surface. Phase 3.A.2 wires the executor
/// into the conversation flow (tool-turn read/write); that requires a
/// sibling Trellis.Core PR for the
/// <see cref="IAssistantConversationStore"/> surface widening, which is
/// out of scope here.
/// </para>
/// </summary>
public interface IAgentRunStore
{
    /// <summary>
    /// Insert a new <see cref="AgentRun"/> row. Implementations validate
    /// <c>OrgId</c> non-empty via the Trellis.Core record's
    /// <c>AgentRunRequest.Create</c> contract; this method trusts the
    /// caller has done that validation upstream.
    /// </summary>
    Task<AgentRun> CreateRunAsync(
        AgentRun run,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Read an agent run scoped by <paramref name="orgId"/>. Returns
    /// <c>null</c> when the run doesn't exist OR is owned by a different
    /// org — caller cannot distinguish (intentional; cross-tenant
    /// existence-probing is the leak this prevents).
    /// </summary>
    Task<AgentRun?> GetRunAsync(
        Guid orgId,
        Guid runId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Update the run's terminal state — <see cref="AgentRun.Status"/>,
    /// <see cref="AgentRun.CompletedAt"/>, <see cref="AgentRun.TokensUsed"/>,
    /// and the optional error message. The implementation re-checks
    /// org ownership inside the UPDATE so a forgotten scope can't bleed
    /// across tenants. Returns the updated run.
    /// </summary>
    Task<AgentRun> CompleteRunAsync(
        Guid orgId,
        Guid runId,
        AgentRunStatus status,
        DateTime completedAt,
        long tokensUsed,
        string? errorMessage,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Append one <see cref="AgentStep"/> to the run. Step index is
    /// caller-supplied (the executor assigns 0-based sequentially); the
    /// implementation enforces <c>(agent_run_id, step_index)</c>
    /// uniqueness via the DB constraint, so a repeat assignment fails
    /// loud at insert time.
    /// </summary>
    Task<AgentStep> AppendStepAsync(
        Guid orgId,
        AgentStep step,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Read all steps for a run in <see cref="AgentStep.StepIndex"/>
    /// ascending order. Returns an empty list when the run has no
    /// steps yet, or when the run doesn't exist / isn't owned by
    /// <paramref name="orgId"/>. Callers wanting to distinguish those
    /// pre-check via <see cref="GetRunAsync"/>.
    /// </summary>
    Task<IReadOnlyList<AgentStep>> GetStepsAsync(
        Guid orgId,
        Guid runId,
        CancellationToken cancellationToken = default);
}

namespace Trellis.Assistant.Services;

/// <summary>
/// Phase 3.I: narrow client for trellis-workflow's <c>POST /api/workflows/runs</c>
/// schedule-by-id branch. One method by design — the inline-JSON
/// branch (which would take a full <c>WorkflowDefinitionJson</c> blob)
/// is intentionally NOT exposed at the Assistant layer, because an LLM
/// has no business emitting that much surface (hallucination + injection
/// risk per the Phase 3.I brief).
///
/// <para>
/// Used by <see cref="AgentExecution.WorkflowScheduleTool"/> to dispatch
/// LLM-emitted schedule calls. Failure mapping shape mirrors
/// <see cref="ISearchClient"/> — structured discriminated outcome
/// instead of throwing, so the tool can surface a LLM-readable
/// envelope without breaking the agent loop on every failure.
/// </para>
/// </summary>
public interface IWorkflowClient
{
    Task<WorkflowScheduleResult> ScheduleByIdAsync(
        Guid workflowDefinitionId,
        string? initialInputJson,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Discriminated result for <see cref="IWorkflowClient.ScheduleByIdAsync"/>.
/// On <see cref="Success"/>=<c>true</c>, <see cref="ResponseBodyJson"/>
/// carries qwen's 201 Created body verbatim (the tool returns this to
/// the LLM as <see cref="Core.Models.AgentToolOutput.ResultJson"/>).
/// On failure, <see cref="ErrorCode"/> classifies the failure into one
/// of the four LLM-readable shapes; <see cref="ErrorMessage"/> carries a
/// short human-readable description.
/// </summary>
public sealed record WorkflowScheduleResult
{
    public required bool Success { get; init; }
    public string? ResponseBodyJson { get; init; }
    public WorkflowScheduleErrorCode? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Phase 3.I failure-mapping enum. Each value maps to a distinct
/// LLM-readable error envelope shape so the LLM can reason about retry
/// vs surface-to-user vs ask-for-different-id.
/// </summary>
public enum WorkflowScheduleErrorCode
{
    /// <summary>
    /// qwen returned 401 Unauthorized. Operator-actionable (the
    /// Authorization header was rejected); the LLM should NOT retry —
    /// retrying won't fix a credential problem.
    /// </summary>
    AuthFailed,

    /// <summary>
    /// qwen returned 404 Not Found. The workflow definition id either
    /// doesn't exist or isn't visible to the calling tenant (qwen's
    /// per-tenant filter collapses the two cases to avoid cross-tenant
    /// existence-leak). The LLM can ask the user for a different id.
    /// </summary>
    DefinitionNotFound,

    /// <summary>
    /// qwen returned a 4xx other than 401/404. Surface the
    /// ProblemDetails detail verbatim — the LLM can read it + decide
    /// whether to retry with corrected args.
    /// </summary>
    BadRequest,

    /// <summary>
    /// qwen unreachable / 5xx / transport / timeout. The LLM can
    /// retry-with-backoff or surface to the user.
    /// </summary>
    Transient,
}

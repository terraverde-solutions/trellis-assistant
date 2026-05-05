using Trellis.Assistant.AgentExecution;
using Trellis.Assistant.Middleware;
using Trellis.Core.Models;
using Trellis.Core.Services;

namespace Trellis.Assistant.Endpoints;

/// <summary>
/// HTTP endpoints for Phase 3.A.1's agentic surface. One operation:
/// <c>POST /api/agent-runs</c> — kick off a one-shot agentic run with a
/// caller-supplied prompt + (implicit) tool catalogue (from the
/// registered <see cref="IToolRegistry"/>). Returns the terminal
/// <see cref="AgentRun"/> with its full step history.
///
/// <para>
/// Phase 4 channel adapters use this endpoint directly for
/// "agentic-without-conversation-history" flows (e.g. a Slack slash
/// command <c>/ask &lt;prompt&gt;</c>). Phase 3.A.2 wires the executor
/// into the conversation flow at <c>POST /api/conversations/{id}/turns</c>;
/// this endpoint is the standalone surface that exists independently.
/// </para>
///
/// <para>
/// OrgId derivation per Phase 3.A C1: <see cref="Guid"/>.Parse the
/// trusted tenant id from <see cref="TenantHeadersMiddleware"/>. JWT
/// production tenants are uuid-shaped; non-uuid tenant strings here
/// fail loud at request time with 400 Bad Request rather than silently
/// mis-mapping to a random Guid.
/// </para>
///
/// <para>
/// Per-request timeout: same wall-clock budget as Phase 2's POST /turns
/// — bound from <c>Assistant:TurnRequestTimeoutSeconds</c>. The
/// AssistantBudgetGate's MaxRunDuration (10 min default) is the
/// run-level cap; the endpoint-level CT cancel from this timeout is
/// the additional outer wrapper that surfaces "client gave up" cleanly.
/// </para>
/// </summary>
public static class AgentRunEndpoints
{
    public static void MapAgentRunEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/agent-runs");
        group.MapPost("/", CreateAgentRunAsync);
    }

    public sealed record CreateAgentRunRequest(
        string UserPrompt,
        IReadOnlyList<string>? ToolNames,
        int? MaxSteps);

    public sealed record CreateAgentRunResponse(
        string Id,
        string Status,
        string Plan,
        DateTime StartedAt,
        DateTime? CompletedAt,
        long TokensUsed,
        string? ErrorMessage,
        IReadOnlyList<AgentStepDto> Steps);

    public sealed record AgentStepDto(
        string Id,
        int StepIndex,
        string ToolName,
        string ToolInputJson,
        string? ToolOutputJson,
        string Status,
        DateTime StartedAt,
        DateTime? CompletedAt,
        string? ErrorMessage,
        long DurationMs);

    private static async Task<IResult> CreateAgentRunAsync(
        CreateAgentRunRequest? request,
        HttpContext context,
        IAgentExecutor executor,
        IToolRegistry tools,
        CancellationToken cancellationToken)
    {
        var tenantIdString = (string)context.Items[TenantHeadersMiddleware.TenantIdKey]!;
        // userId is read for parity with the conversation surface but
        // currently unused — agent_runs scope by OrgId only. Phase 5+
        // identity work may add per-user agent permissions.
        _ = (string)context.Items[TenantHeadersMiddleware.UserIdKey]!;

        if (request is null || string.IsNullOrWhiteSpace(request.UserPrompt))
        {
            return Results.BadRequest(new { error = "userPrompt is required" });
        }

        if (!Guid.TryParse(tenantIdString, out var orgId) || orgId == Guid.Empty)
        {
            // Phase 3.A C1 contract: production tenantIds are uuid-shaped.
            // A non-uuid tenant slipping through TenantHeadersMiddleware
            // (which only checks non-blank) means a misconfigured client
            // or a pre-Phase-3 test fixture. 400 surfaces it loud rather
            // than silently mis-scoping the run.
            return Results.BadRequest(new
            {
                error = "tenant_id is not a valid uuid; agent runs require uuid-shaped tenant identifiers per Phase 3.A C1 contract",
            });
        }

        // Build the tool catalogue. If the caller supplied an explicit
        // ToolNames filter, intersect with the registered tools; else
        // pass all registered tools to the planner.
        var availableTools = request.ToolNames is { Count: > 0 }
            ? tools.Descriptors.Where(d => request.ToolNames.Contains(d.Name)).ToList()
            : tools.Descriptors.ToList();

        AgentRunRequest runRequest;
        try
        {
            runRequest = AgentRunRequest.Create(
                orgId: orgId,
                userPrompt: request.UserPrompt,
                availableTools: availableTools,
                assistantTurnId: null,
                budgetOverrides: request.MaxSteps is null
                    ? null
                    : new AgentBudgetOverrides { MaxSteps = request.MaxSteps });
        }
        catch (ArgumentException ex)
        {
            // Core's AgentRunRequest.Create throws on Guid.Empty OrgId;
            // map cleanly to 400.
            return Results.BadRequest(new { error = ex.Message });
        }

        var run = await executor.RunAsync(runRequest, cancellationToken).ConfigureAwait(false);

        return Results.Ok(new CreateAgentRunResponse(
            Id: GuidToUlidString(run.Id),
            Status: AgentRunStatusWire(run.Status),
            Plan: run.Plan,
            StartedAt: run.StartedAt,
            CompletedAt: run.CompletedAt,
            TokensUsed: run.TokensUsed,
            ErrorMessage: null,
            Steps: run.Steps.Select(ToStepDto).ToList()));
    }

    private static string GuidToUlidString(Guid g) => new Ulid(g).ToString();

    private static AgentStepDto ToStepDto(AgentStep s) => new(
        Id: GuidToUlidString(s.Id),
        StepIndex: s.StepIndex,
        ToolName: s.ToolName,
        ToolInputJson: s.ToolInputJson,
        ToolOutputJson: s.ToolOutputJson,
        Status: s.Status.ToString().ToLowerInvariant(),
        StartedAt: s.StartedAt,
        CompletedAt: s.CompletedAt,
        ErrorMessage: s.ErrorMessage,
        DurationMs: s.DurationMs);

    private static string AgentRunStatusWire(AgentRunStatus status) => status switch
    {
        AgentRunStatus.Planning => "planning",
        AgentRunStatus.Running => "running",
        AgentRunStatus.Succeeded => "succeeded",
        AgentRunStatus.Failed => "failed",
        AgentRunStatus.CapReached => "cap_reached",
        AgentRunStatus.LoopDetected => "loop_detected",
        AgentRunStatus.Cancelled => "cancelled",
        _ => "unknown",
    };
}

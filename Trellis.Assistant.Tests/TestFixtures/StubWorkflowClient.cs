using Trellis.Assistant.Services;

namespace Trellis.Assistant.Tests.TestFixtures;

/// <summary>
/// Phase 3.I fix-up test stub for <see cref="IWorkflowClient"/>. Used by
/// <see cref="AssistantWebApplicationFactory"/> to keep
/// WorkflowScheduleTool dispatches in-process — no outbound HTTP to a
/// real workflow service, no JWT propagation concerns. Tests set
/// <see cref="NextResult"/> before driving an endpoint; the executor
/// dispatches the tool, the tool calls <see cref="ScheduleByIdAsync"/>,
/// this stub returns the canned result.
/// </summary>
public sealed class StubWorkflowClient : IWorkflowClient
{
    public int CallCount { get; private set; }
    public Guid LastDefinitionId { get; private set; }
    public string? LastInitialInputJson { get; private set; }

    /// <summary>
    /// Canned response for the next <see cref="ScheduleByIdAsync"/>
    /// call. Default: success with a deterministic run id so tests can
    /// assert on the LLM-visible body without per-test wiring.
    /// </summary>
    public WorkflowScheduleResult NextResult { get; set; } = new()
    {
        Success = true,
        ResponseBodyJson = """{"id":"55555555-5555-5555-5555-555555555555","status":1}""",
    };

    public Task<WorkflowScheduleResult> ScheduleByIdAsync(
        Guid workflowDefinitionId,
        string? initialInputJson,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastDefinitionId = workflowDefinitionId;
        LastInitialInputJson = initialInputJson;
        return Task.FromResult(NextResult);
    }
}

/// <summary>
/// Phase 3.I fix-up test stub for <see cref="IInternalTokenIssuer"/>.
/// Returns a fixed token so the integration tests don't need a real
/// Auth service running. <see cref="HttpWorkflowClient"/> is replaced
/// in test with the StubWorkflowClient above, so the token never
/// actually flows to a network call.
/// </summary>
public sealed class StubInternalTokenIssuer : IInternalTokenIssuer
{
    private readonly string _token;
    public StubInternalTokenIssuer(string token = "stub-test-token") { _token = token; }
    public Task<string> GetAccessTokenAsync(string targetResource, CancellationToken ct)
        => Task.FromResult(_token);
}

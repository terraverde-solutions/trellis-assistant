using Trellis.Core.Services;

namespace Trellis.Assistant.AgentExecution;

/// <summary>
/// Phase 3.F: gates which registered tools are visible to a given
/// tenant/user/role on the conversation-integrated agent path. Returns
/// <c>true</c> to expose the tool in the LLM-visible catalogue,
/// <c>false</c> to hide it.
///
/// <para>
/// Filter is applied at the <see cref="IToolRegistry"/> boundary via
/// <see cref="IToolRegistry.GetExposedDescriptorsFor"/>. The conversation
/// orchestrator calls into the registry; the standalone
/// <c>POST /api/agent-runs</c> path bypasses tenancy and uses the
/// untenanted <see cref="IToolRegistry.Descriptors"/> property — operator
/// runs see the full catalog regardless of policy.
/// </para>
///
/// <para>
/// Dispatch (<see cref="IToolRegistry.GetTool"/>) is INTENTIONALLY not
/// tenancy-gated. v0 relies on catalogue-only gating: an LLM that never
/// sees a tool can't emit a tool_call for it. Defense-in-depth dispatch-
/// time checking is a future Phase 3.G+ hardening if a real bypass
/// surfaces (e.g., post-jailbreak attempts to name an ungated tool).
/// </para>
///
/// <para>
/// Default behavior preserved per Phase 3.F pin #1: tools with no
/// matching <c>PerTool</c> rule are exposed to all tenants. EchoTool
/// keeps its Phase 3.C <c>ExposeEcho</c> boolean as a special case.
/// </para>
/// </summary>
public interface IToolExposurePolicy
{
    /// <summary>
    /// Returns <c>true</c> when the supplied tool should appear in
    /// <paramref name="tenancy"/>'s LLM-visible catalogue. Should not
    /// throw — gating decisions are normal operation, not exceptional;
    /// log denials at <see cref="LogLevel.Debug"/> per Phase 3.F pin #7.
    /// </summary>
    bool IsExposedTo(IAgentTool tool, AgentToolTenancy tenancy);
}

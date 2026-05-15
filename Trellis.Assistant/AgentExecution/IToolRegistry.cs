using Trellis.Core.Models;
using Trellis.Core.Services;

namespace Trellis.Assistant.AgentExecution;

/// <summary>
/// Lookup surface over all registered <see cref="IAgentTool"/> instances.
/// Populated at host startup from DI; the executor resolves tool calls
/// from the model against this dictionary.
///
/// <para>
/// Validation is performed at startup (see <see cref="ToolRegistry"/>'s
/// constructor):
/// <list type="bullet">
/// <item>Name uniqueness across all registered tools</item>
/// <item>Non-empty <see cref="AgentToolDescriptor.Name"/> + <see cref="AgentToolDescriptor.Description"/></item>
/// <item><see cref="IAgentTool"/> registration key matches its
/// <see cref="IAgentTool.Descriptor"/>.<see cref="AgentToolDescriptor.Name"/></item>
/// </list>
/// </para>
///
/// <para>
/// JSON Schema validation of <see cref="AgentToolDescriptor.ParameterSchema"/>
/// turned on in Phase 3.B (alongside <c>SearchDocumentsTool</c>): the
/// registry's ctor calls
/// <see cref="IJsonSchemaValidator.EnsureValidSchema"/> per tool,
/// throwing on malformed schema strings so the host crashes at startup
/// rather than letting a misconfigured tool surface as a 500 on first
/// dispatch.
/// </para>
/// </summary>
public interface IToolRegistry
{
    /// <summary>
    /// Untenanted exposed-tool descriptor list. Phase 3.C filtering by
    /// <see cref="ToolCatalogueOptions.ExposeEcho"/> only — does NOT
    /// apply the Phase 3.F per-tenant <see cref="ToolCatalogueOptions.PerTool"/>
    /// matrix. Used by the standalone <c>POST /api/agent-runs</c> path
    /// (operator-facing; no end-user tenancy in scope) per Phase 3.F
    /// pin #6.
    /// </summary>
    IReadOnlyList<AgentToolDescriptor> Descriptors { get; }

    /// <summary>
    /// Phase 3.F: per-tenant exposed-tool descriptor list. Applies the
    /// full <see cref="IToolExposurePolicy"/>:
    /// <list type="bullet">
    /// <item>EchoTool gated by <see cref="ToolCatalogueOptions.ExposeEcho"/>
    /// (Phase 3.C special case).</item>
    /// <item>Other tools gated by the per-tool rule in
    /// <see cref="ToolCatalogueOptions.PerTool"/>: AllowedTenants
    /// allowlist + RequiredRole gate.</item>
    /// <item>Default exposed when no rule matches (Phase 3.F pin #1
    /// back-compat).</item>
    /// </list>
    /// Used by the conversation-integrated agent path
    /// (<c>POST /api/conversations/{id}/turns</c>) where
    /// <see cref="AgentToolTenancy"/> is available from the request
    /// context.
    /// </summary>
    IReadOnlyList<AgentToolDescriptor> GetExposedDescriptorsFor(AgentToolTenancy tenancy);

    /// <summary>
    /// Resolve a tool by name. Returns <c>null</c> when the model emits
    /// a tool_call for a name that isn't registered — the executor
    /// converts that to an <see cref="AgentStepStatus.Failed"/> step
    /// with a "tool not registered" error message.
    ///
    /// <para>
    /// Phase 3.F note: <see cref="GetTool"/> is INTENTIONALLY not
    /// tenancy-gated. v0 relies on catalogue-only gating —
    /// <see cref="GetExposedDescriptorsFor"/> filters what the LLM sees;
    /// the LLM can't emit a tool_call for what it doesn't see. A
    /// dispatch-time tenancy check would be defense-in-depth against
    /// post-jailbreak attempts and is a future 3.G+ hardening if a
    /// real bypass surfaces.
    /// </para>
    /// </summary>
    IAgentTool? GetTool(string name);
}

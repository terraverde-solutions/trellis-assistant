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
    /// All registered tool descriptors. Surfaced to the planner LLM as
    /// the <c>AgentRunRequest.AvailableTools</c> input.
    /// </summary>
    IReadOnlyList<AgentToolDescriptor> Descriptors { get; }

    /// <summary>
    /// Resolve a tool by name. Returns <c>null</c> when the model emits
    /// a tool_call for a name that isn't registered — the executor
    /// converts that to an <see cref="AgentStepStatus.Failed"/> step
    /// with a "tool not registered" error message.
    /// </summary>
    IAgentTool? GetTool(string name);
}

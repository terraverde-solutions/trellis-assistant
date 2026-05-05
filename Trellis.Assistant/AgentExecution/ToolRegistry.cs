using Trellis.Core.Models;
using Trellis.Core.Services;

namespace Trellis.Assistant.AgentExecution;

/// <summary>
/// Default <see cref="IToolRegistry"/> implementation. Eagerly validates
/// all registered <see cref="IAgentTool"/> instances at construction
/// (i.e. at first DI resolution at host startup). Validation failures
/// throw — the host crashes before accepting traffic, so a
/// misconfigured tool registration can't lurk until a real user
/// request triggers a NullReferenceException.
///
/// <para>
/// Validation rules (Phase 3.A.1, trimmed per ratification):
/// <list type="bullet">
/// <item>Each tool's <see cref="IAgentTool.Descriptor"/>.<see cref="AgentToolDescriptor.Name"/>
/// is non-empty + non-whitespace.</item>
/// <item>Each tool's <see cref="AgentToolDescriptor.Description"/>
/// is non-empty + non-whitespace.</item>
/// <item>Names are unique across the entire registry — collisions
/// throw <see cref="InvalidOperationException"/> with the colliding
/// pair surfaced.</item>
/// </list>
/// (Note: the registration-key-vs-Descriptor.Name check is implicit —
/// registrations come from a single <see cref="IEnumerable{IAgentTool}"/>,
/// the key IS the Descriptor.Name. The "name-mismatch" pin in tests
/// covers the case where a tool's static descriptor field gets
/// out-of-sync with its registered identity, which is currently
/// impossible structurally but is pinned for forward-compat against a
/// future "tool with overrideable name" pattern.)
/// </para>
///
/// <para>
/// JSON Schema validation of <see cref="AgentToolDescriptor.ParameterSchema"/>
/// is deferred to Phase 3.B (when <c>SearchDocumentsTool</c> lands
/// with a non-trivial multi-property schema; EchoTool's 1-property
/// schema doesn't justify the surface yet).
/// </para>
/// </summary>
public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, IAgentTool> _toolsByName;
    private readonly IReadOnlyList<AgentToolDescriptor> _descriptors;

    public ToolRegistry(IEnumerable<IAgentTool> registeredTools)
    {
        ArgumentNullException.ThrowIfNull(registeredTools);

        var tools = registeredTools.ToList();
        var byName = new Dictionary<string, IAgentTool>(StringComparer.Ordinal);

        foreach (var tool in tools)
        {
            ValidateDescriptor(tool);

            if (byName.ContainsKey(tool.Descriptor.Name))
            {
                throw new InvalidOperationException(
                    $"Tool registry name collision: '{tool.Descriptor.Name}' is registered by both " +
                    $"'{byName[tool.Descriptor.Name].GetType().FullName}' and '{tool.GetType().FullName}'. " +
                    "Tool names must be unique across the registry. Drop one registration or rename the tool.");
            }
            byName[tool.Descriptor.Name] = tool;
        }

        _toolsByName = byName;
        _descriptors = tools.Select(t => t.Descriptor).ToList();
    }

    public IReadOnlyList<AgentToolDescriptor> Descriptors => _descriptors;

    public IAgentTool? GetTool(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }
        return _toolsByName.TryGetValue(name, out var tool) ? tool : null;
    }

    private static void ValidateDescriptor(IAgentTool tool)
    {
        var descriptor = tool.Descriptor;
        if (descriptor is null)
        {
            throw new InvalidOperationException(
                $"Tool '{tool.GetType().FullName}' returned a null Descriptor. " +
                "IAgentTool.Descriptor is non-nullable per Trellis.Core's contract.");
        }
        if (string.IsNullOrWhiteSpace(descriptor.Name))
        {
            throw new InvalidOperationException(
                $"Tool '{tool.GetType().FullName}' has empty/whitespace Descriptor.Name. " +
                "Names are required for the planner LLM + AgentStep persistence.");
        }
        if (string.IsNullOrWhiteSpace(descriptor.Description))
        {
            throw new InvalidOperationException(
                $"Tool '{tool.GetType().FullName}' (Name='{descriptor.Name}') has " +
                "empty/whitespace Descriptor.Description. The description is the planner LLM's " +
                "only signal for what the tool does — without it, the model can't reason about " +
                "when to invoke.");
        }
    }
}

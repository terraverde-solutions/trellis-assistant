using Microsoft.Extensions.Options;
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
/// turned on in Phase 3.B: the ctor calls
/// <see cref="IJsonSchemaValidator.EnsureValidSchema"/> per registered
/// tool. A malformed schema string throws
/// <see cref="InvalidJsonSchemaException"/>, caught + rethrown here as
/// <see cref="InvalidOperationException"/> with the offending tool's
/// type + descriptor Name in the message — startup log identifies which
/// tool caused the failure without operator detective work.
/// </para>
///
/// <para>
/// Phase 3.C exposure filter: <see cref="Descriptors"/> returns only the
/// LLM-visible subset, gated by <see cref="ToolCatalogueOptions"/>. Tools
/// stay registered + dispatchable via <see cref="GetTool"/> regardless of
/// exposure — operators can still target unexposed tools through the
/// standalone <c>POST /api/agent-runs</c> with an explicit
/// <c>toolNames</c> filter. Hidden tools just don't appear in the LLM's
/// function-calling tool array (the planner never tries to call what it
/// can't see).
/// </para>
/// </summary>
public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, IAgentTool> _toolsByName;
    private readonly IReadOnlyList<AgentToolDescriptor> _exposedDescriptors;

    public ToolRegistry(
        IEnumerable<IAgentTool> registeredTools,
        IJsonSchemaValidator schemaValidator,
        IOptions<ToolCatalogueOptions> catalogueOptions)
    {
        ArgumentNullException.ThrowIfNull(registeredTools);
        ArgumentNullException.ThrowIfNull(schemaValidator);
        ArgumentNullException.ThrowIfNull(catalogueOptions);

        var options = catalogueOptions.Value;
        var tools = registeredTools.ToList();
        var byName = new Dictionary<string, IAgentTool>(StringComparer.Ordinal);

        foreach (var tool in tools)
        {
            ValidateDescriptor(tool);
            ValidateSchema(tool, schemaValidator);

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
        _exposedDescriptors = tools
            .Where(t => IsExposed(t, options))
            .Select(t => t.Descriptor)
            .ToList();
    }

    public IReadOnlyList<AgentToolDescriptor> Descriptors => _exposedDescriptors;

    /// <summary>
    /// Phase 3.C exposure gate. Per-tool config flag pattern; matches
    /// <c>Assistant:AutoMigrate</c> + <c>Auth:RequireHttpsMetadata</c>
    /// surrounding conventions. EchoTool is hidden by default
    /// (production-safe — it's a debugging aid, not a real tool); future
    /// tools default exposed unless a per-tool flag says otherwise.
    /// </summary>
    private static bool IsExposed(IAgentTool tool, ToolCatalogueOptions options)
    {
        if (tool.Descriptor.Name == EchoTool.ToolName)
        {
            return options.ExposeEcho;
        }
        return true;
    }

    public IAgentTool? GetTool(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }
        return _toolsByName.TryGetValue(name, out var tool) ? tool : null;
    }

    private static void ValidateSchema(IAgentTool tool, IJsonSchemaValidator validator)
    {
        var descriptor = tool.Descriptor;
        if (string.IsNullOrWhiteSpace(descriptor.ParameterSchema))
        {
            throw new InvalidOperationException(
                $"Tool '{tool.GetType().FullName}' (Name='{descriptor.Name}') has " +
                "empty/whitespace Descriptor.ParameterSchema. The schema is required for " +
                "the executor's runtime args validation; an empty schema would let any " +
                "arguments through.");
        }
        try
        {
            validator.EnsureValidSchema(descriptor.ParameterSchema);
        }
        catch (InvalidJsonSchemaException ex)
        {
            throw new InvalidOperationException(
                $"Tool '{tool.GetType().FullName}' (Name='{descriptor.Name}') has " +
                $"a malformed Descriptor.ParameterSchema: {ex.Message}",
                ex);
        }
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

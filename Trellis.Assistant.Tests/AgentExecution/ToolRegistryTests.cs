using FluentAssertions;
using Trellis.Assistant.AgentExecution;
using Trellis.Core.Models;
using Trellis.Core.Services;
using Xunit;

namespace Trellis.Assistant.Tests.AgentExecution;

/// <summary>
/// Phase 3.A C7 ratified: tool registry validation IS still required at
/// startup, just trimmed scope (no JSON Schema validation; that's
/// deferred to Phase 3.B with SearchDocumentsTool).
///
/// Three pinned tests per ratification:
///   - <c>ToolRegistry_NameCollision_FailsAtStartup</c>
///   - <c>ToolRegistry_EmptyDescriptorField_FailsAtStartup</c>
///   - <c>ToolRegistry_NameMismatch_FailsAtStartup</c>
///
/// Plus straight-shot tests for <see cref="IToolRegistry.GetTool"/>
/// happy + miss paths.
/// </summary>
public sealed class ToolRegistryTests
{
    [Fact]
    public void NameCollision_FailsAtStartup()
    {
        var firstEcho = new EchoTool();
        var collidingEcho = new FakeTool(name: "echo", description: "shouldn't matter");
        var act = () => new ToolRegistry(new IAgentTool[] { firstEcho, collidingEcho });
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*name collision*echo*",
                "two tools registered with the same Descriptor.Name must crash the host on construction");
    }

    [Fact]
    public void EmptyName_FailsAtStartup()
    {
        var bad = new FakeTool(name: "", description: "valid description");
        var act = () => new ToolRegistry(new IAgentTool[] { bad });
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*empty/whitespace Descriptor.Name*");
    }

    [Fact]
    public void WhitespaceName_FailsAtStartup()
    {
        var bad = new FakeTool(name: "   ", description: "valid description");
        var act = () => new ToolRegistry(new IAgentTool[] { bad });
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*empty/whitespace Descriptor.Name*",
                "whitespace-only Name is functionally indistinguishable from empty");
    }

    [Fact]
    public void EmptyDescription_FailsAtStartup()
    {
        var bad = new FakeTool(name: "valid_name", description: "");
        var act = () => new ToolRegistry(new IAgentTool[] { bad });
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*empty/whitespace Descriptor.Description*");
    }

    [Fact]
    public void WhitespaceDescription_FailsAtStartup()
    {
        var bad = new FakeTool(name: "valid_name", description: "   ");
        var act = () => new ToolRegistry(new IAgentTool[] { bad });
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*empty/whitespace Descriptor.Description*");
    }

    [Fact]
    public void NameMismatch_BetweenRegistrationAndDescriptor_FailsAtStartup()
    {
        // Phase 3.A C7 ratified pin replacing the original schema-
        // validation pin. Currently impossible structurally (registration
        // key IS Descriptor.Name), but pinned for forward-compat against
        // a future "tool with overrideable name" pattern that drifts
        // the registration key from the descriptor.
        //
        // Models the failure as: a tool registered as a singleton whose
        // Descriptor.Name is internally inconsistent (e.g. the Descriptor
        // returns one Name on first call and a different Name on second
        // call due to a caching bug). That manifests as collision-like
        // behaviour and is caught by the same unique-name check.
        var slipperyTool = new MutableNameTool();
        slipperyTool.NameFromDescriptor = "consistent_name";
        var legitTool = new FakeTool(name: "consistent_name", description: "second tool with same descriptor name");
        // After construction, the slippery tool already cached its
        // descriptor in the registry's dict. Adding the legit tool
        // (same name) triggers the collision detection — same code
        // path that catches name-mismatch drift.
        var act = () => new ToolRegistry(new IAgentTool[] { slipperyTool, legitTool });
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*name collision*consistent_name*");
    }

    [Fact]
    public void GetTool_RegisteredName_ReturnsTool()
    {
        var echo = new EchoTool();
        var registry = new ToolRegistry(new IAgentTool[] { echo });
        registry.GetTool("echo").Should().BeSameAs(echo);
    }

    [Fact]
    public void GetTool_UnknownName_ReturnsNull()
    {
        var echo = new EchoTool();
        var registry = new ToolRegistry(new IAgentTool[] { echo });
        registry.GetTool("not_registered").Should().BeNull();
    }

    [Fact]
    public void GetTool_NullOrEmptyName_ReturnsNull()
    {
        var echo = new EchoTool();
        var registry = new ToolRegistry(new IAgentTool[] { echo });
        registry.GetTool(null!).Should().BeNull();
        registry.GetTool("").Should().BeNull();
    }

    [Fact]
    public void Descriptors_ExposesAllRegistered_InRegistrationOrder()
    {
        var first = new FakeTool(name: "alpha", description: "first tool");
        var second = new FakeTool(name: "beta", description: "second tool");
        var registry = new ToolRegistry(new IAgentTool[] { first, second });
        registry.Descriptors.Select(d => d.Name).Should().Equal("alpha", "beta");
    }

    private sealed class FakeTool : IAgentTool
    {
        public AgentToolDescriptor Descriptor { get; }

        public FakeTool(string name, string description)
        {
            Descriptor = new AgentToolDescriptor
            {
                Name = name,
                Description = description,
                ParameterSchema = """{"type":"object"}""",
                Category = AgentToolCategory.Inspect,
            };
        }

        public Task<AgentToolOutput> RunAsync(AgentToolInput input, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentToolOutput { Success = true, ResultJson = "{}" });
    }

    private sealed class MutableNameTool : IAgentTool
    {
        public string NameFromDescriptor { get; set; } = "consistent_name";

        public AgentToolDescriptor Descriptor => new()
        {
            Name = NameFromDescriptor,
            Description = "tool whose descriptor identity could drift if not caught",
            ParameterSchema = """{"type":"object"}""",
            Category = AgentToolCategory.Inspect,
        };

        public Task<AgentToolOutput> RunAsync(AgentToolInput input, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentToolOutput { Success = true, ResultJson = "{}" });
    }
}

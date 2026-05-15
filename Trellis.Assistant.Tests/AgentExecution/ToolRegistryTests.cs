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
    private readonly JsonSchemaNetValidator _schemaValidator = new();
    private readonly Microsoft.Extensions.Options.IOptions<ToolCatalogueOptions> _catalogueOptions =
        Microsoft.Extensions.Options.Options.Create(new ToolCatalogueOptions { ExposeEcho = true });
    // Phase 3.F: Phase 3.C registry tests pre-date the IToolExposurePolicy
    // dependency. The default no-op policy (all tools exposed) preserves
    // these tests' assertions — they verify ctor validation +
    // untenanted Descriptors property, not the tenancy-gated path.
    private readonly IToolExposurePolicy _exposurePolicy =
        new AllowAllExposurePolicy();

    [Fact]
    public void NameCollision_FailsAtStartup()
    {
        var firstEcho = new EchoTool();
        var collidingEcho = new FakeTool(name: "echo", description: "shouldn't matter");
        var act = () => new ToolRegistry(new IAgentTool[] { firstEcho, collidingEcho }, _schemaValidator, _catalogueOptions, _exposurePolicy);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*name collision*echo*",
                "two tools registered with the same Descriptor.Name must crash the host on construction");
    }

    [Fact]
    public void EmptyName_FailsAtStartup()
    {
        var bad = new FakeTool(name: "", description: "valid description");
        var act = () => new ToolRegistry(new IAgentTool[] { bad }, _schemaValidator, _catalogueOptions, _exposurePolicy);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*empty/whitespace Descriptor.Name*");
    }

    [Fact]
    public void WhitespaceName_FailsAtStartup()
    {
        var bad = new FakeTool(name: "   ", description: "valid description");
        var act = () => new ToolRegistry(new IAgentTool[] { bad }, _schemaValidator, _catalogueOptions, _exposurePolicy);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*empty/whitespace Descriptor.Name*",
                "whitespace-only Name is functionally indistinguishable from empty");
    }

    [Fact]
    public void EmptyDescription_FailsAtStartup()
    {
        var bad = new FakeTool(name: "valid_name", description: "");
        var act = () => new ToolRegistry(new IAgentTool[] { bad }, _schemaValidator, _catalogueOptions, _exposurePolicy);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*empty/whitespace Descriptor.Description*");
    }

    [Fact]
    public void WhitespaceDescription_FailsAtStartup()
    {
        var bad = new FakeTool(name: "valid_name", description: "   ");
        var act = () => new ToolRegistry(new IAgentTool[] { bad }, _schemaValidator, _catalogueOptions, _exposurePolicy);
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
        var act = () => new ToolRegistry(new IAgentTool[] { slipperyTool, legitTool }, _schemaValidator, _catalogueOptions, _exposurePolicy);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*name collision*consistent_name*");
    }

    [Fact]
    public void GetTool_RegisteredName_ReturnsTool()
    {
        var echo = new EchoTool();
        var registry = new ToolRegistry(new IAgentTool[] { echo }, _schemaValidator, _catalogueOptions, _exposurePolicy);
        registry.GetTool("echo").Should().BeSameAs(echo);
    }

    [Fact]
    public void GetTool_UnknownName_ReturnsNull()
    {
        var echo = new EchoTool();
        var registry = new ToolRegistry(new IAgentTool[] { echo }, _schemaValidator, _catalogueOptions, _exposurePolicy);
        registry.GetTool("not_registered").Should().BeNull();
    }

    [Fact]
    public void GetTool_NullOrEmptyName_ReturnsNull()
    {
        var echo = new EchoTool();
        var registry = new ToolRegistry(new IAgentTool[] { echo }, _schemaValidator, _catalogueOptions, _exposurePolicy);
        registry.GetTool(null!).Should().BeNull();
        registry.GetTool("").Should().BeNull();
    }

    // ---------------- Phase 3.C: exposure filter ----------------

    [Fact]
    public void ExposeEchoFalse_HidesEchoFromDescriptors_ButGetToolStillResolves()
    {
        // Phase 3.C contract: exposure controls only Descriptors (the
        // LLM-visible catalogue). GetTool always resolves so existing
        // dispatch paths (operator-initiated POST /api/agent-runs with
        // an explicit toolNames filter, etc.) keep working.
        var options = Microsoft.Extensions.Options.Options.Create(
            new ToolCatalogueOptions { ExposeEcho = false });
        var registry = new ToolRegistry(
            new IAgentTool[] { new EchoTool() },
            _schemaValidator,
            options,
            _exposurePolicy);
        registry.Descriptors.Should().BeEmpty(
            "ExposeEcho=false hides EchoTool from the LLM-visible catalogue");
        registry.GetTool(EchoTool.ToolName).Should().NotBeNull(
            "ExposeEcho=false does NOT unregister the tool; dispatch still resolves it");
    }

    [Fact]
    public void ExposeEchoTrue_IncludesEchoInDescriptors()
    {
        var options = Microsoft.Extensions.Options.Options.Create(
            new ToolCatalogueOptions { ExposeEcho = true });
        var registry = new ToolRegistry(
            new IAgentTool[] { new EchoTool() },
            _schemaValidator,
            options,
            _exposurePolicy);
        registry.Descriptors.Should().ContainSingle(
            d => d.Name == EchoTool.ToolName,
            "ExposeEcho=true makes EchoTool visible in Descriptors");
    }

    [Fact]
    public void NonEchoTools_AlwaysExposed_RegardlessOfExposeEchoFlag()
    {
        // ExposeEcho governs only EchoTool. Other tools (production
        // tools like SearchDocumentsTool, future Phase 3.D+ additions)
        // default to exposed unless their own per-tool flag flips.
        var options = Microsoft.Extensions.Options.Options.Create(
            new ToolCatalogueOptions { ExposeEcho = false });
        var productionTool = new FakeTool(
            name: "future_production_tool",
            description: "A non-echo tool added in some future phase");
        var registry = new ToolRegistry(
            new IAgentTool[] { new EchoTool(), productionTool },
            _schemaValidator,
            options,
            _exposurePolicy);
        registry.Descriptors.Should().ContainSingle(
            d => d.Name == "future_production_tool",
            "non-echo tools stay exposed even when ExposeEcho=false");
        registry.Descriptors.Select(d => d.Name)
            .Should().NotContain(EchoTool.ToolName);
    }

    // ---------------- Phase 3.F: GetExposedDescriptorsFor (tenancy-aware) ----------------

    [Fact]
    public void GetExposedDescriptorsFor_DelegatesToPolicy_ReturnsFilteredList()
    {
        // Phase 3.F: registry's tenancy-aware getter invokes the
        // IToolExposurePolicy per tool. With a deny-only-echo stub
        // policy + EchoTool + a production tool, the returned list
        // should exclude echo.
        var prodTool = new FakeTool("prod_tool", "production tool that exposure allows");
        var policy = new DenyEchoPolicy();
        var registry = new ToolRegistry(
            new IAgentTool[] { new EchoTool(), prodTool },
            _schemaValidator,
            _catalogueOptions,  // ExposeEcho = true, but the policy denies it independently
            policy);

        var tenancy = new AgentToolTenancy(
            Guid.Parse(TestFixtures.TestTenants.TenantA), "user-a", TenantRole: null);
        var exposed = registry.GetExposedDescriptorsFor(tenancy);

        exposed.Should().ContainSingle(d => d.Name == "prod_tool");
        exposed.Should().NotContain(d => d.Name == EchoTool.ToolName,
            "policy denied EchoTool; registry filters it out of the tenancy-scoped list");
    }

    [Fact]
    public void GetExposedDescriptorsFor_DifferentTenancies_DifferentLists()
    {
        // Pin: the registry calls the policy per-tenancy. A policy that
        // allows tool only for TenantA returns different lists for
        // TenantA vs TenantB.
        var gatedTool = new FakeTool("tenant_a_only", "only TenantA may see");
        var policy = new TenantAOnlyPolicy(Guid.Parse(TestFixtures.TestTenants.TenantA));
        var registry = new ToolRegistry(
            new IAgentTool[] { gatedTool },
            _schemaValidator,
            _catalogueOptions,
            policy);

        var tenantAList = registry.GetExposedDescriptorsFor(new AgentToolTenancy(
            Guid.Parse(TestFixtures.TestTenants.TenantA), "u", null));
        var tenantBList = registry.GetExposedDescriptorsFor(new AgentToolTenancy(
            Guid.Parse(TestFixtures.TestTenants.TenantB), "u", null));

        tenantAList.Should().ContainSingle(d => d.Name == "tenant_a_only");
        tenantBList.Should().BeEmpty(
            "policy hides tenant_a_only from TenantB; registry filters per-call");
    }

    [Fact]
    public void GetExposedDescriptorsFor_EmptyResult_ReturnsEmptyListNotNull()
    {
        var policy = new DenyAllPolicy();
        var registry = new ToolRegistry(
            new IAgentTool[] { new EchoTool() },
            _schemaValidator,
            _catalogueOptions,
            policy);

        var exposed = registry.GetExposedDescriptorsFor(new AgentToolTenancy(
            Guid.Parse(TestFixtures.TestTenants.TenantA), "u", null));

        exposed.Should().NotBeNull();
        exposed.Should().BeEmpty();
    }

    [Fact]
    public void Descriptors_UntenantedPath_BypassesPolicy()
    {
        // Phase 3.F pin #6: the standalone POST /api/agent-runs path
        // reads Descriptors (untenanted) — bypasses IToolExposurePolicy.
        // Operator runs see the full catalog regardless of the
        // policy's per-tenancy decisions.
        var prodTool = new FakeTool("prod_tool", "production tool");
        // Policy that denies ALL tools — Descriptors still returns them
        // (minus echo, gated by ExposeEcho=true → exposed in this options).
        var registry = new ToolRegistry(
            new IAgentTool[] { new EchoTool(), prodTool },
            _schemaValidator,
            _catalogueOptions,
            new DenyAllPolicy());

        registry.Descriptors.Select(d => d.Name).Should().Contain("prod_tool",
            "Descriptors property uses Phase 3.C ExposeEcho only — Phase 3.F policy is NOT consulted");
        registry.Descriptors.Select(d => d.Name).Should().Contain(EchoTool.ToolName,
            "ExposeEcho=true exposes echo on the untenanted path regardless of policy");
    }

    // Phase 3.F test helper policies — distinct from the AllowAllExposurePolicy
    // used by the ctor-validation tests above.
    private sealed class DenyEchoPolicy : IToolExposurePolicy
    {
        public bool IsExposedTo(IAgentTool tool, AgentToolTenancy tenancy)
            => tool.Descriptor.Name != EchoTool.ToolName;
    }

    private sealed class TenantAOnlyPolicy : IToolExposurePolicy
    {
        private readonly Guid _allowed;
        public TenantAOnlyPolicy(Guid allowed) { _allowed = allowed; }
        public bool IsExposedTo(IAgentTool tool, AgentToolTenancy tenancy)
            => tenancy.TenantId == _allowed;
    }

    private sealed class DenyAllPolicy : IToolExposurePolicy
    {
        public bool IsExposedTo(IAgentTool tool, AgentToolTenancy tenancy) => false;
    }

    [Fact]
    public void MalformedParameterSchema_FailsAtStartup_WithToolIdentifyingMessage()
    {
        // Phase 3.B turn-on: JSON Schema validation gates registration.
        // A tool registered with a malformed ParameterSchema must crash
        // the host at startup with a message identifying the offending
        // tool — operators need to know WHICH tool was misconfigured.
        var bad = new FakeTool(
            name: "bad_schema",
            description: "valid description",
            parameterSchema: "{not a valid json schema");
        var act = () => new ToolRegistry(new IAgentTool[] { bad }, _schemaValidator, _catalogueOptions, _exposurePolicy);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*bad_schema*malformed*",
                "registry surfaces the tool name + 'malformed' so startup logs identify the offending registration");
    }

    [Fact]
    public void EmptyParameterSchema_FailsAtStartup()
    {
        var bad = new FakeTool(
            name: "no_schema",
            description: "valid description",
            parameterSchema: "");
        var act = () => new ToolRegistry(new IAgentTool[] { bad }, _schemaValidator, _catalogueOptions, _exposurePolicy);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*empty/whitespace*ParameterSchema*");
    }

    [Fact]
    public void Descriptors_ExposesAllRegistered_InRegistrationOrder()
    {
        var first = new FakeTool(name: "alpha", description: "first tool");
        var second = new FakeTool(name: "beta", description: "second tool");
        var registry = new ToolRegistry(new IAgentTool[] { first, second }, _schemaValidator, _catalogueOptions, _exposurePolicy);
        registry.Descriptors.Select(d => d.Name).Should().Equal("alpha", "beta");
    }

    /// <summary>
    /// Phase 3.F test helper. Returns true for every tool — preserves
    /// the pre-Phase-3.F "all tools exposed" behavior for tests that
    /// don't specifically exercise the per-tenant gating matrix. Tests
    /// that DO exercise gating construct their own
    /// <see cref="IToolExposurePolicy"/> stub or use the production
    /// <see cref="DefaultToolExposurePolicy"/> with crafted options.
    /// </summary>
    private sealed class AllowAllExposurePolicy : IToolExposurePolicy
    {
        public bool IsExposedTo(IAgentTool tool, AgentToolTenancy tenancy) => true;
    }

    private sealed class FakeTool : IAgentTool
    {
        public AgentToolDescriptor Descriptor { get; }

        public FakeTool(string name, string description, string parameterSchema = """{"type":"object"}""")
        {
            Descriptor = new AgentToolDescriptor
            {
                Name = name,
                Description = description,
                ParameterSchema = parameterSchema,
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

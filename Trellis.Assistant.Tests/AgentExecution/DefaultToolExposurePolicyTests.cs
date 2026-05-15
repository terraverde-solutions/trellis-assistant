using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Trellis.Assistant.AgentExecution;
using Trellis.Core.Models;
using Trellis.Core.Services;
using Xunit;

namespace Trellis.Assistant.Tests.AgentExecution;

/// <summary>
/// Phase 3.F pure-unit pins for <see cref="DefaultToolExposurePolicy"/>.
/// Exercises the EchoTool special case + the per-tool AllowedTenants
/// allowlist + the RequiredRole gate + their AND-combination + the
/// no-rule default-exposed behavior. No DB, no HTTP, no executor —
/// just the policy logic against synthetic tools + options.
/// </summary>
public sealed class DefaultToolExposurePolicyTests
{
    private static readonly Guid TenantA = Guid.Parse(TestFixtures.TestTenants.TenantA);
    private static readonly Guid TenantB = Guid.Parse(TestFixtures.TestTenants.TenantB);

    [Fact]
    public void EchoTool_ExposeEchoFalse_HiddenRegardlessOfTenancy()
    {
        var policy = NewPolicy(new ToolCatalogueOptions { ExposeEcho = false });
        var tenancy = new AgentToolTenancy(TenantA, "user-a", TenantRole: null);

        policy.IsExposedTo(new EchoTool(), tenancy).Should().BeFalse(
            "EchoTool is gated solely by the ExposeEcho flag (Phase 3.C special case); " +
            "tenancy doesn't enter the decision");
    }

    [Fact]
    public void EchoTool_ExposeEchoTrue_Exposed()
    {
        var policy = NewPolicy(new ToolCatalogueOptions { ExposeEcho = true });
        var tenancy = new AgentToolTenancy(TenantA, "user-a", TenantRole: null);

        policy.IsExposedTo(new EchoTool(), tenancy).Should().BeTrue();
    }

    [Fact]
    public void NonEchoTool_NoRule_DefaultExposed()
    {
        // Phase 3.F pin #1: tools without a PerTool entry are exposed
        // to all tenants by default. Preserves the pre-Phase-3.F
        // contract for callers that don't author per-tool config.
        var policy = NewPolicy(new ToolCatalogueOptions());
        var tenancy = new AgentToolTenancy(TenantA, "user-a", TenantRole: null);

        policy.IsExposedTo(FakeTool("unconfigured_tool"), tenancy).Should().BeTrue();
    }

    [Fact]
    public void AllowedTenants_EmptyList_AnyTenantPasses()
    {
        // Empty list documents "no tenant restriction" — distinct from
        // a missing rule (which also defaults to exposed) but allows
        // operators to specify a rule that ONLY has RequiredRole.
        var policy = NewPolicy(new ToolCatalogueOptions
        {
            PerTool = new()
            {
                ["gated_tool"] = new ToolExposureRule { AllowedTenants = new() },
            },
        });

        policy.IsExposedTo(FakeTool("gated_tool"),
            new AgentToolTenancy(TenantA, "u", null)).Should().BeTrue();
        policy.IsExposedTo(FakeTool("gated_tool"),
            new AgentToolTenancy(TenantB, "u", null)).Should().BeTrue();
    }

    [Fact]
    public void AllowedTenants_TenantInList_Exposed()
    {
        var policy = NewPolicy(new ToolCatalogueOptions
        {
            PerTool = new()
            {
                ["gated_tool"] = new ToolExposureRule
                {
                    AllowedTenants = new() { TenantA },
                },
            },
        });

        policy.IsExposedTo(FakeTool("gated_tool"),
            new AgentToolTenancy(TenantA, "u", null)).Should().BeTrue();
    }

    [Fact]
    public void AllowedTenants_TenantNotInList_Hidden()
    {
        var policy = NewPolicy(new ToolCatalogueOptions
        {
            PerTool = new()
            {
                ["gated_tool"] = new ToolExposureRule
                {
                    AllowedTenants = new() { TenantA },
                },
            },
        });

        policy.IsExposedTo(FakeTool("gated_tool"),
            new AgentToolTenancy(TenantB, "u", null)).Should().BeFalse(
            "TenantB not in [TenantA] AllowedTenants → hidden");
    }

    [Fact]
    public void RequiredRole_NullRole_NoGate_Exposed()
    {
        var policy = NewPolicy(new ToolCatalogueOptions
        {
            PerTool = new()
            {
                ["gated_tool"] = new ToolExposureRule { RequiredRole = null },
            },
        });

        policy.IsExposedTo(FakeTool("gated_tool"),
            new AgentToolTenancy(TenantA, "u", TenantRole: "any")).Should().BeTrue();
    }

    [Fact]
    public void RequiredRole_Match_Exposed()
    {
        var policy = NewPolicy(new ToolCatalogueOptions
        {
            PerTool = new()
            {
                ["gated_tool"] = new ToolExposureRule { RequiredRole = "admin" },
            },
        });

        policy.IsExposedTo(FakeTool("gated_tool"),
            new AgentToolTenancy(TenantA, "u", TenantRole: "admin")).Should().BeTrue();
    }

    [Fact]
    public void RequiredRole_Mismatch_Hidden()
    {
        var policy = NewPolicy(new ToolCatalogueOptions
        {
            PerTool = new()
            {
                ["gated_tool"] = new ToolExposureRule { RequiredRole = "admin" },
            },
        });

        policy.IsExposedTo(FakeTool("gated_tool"),
            new AgentToolTenancy(TenantA, "u", TenantRole: "user")).Should().BeFalse();
    }

    [Fact]
    public void RequiredRole_NullRoleOnTenancy_FailClosed()
    {
        // Macro 2 PR 6.7 missing-claim defense: when the request
        // carries no tenant_role claim AND the rule requires one,
        // hide the tool. Don't silently pass — that's an exposure leak.
        var policy = NewPolicy(new ToolCatalogueOptions
        {
            PerTool = new()
            {
                ["gated_tool"] = new ToolExposureRule { RequiredRole = "admin" },
            },
        });

        policy.IsExposedTo(FakeTool("gated_tool"),
            new AgentToolTenancy(TenantA, "u", TenantRole: null)).Should().BeFalse(
            "null TenantRole vs non-null RequiredRole = fail-closed (hidden)");
    }

    [Fact]
    public void RequiredRole_CaseSensitive_Exact()
    {
        // Phase 3.F pin #4: single-string ordinal comparison.
        // "Admin" ≠ "admin".
        var policy = NewPolicy(new ToolCatalogueOptions
        {
            PerTool = new()
            {
                ["gated_tool"] = new ToolExposureRule { RequiredRole = "admin" },
            },
        });

        policy.IsExposedTo(FakeTool("gated_tool"),
            new AgentToolTenancy(TenantA, "u", TenantRole: "Admin")).Should().BeFalse(
            "case-sensitive ordinal compare — uppercase 'Admin' doesn't match lowercase 'admin'");
    }

    [Fact]
    public void Combined_AllowedTenants_AND_RequiredRole_BothMustPass()
    {
        // Combined gate: AllowedTenants AND RequiredRole. Both must
        // pass; failing either hides the tool.
        var policy = NewPolicy(new ToolCatalogueOptions
        {
            PerTool = new()
            {
                ["gated_tool"] = new ToolExposureRule
                {
                    AllowedTenants = new() { TenantA },
                    RequiredRole = "admin",
                },
            },
        });

        // TenantA + admin → exposed.
        policy.IsExposedTo(FakeTool("gated_tool"),
            new AgentToolTenancy(TenantA, "u", "admin")).Should().BeTrue();

        // TenantA + non-admin → hidden (role gate fails).
        policy.IsExposedTo(FakeTool("gated_tool"),
            new AgentToolTenancy(TenantA, "u", "user")).Should().BeFalse();

        // TenantB + admin → hidden (tenant gate fails).
        policy.IsExposedTo(FakeTool("gated_tool"),
            new AgentToolTenancy(TenantB, "u", "admin")).Should().BeFalse();

        // TenantB + non-admin → hidden (both gates fail).
        policy.IsExposedTo(FakeTool("gated_tool"),
            new AgentToolTenancy(TenantB, "u", "user")).Should().BeFalse();
    }

    [Fact]
    public void EchoToolInPerToolConfig_IgnoredInFavorOfExposeEcho()
    {
        // Pin: even if operators add "echo" to PerTool, the policy
        // honors ExposeEcho. Listing echo in PerTool isn't an error;
        // it's just silently ignored.
        var policy = NewPolicy(new ToolCatalogueOptions
        {
            ExposeEcho = true,
            PerTool = new()
            {
                ["echo"] = new ToolExposureRule { RequiredRole = "admin" },
            },
        });

        // ExposeEcho=true wins even though the PerTool rule would
        // hide echo from a non-admin caller.
        policy.IsExposedTo(new EchoTool(),
            new AgentToolTenancy(TenantA, "u", TenantRole: null)).Should().BeTrue(
            "EchoTool gating is ExposeEcho-only; PerTool entry for 'echo' is ignored");
    }

    // ----- helpers -----

    private static DefaultToolExposurePolicy NewPolicy(ToolCatalogueOptions opts)
    {
        var monitor = new StaticOptionsMonitor<ToolCatalogueOptions>(opts);
        return new DefaultToolExposurePolicy(monitor, NullLogger<DefaultToolExposurePolicy>.Instance);
    }

    private static IAgentTool FakeTool(string name) => new FakeAgentTool(name);

    private sealed class FakeAgentTool : IAgentTool
    {
        public FakeAgentTool(string name)
        {
            Descriptor = new AgentToolDescriptor
            {
                Name = name,
                Description = $"fake tool {name}",
                ParameterSchema = """{"type":"object"}""",
                Category = AgentToolCategory.Inspect,
            };
        }

        public AgentToolDescriptor Descriptor { get; }

        public Task<AgentToolOutput> RunAsync(AgentToolInput input, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("FakeAgentTool doesn't dispatch — policy tests only.");
    }

    /// <summary>
    /// Static <see cref="IOptionsMonitor{T}"/> stub returning the
    /// supplied options regardless of named-options key + ignoring
    /// change-token subscriptions. Sufficient for the policy's
    /// CurrentValue reads.
    /// </summary>
    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

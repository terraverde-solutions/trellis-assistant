using Microsoft.Extensions.Options;
using Trellis.Core.Services;

namespace Trellis.Assistant.AgentExecution;

/// <summary>
/// Phase 3.F default <see cref="IToolExposurePolicy"/>. Reads
/// <see cref="ToolCatalogueOptions"/> (<c>Assistant:Tools</c> config)
/// for the EchoTool flag + the per-tool PerTool matrix, applies the
/// gating logic, returns true/false per tool+tenancy.
///
/// <para>
/// Gating order:
/// <list type="number">
/// <item>EchoTool special case: <see cref="ToolCatalogueOptions.ExposeEcho"/>
/// boolean (Phase 3.C). No tenant filtering on echo — operator-tier
/// gating only.</item>
/// <item>Other tools: look up rule by tool name in
/// <see cref="ToolCatalogueOptions.PerTool"/>.</item>
/// <item>If no rule found → exposed (Phase 3.F pin #1 back-compat).</item>
/// <item>If rule found with non-empty <see cref="ToolExposureRule.AllowedTenants"/>
/// → tenancy's TenantId must be in the list.</item>
/// <item>If rule found with non-null <see cref="ToolExposureRule.RequiredRole"/>
/// → tenancy's TenantRole must match (ordinal, case-sensitive).</item>
/// </list>
/// All gates AND together — failing any gate hides the tool. Denials
/// log at Debug per Phase 3.F pin #7 (gating is normal operation, not
/// an alert-worthy event).
/// </para>
/// </summary>
public sealed class DefaultToolExposurePolicy : IToolExposurePolicy
{
    private readonly IOptionsMonitor<ToolCatalogueOptions> _options;
    private readonly ILogger<DefaultToolExposurePolicy> _logger;

    public DefaultToolExposurePolicy(
        IOptionsMonitor<ToolCatalogueOptions> options,
        ILogger<DefaultToolExposurePolicy> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    public bool IsExposedTo(IAgentTool tool, AgentToolTenancy tenancy)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(tenancy);

        var opts = _options.CurrentValue;
        var name = tool.Descriptor.Name;

        // EchoTool special case (Phase 3.C). The ExposeEcho flag
        // gates EchoTool regardless of PerTool config — listing
        // "echo" in PerTool is harmless but ignored.
        if (name == EchoTool.ToolName)
        {
            if (!opts.ExposeEcho)
            {
                _logger.LogDebug(
                    "Tool {Name} hidden from tenant {TenantId} (role={Role}, reason=ExposeEcho=false)",
                    name, tenancy.TenantId, tenancy.TenantRole ?? "(none)");
                return false;
            }
            return true;
        }

        // WorkflowScheduleTool special case (Phase 3.I). Same shape as
        // EchoTool — opt-in flag is the first gate. When flipped on,
        // falls through to the PerTool path so per-tenant gating
        // (AllowedTenants + RequiredRole) layers on top.
        if (name == WorkflowScheduleTool.ToolName)
        {
            if (!opts.ExposeWorkflowSchedule)
            {
                _logger.LogDebug(
                    "Tool {Name} hidden from tenant {TenantId} (role={Role}, reason=ExposeWorkflowSchedule=false)",
                    name, tenancy.TenantId, tenancy.TenantRole ?? "(none)");
                return false;
            }
            // Fall through to PerTool checks below.
        }

        // Non-Echo tools: check the per-tool rule.
        if (!opts.PerTool.TryGetValue(name, out var rule))
        {
            // No rule → default exposed (Phase 3.F pin #1).
            return true;
        }

        // AllowedTenants gate. Empty list = any tenant passes.
        if (rule.AllowedTenants.Count > 0 && !rule.AllowedTenants.Contains(tenancy.TenantId))
        {
            _logger.LogDebug(
                "Tool {Name} hidden from tenant {TenantId} (role={Role}, reason=TenantId not in AllowedTenants)",
                name, tenancy.TenantId, tenancy.TenantRole ?? "(none)");
            return false;
        }

        // RequiredRole gate. Null = no role gate.
        // Fail-closed on null-role + non-null RequiredRole (Macro 2 PR
        // 6.7 missing-claim defense — request carries no role → any
        // role gate fails).
        if (!string.IsNullOrEmpty(rule.RequiredRole))
        {
            if (string.IsNullOrEmpty(tenancy.TenantRole))
            {
                _logger.LogDebug(
                    "Tool {Name} hidden from tenant {TenantId} (role=null, reason=RequiredRole={Required} but request carries no role claim)",
                    name, tenancy.TenantId, rule.RequiredRole);
                return false;
            }
            if (!string.Equals(tenancy.TenantRole, rule.RequiredRole, StringComparison.Ordinal))
            {
                _logger.LogDebug(
                    "Tool {Name} hidden from tenant {TenantId} (role={Role}, reason=RequiredRole={Required} mismatch)",
                    name, tenancy.TenantId, tenancy.TenantRole, rule.RequiredRole);
                return false;
            }
        }

        return true;
    }
}

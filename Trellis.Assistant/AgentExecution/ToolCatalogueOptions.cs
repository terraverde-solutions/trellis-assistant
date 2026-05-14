namespace Trellis.Assistant.AgentExecution;

/// <summary>
/// Phase 3.C: per-environment knobs governing which registered tools are
/// surfaced to the LLM (the "exposed catalogue") versus which stay
/// registered but invisible (dispatchable only when explicitly named).
/// Bound from <c>Assistant:Tools</c> in appsettings; default values
/// match the production posture (test-only tools hidden).
///
/// <para>
/// Per-flag pattern matches the surrounding codebase
/// (<c>Assistant:AutoMigrate</c>, <c>Auth:RequireHttpsMetadata</c>): one
/// boolean per tool that defaults to the production-safe value, dev/test
/// environments override via <c>appsettings.user.json</c> or
/// <c>WebApplicationFactory&lt;Program&gt;.ConfigureAppConfiguration</c>.
/// </para>
///
/// <para>
/// FORWARD-FLAG (per hub's Phase 3.C ratify): if Phase 3.D+ adds 2+ more
/// test-only tools (<c>EchoDelay</c>, <c>EchoError</c>, etc.), the
/// per-tool <c>Expose&lt;X&gt;</c> config forest becomes a maintenance
/// smell. At that point we revisit moving exposure onto the
/// <see cref="Trellis.Core.Services.IAgentTool"/> interface itself
/// (Option B from the ratify — <c>bool IsExposedByDefault</c>). For
/// Phase 3.C with one test tool (EchoTool), the config flag is the
/// smaller diff + matches surrounding patterns.
/// </para>
/// </summary>
public sealed class ToolCatalogueOptions
{
    public const string SectionName = "Assistant:Tools";

    /// <summary>
    /// When <c>true</c>, <c>EchoTool</c>'s descriptor appears in
    /// <see cref="IToolRegistry.Descriptors"/> (the LLM-visible
    /// catalogue). When <c>false</c>, EchoTool stays registered + remains
    /// dispatchable via <see cref="IToolRegistry.GetTool"/> (so existing
    /// tests + operator-initiated dispatch through
    /// <c>POST /api/agent-runs</c>'s <c>toolNames</c> filter still work)
    /// — but the LLM never sees it in its function-calling tool array.
    ///
    /// <para>
    /// Default <c>false</c> (production-safe): EchoTool is a debugging
    /// aid, not a production tool. Letting an LLM spam it in a customer
    /// conversation would be confusing. Test environments
    /// (<c>AssistantWebApplicationFactory</c>) override to <c>true</c>.
    /// </para>
    /// </summary>
    public bool ExposeEcho { get; set; } = false;

    /// <summary>
    /// Phase 3.F per-tenant tool gating. Map keyed by tool name
    /// (<see cref="Trellis.Core.Models.AgentToolDescriptor.Name"/>); each
    /// rule combines an <see cref="ToolExposureRule.AllowedTenants"/>
    /// UUID allowlist + an optional <see cref="ToolExposureRule.RequiredRole"/>
    /// gate. Tools NOT in this map default to "exposed to all tenants"
    /// per Phase 3.F pin #1 (back-compat: no PerTool config → unchanged
    /// behavior).
    ///
    /// <para>
    /// EchoTool is the exception — it's gated by <see cref="ExposeEcho"/>
    /// (Phase 3.C), NOT by PerTool. Listing "echo" in PerTool is
    /// harmless but ignored by the policy.
    /// </para>
    ///
    /// <para>
    /// Sample appsettings binding:
    /// <code>
    /// "Assistant:Tools:PerTool": {
    ///   "chat_recent": {
    ///     "AllowedTenants": [],          // [] = any tenant
    ///     "RequiredRole": "admin"        // only role=admin sees it
    ///   },
    ///   "search_documents": {
    ///     "AllowedTenants": ["00000000-0000-0000-0000-000000000001"]
    ///   }
    /// }
    /// </code>
    /// </para>
    /// </summary>
    public Dictionary<string, ToolExposureRule> PerTool { get; set; } = new();
}

/// <summary>
/// One per-tool exposure rule. AllowedTenants + RequiredRole are AND'd
/// at the policy boundary — both must pass for the tool to expose.
/// Empty <see cref="AllowedTenants"/> + null <see cref="RequiredRole"/>
/// is equivalent to "no gating" (same as omitting the rule entirely).
/// </summary>
public sealed class ToolExposureRule
{
    /// <summary>
    /// Allowlist of tenant Guids that may see this tool. Empty list →
    /// any tenant passes the AllowedTenants gate (the RequiredRole gate
    /// still applies independently if set). Matches the JWT's
    /// <c>tenant_id</c> claim shape (uuid-format string per Phase 3.A
    /// C1; parsed to Guid by the orchestrator before policy invocation).
    /// </summary>
    public List<Guid> AllowedTenants { get; set; } = new();

    /// <summary>
    /// Required role string. <c>null</c> → no role gate (the
    /// AllowedTenants gate still applies). Non-null → the request's
    /// <see cref="AgentToolTenancy.TenantRole"/> must equal this string
    /// exactly (ordinal comparison; case-sensitive). Macro 2 ships
    /// single-role-per-user, so a single-string check is sufficient
    /// per Phase 3.F pin #4; a future multi-role-per-user surface would
    /// upgrade this to a list.
    ///
    /// <para>
    /// Fail-closed posture: if the request carries no role claim
    /// (<c>TenantRole == null</c>), any non-null <c>RequiredRole</c>
    /// hides the tool. Matches Macro 2 PR 6.7's missing-claim defense
    /// convention.
    /// </para>
    /// </summary>
    public string? RequiredRole { get; set; }
}

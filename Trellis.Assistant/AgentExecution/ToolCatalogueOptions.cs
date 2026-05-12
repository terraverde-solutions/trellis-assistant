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
}

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Trellis.Assistant.Observability;

/// <summary>
/// Phase 3.H: agent-execution OpenTelemetry surface. Static
/// <see cref="Meter"/> + <see cref="ActivitySource"/> + counters +
/// histogram registered process-wide; OTel SDK wired in
/// <c>Program.cs</c> opts into observation via the configured
/// <see cref="OpenTelemetryOptions"/>.
///
/// <para>
/// Naming convention per OTel semantic conventions:
/// <list type="bullet">
/// <item><b>Meter / ActivitySource name:</b>
/// <c>Trellis.Assistant.AgentExecution</c> (PascalCase namespace
/// matches .NET convention for Meter naming).</item>
/// <item><b>Metric names:</b>
/// <c>trellis.assistant.tool.dispatch.{count,duration_ms,failure.count,budget_exhausted.count}</c>
/// (lowercase, dot-separated, matches OTel semantic-conventions
/// pattern for custom metrics).</item>
/// <item><b>Activity names:</b> <c>agent.tool.dispatch</c> (verb-noun-action;
/// matches OTel span-naming guidance for internal spans).</item>
/// <item><b>Activity / metric tags:</b> <c>tool.name</c>,
/// <c>tenant.id</c>, <c>outcome</c>, plus activity-only
/// <c>agent.run.id</c> + <c>step.index</c>.</item>
/// </list>
/// </para>
///
/// <para>
/// Cardinality discipline (Phase 3.H pin #3): NEVER tag with
/// <c>user_id</c> — would create unbounded cardinality across the
/// fleet's users. <c>tenant.id</c> is bounded by customer count (~10s
/// to 100s); operator-side cardinality limits at the collector are the
/// belt-and-suspenders. <c>outcome</c> is a fixed enum (success /
/// failure / cancelled / budget_exhausted). <c>tool.name</c> is
/// bounded by the registered tool count (~3 in v0). <c>step.index</c>
/// is high-cardinality but lives only on activity tags (tracing
/// systems handle this; metrics never tag with it).
/// </para>
/// </summary>
public static class AgentTelemetry
{
    /// <summary>Shared name for the Meter + ActivitySource. Operators wire OTel pipelines against this single string.</summary>
    public const string SourceName = "Trellis.Assistant.AgentExecution";

    /// <summary>Static Meter; OTel SDK registers a listener at startup.</summary>
    public static readonly Meter Meter = new(SourceName, "1.0.0");

    /// <summary>Static ActivitySource; OTel SDK registers a listener at startup.</summary>
    public static readonly ActivitySource ActivitySource = new(SourceName, "1.0.0");

    /// <summary>
    /// Counter: total tool dispatches, regardless of outcome. Tagged
    /// with <c>tool.name</c>, <c>tenant.id</c>, <c>outcome</c>. Aggregating
    /// at the collector by <c>outcome=success</c> yields success rate;
    /// dividing by total yields success ratio.
    /// </summary>
    public static readonly Counter<long> ToolDispatchCount =
        Meter.CreateCounter<long>(
            "trellis.assistant.tool.dispatch.count",
            unit: "{dispatch}",
            description: "Total agent-tool dispatches, tagged by tool.name + tenant.id + outcome.");

    /// <summary>
    /// Histogram: dispatch wall-clock duration in milliseconds. Tagged
    /// with <c>tool.name</c>, <c>tenant.id</c>, <c>outcome</c>.
    /// Collector derives p50/p95/p99 — don't pre-bucket here per
    /// Phase 3.H pin #2.
    /// </summary>
    public static readonly Histogram<double> ToolDispatchDurationMs =
        Meter.CreateHistogram<double>(
            "trellis.assistant.tool.dispatch.duration_ms",
            unit: "ms",
            description: "Per-tool-dispatch wall-clock duration in milliseconds.");

    /// <summary>
    /// Counter: dispatch FAILURES (tool returned Success=false OR threw).
    /// Distinct from <see cref="ToolDispatchCount"/> with
    /// <c>outcome=failure</c> tag — operators can alert on this metric
    /// directly without filtering by tag value (faster on simple
    /// collectors that don't index tags efficiently). The counter is
    /// ALSO tagged for drill-down.
    /// </summary>
    public static readonly Counter<long> ToolDispatchFailureCount =
        Meter.CreateCounter<long>(
            "trellis.assistant.tool.dispatch.failure.count",
            unit: "{dispatch}",
            description: "Tool dispatches that returned Success=false OR threw — distinct from cancellation + budget-exhausted.");

    /// <summary>
    /// Counter: budget-exhausted halts mid-iteration. Phase 3.E pin #6
    /// path. Distinct from <see cref="ToolDispatchFailureCount"/>
    /// because budget exhaustion is operator-policy outcome (the LLM
    /// emitted MORE tool_calls than the budget allowed), not a tool
    /// fault. Aggregating both as failures would mask budget-tuning
    /// signal.
    /// </summary>
    public static readonly Counter<long> ToolDispatchBudgetExhaustedCount =
        Meter.CreateCounter<long>(
            "trellis.assistant.tool.dispatch.budget_exhausted.count",
            unit: "{halt}",
            description: "Agent-run halts triggered by the mid-iteration budget gate (Phase 3.E pin #6 path).");

    /// <summary>
    /// Outcome enum string values used as the <c>outcome</c> tag.
    /// Bounded set — operators can alert on any of these without
    /// cardinality concerns.
    /// </summary>
    public static class Outcomes
    {
        public const string Success = "success";
        public const string Failure = "failure";
        public const string Cancelled = "cancelled";
        public const string BudgetExhausted = "budget_exhausted";
    }
}

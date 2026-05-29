using System.Diagnostics;
using System.Diagnostics.Metrics;
using Trellis.Core.Models;

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
    /// Phase 3.J Thread B histogram: token usage recorded PER LLM CALL
    /// (after each <c>ChatWithToolsAsync</c> returns successfully) rather
    /// than once at terminal. Per-call emission lets operators spot
    /// runaway loops mid-flight (e.g. a model that keeps emitting
    /// tool_calls and burning tokens) rather than waiting for the run to
    /// finish. Tagged with <c>model</c> + <c>tenant.id</c> per pin #3 —
    /// NO <c>user.id</c> (unbounded cardinality); NO <c>agent.run.id</c>
    /// or <c>step.index</c> (high cardinality; activity-only).
    /// </summary>
    public static readonly Histogram<double> TokensUsed =
        Meter.CreateHistogram<double>(
            "trellis.assistant.tokens.used",
            unit: "{tokens}",
            description: "Tokens consumed per LLM call (emitted after each ChatWithToolsAsync return) — tagged by model + tenant.id.");

    /// <summary>
    /// Phase 3.J Thread B histogram: per-LLM-call wall-clock latency in
    /// milliseconds. Stopwatch-measured around the
    /// <c>ChatWithToolsAsync</c> await; recorded ONLY on successful
    /// return (failed LLM calls already terminate the loop and shouldn't
    /// emit latency metrics for incomplete data). Tagged with
    /// <c>model</c> + <c>tenant.id</c>.
    /// </summary>
    public static readonly Histogram<double> LlmCallDurationMs =
        Meter.CreateHistogram<double>(
            "trellis.assistant.llm.call.duration_ms",
            unit: "ms",
            description: "Per-LLM-call wall-clock duration in milliseconds — tagged by model + tenant.id.");

    /// <summary>
    /// Phase 3.J Thread B histogram: total agent-run wall-clock duration
    /// in milliseconds. Emitted ONCE at the end of the loop body, just
    /// before the executor returns the terminal <c>LoopResult</c>.
    /// Tagged with <c>tenant.id</c> + <c>outcome</c> (mapped from the
    /// terminal <see cref="AgentRunStatus"/> via
    /// <see cref="Outcomes.Map"/>).
    /// </summary>
    public static readonly Histogram<double> AgentRunDurationMs =
        Meter.CreateHistogram<double>(
            "trellis.assistant.agent.run.duration_ms",
            unit: "ms",
            description: "Per-agent-run wall-clock duration in milliseconds — tagged by tenant.id + outcome.");

    /// <summary>
    /// Outcome enum string values used as the <c>outcome</c> tag.
    /// Bounded set — operators can alert on any of these without
    /// cardinality concerns.
    /// </summary>
    public static class Outcomes
    {
        // Shared vocabulary across Phase 3.H (per-tool-dispatch) +
        // Phase 3.J (per-run) so an operator writing a cross-histogram
        // PromQL query on the `outcome` tag gets consistent values for
        // the same conceptual outcome. Originally 3.J emitted
        // past-tense forms ("succeeded"/"failed"); the 3.J fix-up
        // aligned to 3.H's noun forms ("success"/"failure"). Run-level
        // additions (cap_reached, loop_detected) have no 3.H equivalent
        // and stay run-specific.
        public const string Success = "success";
        public const string Failure = "failure";
        public const string Cancelled = "cancelled";
        public const string BudgetExhausted = "budget_exhausted";

        // Phase 3.J Thread B run-specific additions. CapReached +
        // LoopDetected have no per-dispatch equivalent in 3.H —
        // they're properties of the agent run as a whole.
        public const string CapReached = "cap_reached";
        public const string LoopDetected = "loop_detected";
        // Defensive bucket for in-flight states (Planning, Running) that
        // shouldn't reach terminal mapping; an alert on outcome=unknown
        // surfaces a programming regression without crashing the run.
        public const string Unknown = "unknown";

        /// <summary>
        /// Map a terminal <see cref="AgentRunStatus"/> to its
        /// <c>outcome</c> tag string for the
        /// <see cref="AgentRunDurationMs"/> histogram. Bounded enum →
        /// bounded tag values, safe for metric cardinality.
        /// </summary>
        public static string Map(AgentRunStatus status) => status switch
        {
            AgentRunStatus.Succeeded => Success,
            AgentRunStatus.Failed => Failure,
            AgentRunStatus.CapReached => CapReached,
            AgentRunStatus.LoopDetected => LoopDetected,
            AgentRunStatus.Cancelled => Cancelled,
            // Planning + Running are in-flight; they shouldn't be the
            // terminal status passed to the run-duration histogram. If
            // a programming bug lets one through, the "unknown" bucket
            // keeps the metric pipeline alive without crashing the run.
            _ => Unknown,
        };
    }
}

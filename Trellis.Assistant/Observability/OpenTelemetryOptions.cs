namespace Trellis.Assistant.Observability;

/// <summary>
/// Phase 3.H: OpenTelemetry configuration bound from
/// <c>OpenTelemetry:*</c> in appsettings. All fields optional —
/// production deploys set <see cref="Endpoint"/> to the OTLP collector;
/// dev/test leaves it null/empty and the OTLP exporter registration is
/// skipped entirely (the Meter + ActivitySource still register so test
/// MeterListener / ActivityListener pins observe local events).
///
/// <para>
/// OTLP HTTP (not gRPC) per Phase 3.H pin #4. Simpler firewall posture
/// for the QA collector behind ports-80/443; matches the trellis-deploy
/// network policy. The collector itself can re-export to anywhere
/// (Jaeger, Prometheus, Loki, etc.) — the Assistant's choice is just
/// the protocol on the wire.
/// </para>
/// </summary>
public sealed class OpenTelemetryOptions
{
    public const string SectionName = "OpenTelemetry";

    /// <summary>
    /// OTLP HTTP endpoint base URL. When null or whitespace, the OTLP
    /// exporter is NOT registered + telemetry stays in-process (Meter
    /// + ActivitySource still register; test pins see local events).
    /// Production sets this to the collector's HTTP receiver, e.g.
    /// <c>http://otel-collector.trellis-qa:4318</c>.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Service name attached to every exported metric / span. Defaults
    /// to <c>trellis-assistant</c>; QA + production override to
    /// disambiguate fleet members (e.g. <c>trellis-assistant-qa</c>).
    /// </summary>
    public string ServiceName { get; set; } = "trellis-assistant";

    /// <summary>
    /// Service version string attached as a resource attribute. Operators
    /// rolling deploys can correlate spike-on-version traces by querying
    /// the collector on this tag.
    /// </summary>
    public string ServiceVersion { get; set; } = "0.3.8";
}

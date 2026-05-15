# Phase 3.H design — OpenTelemetry tool-call observability

**Status:** scaffolded on `kimi/phase3h-otel-tool-dispatch`. Closes the operator-visibility gap left by every prior Phase 3 surface: per-tool dispatch was reachable via `journalctl -u trellis-assistant-qa` `LogInformation/LogWarning` greps but had no aggregate counter / latency / failure-ratio surface. Phase 3.H wires the OTel SDK + 1 ActivitySource + 4 custom metrics + log-scope trace correlation; OTLP HTTP exporter is opt-in via the `OpenTelemetry:Endpoint` config knob.

## Brief baseline + decisions

Hub's brief pinned six decisions; all preserved (no deviations).

| # | Decision | Rationale |
|---|---|---|
| 1 | Three distinct dispatch counters (count, failure, budget-exhausted) | Operators alert on `failure.count` + `budget_exhausted.count` directly without filtering `count` by tag value — faster on collectors that don't index tags efficiently. Total `count` still serves the success-ratio query (denominator = total, numerator = filter outcome=success). |
| 2 | Histogram for duration; no pre-bucketing | Collector derives p50/p95/p99 on the fly. Pre-bucketing here would hardcode percentile boundaries that may not match Phase 4's channel-adapter SLO targets. |
| 3 | Tag with `tool.name` + `tenant.id` + `outcome`; **NEVER** tag with `user_id` | `user_id` is unbounded across the fleet — would explode cardinality at the collector. `tenant.id` is bounded by customer count (~10s to 100s); `tool.name` bounded by registered tool count (~3 in v0). `outcome` is a fixed enum (success / failure / cancelled / budget_exhausted). |
| 4 | OTLP **HTTP** exporter, not gRPC | Simpler firewall posture for the QA collector behind ports-80/443; matches trellis-deploy network policy. The collector can re-export to anywhere (Jaeger, Prometheus, Loki); the Assistant's choice is just the wire protocol. |
| 5 | No-op when `OpenTelemetry:Endpoint` is null/empty | Dev + test stay in-process. The Meter + ActivitySource still register so test pins observe local events via `MeterListener` / `ActivityListener`; only the OTLP exporter is gated. Removes the external-network dependency for the local dev loop. |
| 6 | Source name: `Trellis.Assistant.AgentExecution` | PascalCase namespace matches .NET Meter naming convention. One source per logical component (Phase 4 channel-adapter telemetry would register its own source, e.g. `Trellis.Assistant.ChannelAdapters`). |

## What landed

**New package refs** (`Trellis.Assistant.csproj`):
- `OpenTelemetry.Extensions.Hosting` 1.15.3 — the SDK + `AddOpenTelemetry()` host-builder extension.
- `OpenTelemetry.Instrumentation.AspNetCore` 1.15.2 — auto-instruments inbound `/api/*` requests as server spans + http.server.* metrics.
- `OpenTelemetry.Instrumentation.Http` 1.15.1 — auto-instruments outbound `HttpClient` calls (Trainer search + Ollama LLM) as client spans + http.client.* metrics.
- `OpenTelemetry.Exporter.OpenTelemetryProtocol` 1.15.3 — OTLP HTTP/Protobuf exporter.

(Mixed minor versions per NuGet's nearest-available constraint — the Instrumentation packages release on separate cadences from the SDK; 1.15.x is the current stable family for net10.0. Pinned with `TreatWarningsAsErrors=true` requiring NU1902-clean versions.)

**New surface** (`Observability/`):
- `AgentTelemetry.cs` — static class with `public const string SourceName = "Trellis.Assistant.AgentExecution"`, the static `Meter` + `ActivitySource`, three counters, one histogram, and an `Outcomes` constants holder. Process-wide singletons; OTel SDK registers listeners against `SourceName`.
- `OpenTelemetryOptions.cs` — config record bound from `OpenTelemetry:*`. Fields: `Endpoint : string?` (null = no-op exporter), `ServiceName = "trellis-assistant"`, `ServiceVersion = "0.3.8"`. `SectionName = "OpenTelemetry"`.

**Pipeline wiring** (`Program.cs`):
- Reads `OpenTelemetryOptions` via `GetSection().Get<OpenTelemetryOptions>()`; computes `hasOtelEndpoint = !string.IsNullOrWhiteSpace(otelOpts.Endpoint)`.
- `services.AddOpenTelemetry().ConfigureResource(...).WithMetrics(...).WithTracing(...)`:
  - **Metrics pipeline:** adds `AgentTelemetry.SourceName` + `TenantClaimsMiddleware.MeterName` (Phase 3.B deprecated-header counter rides the same SDK) + ASP.NET Core + HttpClient instrumentation. Conditionally adds OTLP HTTP exporter when `hasOtelEndpoint`.
  - **Tracing pipeline:** adds `AgentTelemetry.SourceName` source + ASP.NET Core + HttpClient instrumentation. Conditionally adds OTLP HTTP exporter when `hasOtelEndpoint`.
- Resource attributes set via `AddService(serviceName, serviceVersion)` — collectors index spans/metrics by service name for fleet-wide queries.

**Executor instrumentation** (`AgentExecution/AssistantAgentExecutor.cs`):
- `DispatchOneToolCallAsync` opens an Activity at the top via `AgentTelemetry.ActivitySource.StartActivity("agent.tool.dispatch", ActivityKind.Internal)`. Activity tags: `tool.name`, `tenant.id` (orgId.ToString("D")), `agent.run.id`, `step.index`. Activity is disposed in a `using` so it captures wall-clock duration via the underlying ActivitySource regardless of which exit path the dispatch takes.
- Per-dispatch log scope opens around the same region: `_logger.BeginScope(new Dictionary<string, object?> { ["trace_id"] = Activity.Current?.TraceId.ToString(), ["span_id"] = Activity.Current?.SpanId.ToString(), ["tool.name"] = ..., ["agent.run.id"] = ... })`. Existing `LogInformation/LogWarning` lines inside the dispatch flow inherit the scope automatically — every per-tool log line carries the same `trace_id` as the parent activity, enabling Loki-side join with Jaeger spans.
- Metrics emission at each terminal path:
  - **Tool-not-registered** → `outcome=failure`, activity status `Error`, failure counter fires.
  - **Schema validation reject** → `outcome=failure`, activity tag `dispatch.reject_reason="schema_validation"`, status `Error`, status description carries the validation message.
  - **Successful dispatch** → `outcome=success`, activity status `Ok`, only the count + duration histogram fire (no failure counter).
  - **OperationCanceledException + CT requested** → `outcome=cancelled`, activity status `Unset` (cancellation isn't an error), only count + duration fire.
  - **Other exception / Success=false** → `outcome=failure`, activity status `Error`, failure counter fires.
- `ExecuteLoopAsync` budget-exhausted mid-iteration branch (Phase 3.E pin #6 path): `ToolDispatchBudgetExhaustedCount.Add(1, tags)` tagged with the SKIPPED tool's name + tenant.id + budget decision. Distinct from `ToolDispatchFailureCount` because budget exhaust is operator-policy outcome, not a tool fault. Aggregating both as failures would mask budget-tuning signal.
- `EmitDispatchMetrics(toolName, tenantId, outcome, durationMs)` helper centralizes the 3 tag construction + 2 metric calls; failure-counter emission gated on `outcome == Failure`.

**Config defaults** (`appsettings.json`):
```json
"OpenTelemetry": {
  "_endpoint_doc": "Phase 3.H. OTLP HTTP collector endpoint. When null/empty (default in dev), the Meter + ActivitySource still register so test pins observe via MeterListener/ActivityListener, but the OTLP exporter is NOT wired — no external network dependency in dev. QA/production sets this to the trellis-deploy-side collector, e.g. http://otel-collector.trellis-qa:4318",
  "Endpoint": null,
  "ServiceName": "trellis-assistant",
  "ServiceVersion": "0.3.8"
}
```

## Metric inventory

All metrics emitted by `AgentTelemetry.Meter` (source name `Trellis.Assistant.AgentExecution`):

| Metric name | Type | Unit | Tags | Notes |
|---|---|---|---|---|
| `trellis.assistant.tool.dispatch.count` | Counter\<long\> | `{dispatch}` | tool.name, tenant.id, outcome | Total dispatches regardless of outcome. Operator alerts on rate-of-change. |
| `trellis.assistant.tool.dispatch.duration_ms` | Histogram\<double\> | `ms` | tool.name, tenant.id, outcome | Wall-clock duration. Collector derives p50/p95/p99. |
| `trellis.assistant.tool.dispatch.failure.count` | Counter\<long\> | `{dispatch}` | tool.name, tenant.id | Distinct from `count[outcome=failure]` for faster collector alerts. No `outcome` tag — the metric IS the failure shape. |
| `trellis.assistant.tool.dispatch.budget_exhausted.count` | Counter\<long\> | `{halt}` | tool.name, tenant.id, budget_decision | Mid-iteration budget halt; the SKIPPED tool's name (not the last-successfully-dispatched). |

Pre-existing metric, re-emitted via the same SDK: `trellis.assistant.tenant_claims.deprecated_header` (Phase 3.B; registered via `TenantClaimsMiddleware.MeterName`).

ASP.NET Core auto-instrumentation also emits `http.server.request.duration` per endpoint hit; HttpClient instrumentation emits `http.client.request.duration` for Trainer + Ollama outbound. Both flow through the same exporter when `Endpoint` is set.

## Span inventory

All spans emitted by `AgentTelemetry.ActivitySource` (source name `Trellis.Assistant.AgentExecution`):

| Span name | Kind | Tags | Status |
|---|---|---|---|
| `agent.tool.dispatch` | Internal | tool.name, tenant.id, agent.run.id, step.index, (optional) dispatch.reject_reason | Ok on success; Error on schema-reject / tool-not-registered / exception / Success=false; Unset on cancellation |

ASP.NET Core auto-instrumentation emits server spans per inbound request; HttpClient instrumentation emits client spans for outbound HTTP. Span hierarchy when `/api/conversations/{id}/turns` triggers the agent path:

```
ASP.NET server span (POST /api/conversations/{id}/turns)
└── HttpClient span (POST {Ollama:BaseUrl}/api/chat)      ← LLM call 1
└── agent.tool.dispatch (tool.name=search_documents)       ← per-tool dispatch
│   └── HttpClient span (GET {Trainer:BaseUrl}/api/search)
└── agent.tool.dispatch (tool.name=chat_recent)
└── HttpClient span (POST {Ollama:BaseUrl}/api/chat)      ← LLM call 2 (synthesis)
```

## Log correlation

Every per-tool-dispatch `LogInformation/LogWarning` line inside `DispatchOneToolCallAsync` inherits a log scope opened at the top of the method with:
- `trace_id` — `Activity.Current?.TraceId.ToString()` (hex)
- `span_id` — `Activity.Current?.SpanId.ToString()` (hex)
- `tool.name`, `agent.run.id`

Operators querying Loki for `{service_name="trellis-assistant"} | json | tool_name="search_documents"` get the structured fields; pasting any `trace_id` into the Jaeger query box pulls the corresponding span hierarchy. Closes the Phase 3.C "operator visibility for tool dispatch" gap with a directly-actionable cross-system join.

## Cardinality discipline

Phase 3.H pin #3 forbids `user_id` as a metric tag. Enforced by:

1. **Code:** `EmitDispatchMetrics` accepts `toolName, tenantId, outcome, durationMs` only — no `userId` parameter. Adding one would require touching every call site.
2. **Test:** `ToolDispatch_Success_EmitsDispatchCount_WithExpectedTags` negative-asserts `tags.ContainsKey("user.id") == false` so a future drift attempts to add user.id breaks at green-build time.

Other potentially-unbounded tags considered + rejected:
- `agent.run.id` — high-cardinality (one per agent run). Lives on **activity tags only** (tracing systems handle high cardinality). Metrics never tag with this.
- `step.index` — high-cardinality (one per dispatch step). Same posture as `agent.run.id`: activity tags only.

## OTLP wiring + no-op fallback

`Program.cs` registers the OTel SDK unconditionally — the `Meter` + `ActivitySource` always have listeners. The OTLP HTTP exporter registration is gated:

```csharp
var hasOtelEndpoint = !string.IsNullOrWhiteSpace(otelOpts.Endpoint);
// ...
if (hasOtelEndpoint)
{
    metrics.AddOtlpExporter(opts =>
    {
        opts.Endpoint = new Uri(otelOpts.Endpoint!);
        opts.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
    });
}
```

When `Endpoint` is null/empty:
- **Test pins** still observe local emissions via `MeterListener` / `ActivityListener` subscribed to `AgentTelemetry.SourceName`. The 6 SkippableFact pins under `AgentTelemetryTests.cs` run without an OTLP collector.
- **Dev loop** has no external network dependency. `dotnet run` against the appsettings.json default starts cleanly without the collector running.
- **QA/production** sets `OpenTelemetry:Endpoint` via `/etc/trellis-assistant-qa.env` (e.g. `OpenTelemetry__Endpoint=http://otel-collector.trellis-qa:4318`).

## Test plan

6 SkippableFact pins under `Trellis.Assistant.Tests/Observability/AgentTelemetryTests.cs`. All run under `PostgresFixture` so the agent path can exercise the full executor. Inner helper classes `MetricCapture` (subscribes a `MeterListener` to `AgentTelemetry.SourceName`) + `ActivityCapture` (subscribes an `ActivityListener` to the same source) drain in-process emissions.

- **`ToolDispatch_Success_EmitsDispatchCount_WithExpectedTags`** — stubs LLM to emit one `echo` tool_call → asserts `ToolDispatchCount` fires count=1, tags include `tool.name=echo` + `outcome=success` + `tenant.id`; **NEGATIVE pin**: tags do NOT include `user.id` (pin #3 cardinality discipline).
- **`ToolDispatch_Success_RecordsDurationHistogram`** — same stub setup; asserts histogram fires once with value ≥ 0.
- **`ToolDispatch_SchemaValidationFailure_EmitsFailureCounter`** — stubs LLM to emit `search_documents` with malformed args → asserts `outcome=failure` on the count counter + distinct `ToolDispatchFailureCount` fires once.
- **`ToolDispatch_BudgetExhaustedMidIteration_EmitsBudgetExhaustedCounter_NotFailureCounter`** — stubs multi-tool LLM response with budget limit forcing mid-iteration halt → asserts `ToolDispatchBudgetExhaustedCount` fires once; `ToolDispatchFailureCount` is empty (pin #1 — budget halt is policy outcome, not fault).
- **`ToolDispatch_OpensActivity_WithExpectedTagsAndOkStatus`** — captures the activity; asserts tags include `tool.name`, `tenant.id`, `step.index`, `agent.run.id`; status is `Ok` on success path.
- **`ToolDispatch_FailureSetsActivityStatusError`** — schema-reject path; asserts activity status is `Error`, `StatusDescription` contains "schema validation failed", and the `dispatch.reject_reason="schema_validation"` tag is set.

## Wire-shape changes (operator-visible)

| Pre-3.H | Post-3.H |
|---|---|
| Per-tool dispatch visible only via `journalctl -u trellis-assistant-qa` log greps | Per-tool dispatch emits structured Activity span + 1 counter + 1 histogram + (on failure/budget-exhaust) 1 distinct counter. Logs carry `trace_id` + `span_id` for cross-system join. |
| No aggregate failure-ratio or p95-latency surface | Collector-derived: `count[outcome=failure] / count` for failure ratio; `histogram(duration_ms)` for percentiles. |
| Budget-exhaust visible via `LogWarning` + persisted `AgentRun.Status=CapReached` | Same + dedicated `budget_exhausted.count` counter for direct alerting. |
| No exporter — observability is local-only | Opt-in OTLP HTTP exporter via `OpenTelemetry:Endpoint`. Dev/test still works without it. |

No status-code changes; no breaking API changes. Phase 3.H is observability-additive only.

## Don't (forward-flag for Phase 3.I+)

- **Don't tag any metric with `user_id`.** Unbounded across the fleet. Pin #3. Pinned by `ToolDispatch_Success_EmitsDispatchCount_WithExpectedTags`'s negative assertion.
- **Don't switch the OTLP exporter from HTTP to gRPC.** Pin #4. QA collector firewall posture is built around ports-80/443; gRPC needs port 4317 open + matches a less-standard network policy.
- **Don't pre-bucket the duration histogram.** Pin #2. Collector-derived percentiles let downstream tuning happen without a code change + redeploy.
- **Don't add an `agent.run.id` or `step.index` metric tag.** High-cardinality; lives on activity tags only.
- **Don't aggregate budget-exhausted into failure counters.** Pin #1. Budget halt is operator-policy outcome (LLM emitted more tool_calls than allowed), not tool fault. Aggregating would mask budget-tuning signal.
- **Don't add a dedicated `AgentTelemetryUnitTests` class.** The 6 SkippableFact pins under PostgresFixture cover the contract end-to-end. A pure-unit class would re-test the Meter/ActivitySource registration without the agent-path's invocation context — net coverage cost not earned.
- **Don't move `AgentTelemetry` to Trellis.Core.** Single-consumer rule (Phase 3.A precedent + the same posture Phase 3.B + 3.D + 3.F took for their seams). Lift when Workflow or Server need to emit `tool.dispatch.*` metrics against their own tool catalogues.
- **Don't gate the Meter / ActivitySource registration on `hasOtelEndpoint`.** They must always register so in-process test pins observe local events. Only the OTLP exporter is gated.

## LoC

- Production: +347 (Observability/ +163, AgentTelemetry executor instrumentation +101, Program.cs OTel pipeline +64, csproj +13, appsettings +6).
- Tests: +375 (Observability/AgentTelemetryTests.cs).
- Cumulative: +722.

Within hub's 700–1000 estimate band.

## Phase 4 forward plan

- **Channel-adapter telemetry:** Phase 4 channel adapters (Slack/WhatsApp/Telegram webhook handlers) register their own ActivitySource (e.g. `Trellis.Assistant.ChannelAdapters`) with adapter-specific spans + metrics. The Phase 3.H pipeline is the wiring template.
- **Cross-component correlation:** Trainer's tool-called HTTP request already gets a `traceparent` header courtesy of `HttpClientInstrumentation`. If Trainer registers OTel pipelines on its side (sibling brief), spans join automatically at the collector.
- **Real-Ollama smoke + OTel:** the existing `RealOllamaSmokeTests` skip-gated path could opt-in to a local collector via `OpenTelemetry__Endpoint=http://localhost:4318` to verify end-to-end OTLP flow against a real Jaeger. Not in scope for Phase 3.H — the in-process MeterListener / ActivityListener pins cover the contract.

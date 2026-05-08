using Microsoft.EntityFrameworkCore;
using Trellis.Assistant.AgentExecution;
using Trellis.Assistant.Data;
using Trellis.Assistant.Endpoints;
using Trellis.Assistant.Middleware;
using Trellis.Assistant.Services;
using Trellis.Core.Services;

var builder = WebApplication.CreateBuilder(args);

// ---------------- Configuration: orchestrator options ----------------
builder.Services
    .AddOptions<ConversationOrchestratorOptions>()
    .Bind(builder.Configuration.GetSection(ConversationOrchestratorOptions.SectionName))
    .ValidateDataAnnotations();

// Phase 3.A.1: agent executor options. Bound from "Assistant:Agent"
// (Model, SystemPrompt). Distinct from ConversationOrchestratorOptions
// (TurnRequestTimeoutSeconds, WarmupModel, AutoMigrate); the agent
// surface lives next to but separate from the conversation surface.
builder.Services
    .AddOptions<AssistantAgentExecutorOptions>()
    .Bind(builder.Configuration.GetSection(AssistantAgentExecutorOptions.SectionName))
    .ValidateDataAnnotations();

// ---------------- Postgres + EF Core ----------------
//
// ConnectionStrings:Postgres carries the trellis_assistant_qa role + DB
// at runtime. Local dev uses a localhost Postgres; QA reads from
// /etc/trellis-assistant-qa.env (per the deploy bootstrap that lands
// in trellis-deploy alongside the Phase 1 PR).
//
// LATE-RESOLUTION RULE — load-bearing pattern across the whole project.
//
// The connection-string lookup runs inside the AddDbContext factory
// (LATE — at DI resolution time) rather than eagerly at builder time.
// Eager capture defeats WebApplicationFactory<Program>'s
// ConfigureAppConfiguration overlay: the factory adds its InMemoryCollection
// AFTER `var builder = WebApplication.CreateBuilder(args)` returns but
// BEFORE the host actually starts. Reading IConfiguration inside the
// DbContext factory delays the lookup until all overlays are visible,
// which is exactly when WebApplicationFactory expects to inject the
// test-container connection string. The Phase 1 endpoint integration
// tests would otherwise hit an "auth failed for user postgres" against
// the appsettings.json default rather than the Testcontainers fixture.
//
// PHASE 5 DIRECTIVE — JWT MIDDLEWARE: when real auth lands and replaces
// TenantHeadersMiddleware with AddJwtBearer, the same late-resolution
// rule applies. The failure mode is sneakier with JWT than with EFC
// because JwtBearerOptions can fail SILENTLY: the test issues a token
// with the test issuer, the middleware uses the production issuer
// because options were captured eagerly, the test fails with a
// confusing "issuer mismatch" 401 instead of a clear config-overlay-
// not-applied error.
//
// DO NOT do this:
//   builder.Services.AddAuthentication().AddJwtBearer(opts =>
//   {
//       opts.Authority = builder.Configuration["Auth:Authority"]; // EAGER — captures the appsettings default
//   });
//
// DO this instead:
//   builder.Services
//       .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
//       .Configure<IConfiguration>((opts, cfg) =>
//       {
//           opts.Authority = cfg["Auth:Authority"];   // LATE — resolves at JwtBearerOptions materialization
//           opts.Audience  = cfg["Auth:Audience"];
//       });
//
// The second form pulls IConfiguration from DI at options-binding time,
// which is post-WebApplicationFactory-overlay. Same rule, same reason,
// different DI shape. Tracking memo on hub side captures this as a
// cross-cutting Trellis pattern that survives Phase 5's middleware
// swap.
builder.Services.AddDbContext<AssistantDbContext>((sp, opts) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var cs = config.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException(
            "ConnectionStrings:Postgres is not configured. Set it in appsettings.user.json (Dev) or /etc/trellis-assistant-qa.env (QA).");
    opts.UseNpgsql(cs);
});

// ---------------- Domain services ----------------
//
// IAssistantConversationStore: scoped, lifecycle-bound to the AssistantDbContext.
// IOllamaClient: typed HttpClientFactory client (Phase 2). The HttpClient
//   lifetime is managed by IHttpClientFactory; the OllamaClient instance
//   itself is transient (one per resolution). The Func<Uri> base-URL provider
//   honors the same late-resolution rule as the connection string above —
//   it reads IConfiguration inside the lambda at request time, so test-side
//   ConfigureAppConfiguration overlays + future runtime config changes are
//   visible without restarting the host.
//
// Phase 1 registered AddSingleton<IOllamaClient, StubLlmClient>; the swap to
// the real client is the only DI change Phase 2 brings to the LLM seam. The
// stub still ships in the binary for unit tests that don't have an Ollama
// server reachable; the swap to the typed client wins at runtime.
builder.Services.AddScoped<IAssistantConversationStore, PostgresAssistantConversationStore>();
builder.Services.AddHttpClient<IOllamaClient>()
    .AddTypedClient<IOllamaClient>((http, sp) =>
    {
        var config = sp.GetRequiredService<IConfiguration>();
        return new OllamaClient(
            http,
            () => new Uri(config["Ollama:BaseUrl"]
                ?? throw new InvalidOperationException(
                    "Ollama:BaseUrl is not configured. Set it in appsettings.user.json (Dev) or /etc/trellis-assistant-qa.env (QA).")));
    });
builder.Services.AddScoped<ConversationOrchestrator>();

// ---------------- Readiness + warm-up ----------------
//
// Liveness vs readiness split — Kubernetes-style. /healthz reports
// process liveness (always 200 once Kestrel is listening); /readyz
// reports whether the LLM warm-up has succeeded. Operators wait on
// /readyz before declaring "deploy live"; users transparently get
// cold-load on first request if warm-up never succeeds (lazy fallback).
//
// The warm-up service is a BackgroundService — the host's
// ApplicationStopping CT cancels in-flight Ollama calls cleanly on
// systemctl restart. See OllamaWarmupHostedService class doc for the
// backoff schedule + per-attempt cap reasoning.
builder.Services.AddSingleton<OllamaReadinessState>();
builder.Services.AddHostedService<OllamaWarmupHostedService>();

// ---------------- Phase 3.A.1: agent execution surface ----------------
//
// IAgentRunStore: scoped, lifecycle-bound to the AssistantDbContext.
// IAgentLlmClient: typed HttpClientFactory client (parallel to
//   IOllamaClient — text-vs-function-calling have different streaming
//   semantics; lift to Trellis.Core deferred to first second-consumer
//   per Phase 3.A architectural ratification).
// IToolRegistry: singleton — populated once at construction by scanning
//   all registered IAgentTool services. Validation throws at host
//   startup on collision / empty descriptor / etc., so misconfigured
//   tool registrations crash the host before accepting traffic.
// EchoTool: registered as IAgentTool; the singleton-of-many pattern
//   lets future tools (Phase 3.B's SearchDocumentsTool, Phase 4's
//   send_message tools) drop in via a single AddSingleton<IAgentTool, ...>
//   line without touching the registry's wiring.
// IAgentBudgetGate: singleton, consumes Trellis.Core.Services.DefaultBudgetGate
//   (post-Phase-3.A.2 retrofit per qwen's Phase A merge). Assistant's
//   documented MaxSteps=25 default is injected by AssistantAgentExecutor
//   via WithAssistantDefaults — DefaultBudgetGate's own default is 1000
//   (Workflow's value), and Core's docstring on AgentBudgetOverrides
//   directs callers to inject per-surface defaults. The Assistant
//   executor IS that consumer for all Assistant paths; pinned by
//   Executor_BudgetOverridesNull_InjectsAssistantDefault25.
// IAgentExecutor: scoped — depends on the scoped store + transient
//   typed-client + singleton registry/gate/options.
builder.Services.AddScoped<IAgentRunStore, PostgresAgentRunStore>();
builder.Services.AddHttpClient<IAgentLlmClient>()
    .AddTypedClient<IAgentLlmClient>((http, sp) =>
    {
        var config = sp.GetRequiredService<IConfiguration>();
        return new OllamaAgentLlmClient(
            http,
            () => new Uri(config["Ollama:BaseUrl"]
                ?? throw new InvalidOperationException(
                    "Ollama:BaseUrl is not configured. Set it in appsettings.user.json (Dev) or /etc/trellis-assistant-qa.env (QA).")));
    });
builder.Services.AddSingleton<IAgentTool, EchoTool>();
builder.Services.AddSingleton<IToolRegistry, ToolRegistry>();
builder.Services.AddSingleton<IAgentBudgetGate, DefaultBudgetGate>();
// Register the concrete class + alias the interface to the same scope.
// Phase 3.A.2's ConversationOrchestrator depends on the concrete
// AssistantAgentExecutor for the RunForConversationAsync method (not on
// IAgentExecutor — that interface is the standalone POST /api/agent-runs
// surface, returns just AgentRun). Two registrations, one instance per
// scope; same lifetime semantics as the prior single-line interface
// registration.
builder.Services.AddScoped<AssistantAgentExecutor>();
builder.Services.AddScoped<IAgentExecutor>(sp => sp.GetRequiredService<AssistantAgentExecutor>());

var app = builder.Build();

// ---------------- Auto-migrate on startup ----------------
//
// Mirrors trellis-trainer's Trainer:AutoMigrate=true convention. Pending
// migrations apply before the host accepts traffic; on failure the host
// fails-fast with LogCritical rather than serving against a half-migrated
// schema. Operators bypass via Assistant:AutoMigrate=false in environments
// where migrations are run out-of-band.
var autoMigrate = builder.Configuration.GetValue("Assistant:AutoMigrate", defaultValue: true);
if (autoMigrate)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AssistantDbContext>();
    try
    {
        await db.Database.MigrateAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogCritical(ex, "EF migration on startup failed; refusing to serve traffic against a half-migrated schema.");
        throw;
    }
}

// ---------------- Phase 3.A.1: tool registry startup validation ----------------
//
// Resolve IToolRegistry once at startup to trigger the ToolRegistry
// constructor's eager validation (name uniqueness, non-empty descriptor
// fields). On validation failure, the ctor throws → host crashes here
// → no traffic accepted. The alternative — lazy first-resolution at
// the executor's first dispatch — would let a misconfigured tool
// registration lurk until a real user request triggers a host-internal
// failure that surfaces as a 500.
//
// JSON Schema validation of the descriptor's ParameterSchema is
// deferred to Phase 3.B (with SearchDocumentsTool's non-trivial arg
// shape).
try
{
    _ = app.Services.GetRequiredService<IToolRegistry>();
}
catch (Exception ex)
{
    app.Logger.LogCritical(ex, "Tool registry startup validation failed; refusing to serve traffic against a misconfigured tool catalogue.");
    throw;
}

// ---------------- Pipeline ----------------
//
// Auth header gate first — every /api/* request gets the trusted (tenant,
// user) tuple stashed in HttpContext.Items before reaching the endpoint
// handlers. /healthz bypasses the gate entirely (path check inside the
// middleware).
app.UseMiddleware<TenantHeadersMiddleware>();

// Phase 0 surface: /healthz. Same shape as before — this endpoint is
// load-bearing for the deploy-script smoke wrapper that hits
// 127.0.0.1:5117/healthz after `systemctl start`.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// Phase 2 surface: /readyz. 200 once Ollama warm-up has completed at
// least once; 503 until then. Hub-side Deploy-Assistant-Standalone.ps1
// extension polls /readyz with a 120s budget after `systemctl restart`
// before declaring the deploy live (lands as a sibling deploy PR).
app.MapGet("/readyz", (OllamaReadinessState state) =>
    state.IsReady
        ? Results.Ok(new { status = "ready" })
        : Results.Json(
            new { status = "warming-up" },
            statusCode: StatusCodes.Status503ServiceUnavailable));

// Phase 1 surface: conversations.
app.MapConversationEndpoints();

// Phase 3.A.1 surface: standalone agentic runs. Agent runs are
// independent of the conversation flow — Phase 3.A.2 wires the
// executor into POST /api/conversations/{id}/turns; this endpoint
// remains as the standalone surface for Phase 4 channel adapters.
app.MapAgentRunEndpoints();

app.Run();

// Hook for WebApplicationFactory<Program> in Trellis.Assistant.Tests.
public partial class Program { }

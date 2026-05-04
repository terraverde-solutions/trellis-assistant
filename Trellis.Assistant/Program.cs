using Microsoft.EntityFrameworkCore;
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
// IOllamaClient (Phase 1 stub): singleton — no per-request state. Phase 2
//   swaps the registration to Trellis.Core.Services.OllamaClient backed by
//   a named HttpClient; that swap is Phase 2's only DI change.
// ConversationOrchestrator: scoped; depends on the scoped store + the
//   singleton LLM. The advisory lock + the per-request timeout are spelled
//   out in the orchestrator's class doc.
builder.Services.AddScoped<IAssistantConversationStore, PostgresAssistantConversationStore>();
builder.Services.AddSingleton<IOllamaClient, StubLlmClient>();
builder.Services.AddScoped<ConversationOrchestrator>(sp => new ConversationOrchestrator(
    sp.GetRequiredService<IAssistantConversationStore>(),
    sp.GetRequiredService<IOllamaClient>(),
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ConversationOrchestratorOptions>>().Value));

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

// Phase 1 surface: conversations.
app.MapConversationEndpoints();

app.Run();

// Hook for WebApplicationFactory<Program> in Trellis.Assistant.Tests.
public partial class Program { }

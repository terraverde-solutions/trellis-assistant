var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

// Phase 0: a single liveness probe so the deploy-script smoke wrapper
// (deploy/scripts/qa/Deploy-Assistant-Standalone.ps1) has something real
// to hit after `systemctl start`. The orchestrator, channel adapters,
// tool dispatch, and voice surface land in Phase 1+ — see
// trellis-docs/MarkdownFiles/59-trellis-assistant.md for the design.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.Run();

// Hook for WebApplicationFactory<Program> in Trellis.Assistant.Tests.
// Top-level statements emit an internal Program type by default; this
// redeclaration makes it public so the test host can spin up an
// in-process server. Same convention as Trellis.Web's Program.cs.
public partial class Program { }

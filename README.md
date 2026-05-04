# Trellis.Assistant

Always-on personal AI surface for the [TerraVerde Trellis system](https://github.com/terraverde-solutions/trellis-docs). Voice-first multi-channel presence, MCP-based tool dispatch, cross-surface memory.

The full design is in [`trellis-docs/MarkdownFiles/59-trellis-assistant.md`](https://github.com/terraverde-solutions/trellis-docs/blob/main/MarkdownFiles/59-trellis-assistant.md). Read that first.

**Status:** Phase 1 — conversations API live with a stub LLM. Three endpoints (POST /api/conversations, GET /api/conversations/{id}, POST /api/conversations/{id}/turns), Postgres-backed persistence, multi-tenant chokepoint, per-conversation Postgres advisory lock around position-assignment + INSERT, header-trust auth (Phase 5 swaps to JWT). Channel adapters, real Ollama wiring, tool dispatch, and voice land in Phase 2+.

## Build / test / run

```bash
dotnet restore
dotnet build  --configuration Release
dotnet test   --configuration Release
dotnet run    --project Trellis.Assistant
```

`dotnet test` runs 19 tests across four files:

- `HealthCheckTests` — `/healthz` liveness against a stub-config WebApplicationFactory (no Postgres reach).
- `Integration/ConversationEndpointTests` — 12 end-to-end tests against a Testcontainers-backed Postgres 16: create + read + append, role/position invariants, 401 on missing/blank tenant or user headers, 400 on malformed ulid + empty content, 404 on unknown conversation + cross-tenant + cross-user, and a 5-way concurrency test pinning the advisory-lock contract.
- `Integration/EFMigrationSmokeTests` — 3 tests pinning the migration's column shape via `information_schema`, FK ON DELETE CASCADE, and the `channel` DEFAULT 'api'.
- `Trellis.Assistant.Tests/AppsettingsConventionsTests` — XML-shape pin on `Trellis.Assistant.csproj` (canonical `<None Remove>` + `<Content Remove>` exclude rule for `appsettings.user.json`) plus a publish-output integration test (`[Trait("Category", "Integration")]`).

The `Integration` tests need a running Docker daemon for Testcontainers; they self-skip via `[SkippableFact]` + an `IsDockerAvailable()` probe when Docker isn't reachable. On a CI runner without Docker the 15 integration tests show as **Skipped** (not failed). Use `--filter "Category!=Integration"` for the fast unit-only suite.

Local Kestrel binding picks the default ASP.NET Core dev port (5000); QA + Production deploys pin the unit's `--urls` to 127.0.0.1:5117.

### Trellis.Core dependency

`Trellis.Assistant.csproj` references `..\..\core\Trellis.Core\Trellis.Core.csproj`. Clone the sibling [trellis-core repo](https://github.com/terraverde-solutions/trellis-core) into `c:\dev\core\` (the canonical worker-tree path) before building. Phase 1 consumes `IAssistantConversationStore` + `AssistantConversation` / `AssistantTurn` / `NewAssistantTurn` / `AssistantTurnRole` from `Trellis.Core.Services` (added in trellis-core PR #9), plus `IOllamaClient` + `ChatMessage` + `ChatRole` for the orchestrator's LLM call.

### Postgres

Phase 1 reads + writes against a Postgres 16 database. The connection string lives at `ConnectionStrings:Postgres` in configuration. The local-dev default in `appsettings.json` points at `localhost:5432` with placeholder credentials — override via `appsettings.user.json` (gitignored, excluded from publish output) or environment. EF Core migrations apply automatically on host startup when `Assistant:AutoMigrate=true` (default); set to `false` in environments where migrations run out-of-band.

**Late-resolution rule (load-bearing):** the connection-string lookup runs INSIDE the `AddDbContext` factory, not eagerly at builder time. Eager capture defeats `WebApplicationFactory<Program>`'s `ConfigureAppConfiguration` overlay — the test factory's in-memory connection string wouldn't reach an eagerly-captured field. Phase 5's JWT swap follows the same rule: see the comment block in `Program.cs` for the canonical `AddOptions<JwtBearerOptions>().Configure<IConfiguration>(...)` form.

## Configuration

`appsettings.json` carries:

- `Logging` — standard ASP.NET Core minimum-level config.
- `ConnectionStrings:Postgres` — local-dev default; QA reads from `/etc/trellis-assistant-qa.env` per the deploy bootstrap.
- `Assistant:Model` — LLM tag passed to `IOllamaClient.StreamChatAsync`. Phase 1 stub ignores it; Phase 2's real Ollama uses it (default `llama3.3:70b`).
- `Assistant:AutoMigrate` — `true` by default; flip to `false` to skip startup migration.
- `AllowedHosts` — standard.

No top-level `Urls` key (the `AppsettingsConventionsTests` pin enforces this — Kestrel binding lives in deploy scripts, not config). Operator-local Dev convenience goes in `appsettings.user.json` (gitignored, excluded from publish output via the canonical csproj rule from trainer #41 / web #14 / server #56).

## Auth

Phase 1 trusts two request headers on every `/api/*` request:

- `X-Trellis-Tenant-Id`
- `X-Trellis-User-Id`

Both must be present and non-blank or the request returns 401. The `TenantHeadersMiddleware` validates and stashes the trusted (tenant, user) tuple in `HttpContext.Items`; endpoints never read the headers directly. `/healthz` bypasses the middleware. Phase 5 swaps the middleware for JWT-claim extraction without endpoint changes — the seam is documented in both the middleware class doc and `Program.cs`.

## QA deploy

Steady-state QA redeploys: [`trellis-deploy/scripts/qa/Deploy-Assistant-Standalone.ps1`](https://github.com/terraverde-solutions/trellis-deploy/blob/main/scripts/qa/Deploy-Assistant-Standalone.ps1). Mirrors the trainer-qa + web-qa shape: publish → tar → scp → ssh-and-extract → systemctl restart → in-tunnel `/healthz` smoke. Public hostname `assistant-qa.chat.terraverdellc.com`, gated by Hetzner-side HTTP Basic auth using the shared `trellisqa` credential.

First-time bootstrap walkthrough: [`trellis-deploy/scripts/qa/bootstrap-assistant.md`](https://github.com/terraverde-solutions/trellis-deploy/blob/main/scripts/qa/bootstrap-assistant.md). Adds the `trellis_assistant_qa` Postgres role + database alongside the existing trainer-qa Postgres on GB10.

## What's NOT in this repo (yet)

Phase 1 is intentionally narrow. The following live in `59-trellis-assistant.md`'s roadmap and arrive in Phase 2+:

- Real Ollama client wiring (Phase 1 ships `StubLlmClient`; Phase 2 swaps to `Trellis.Core.Services.OllamaClient` backed by a named `HttpClient`)
- Streaming responses to the HTTP caller (Phase 1 buffers + returns full text)
- Channel adapters (Slack / WhatsApp / Telegram webhook handlers)
- Tool dispatch (MCP client, `send_message`, `search_documents`, etc.)
- Voice (TTS / STT, push-to-talk, wake word)
- Cross-surface memory + `IConversationStore` integration
- Identity / sandboxing / DM allowlist (Phase 5 — paired with JWT auth + Postgres Row-Level Security)
- Production deploy (`Deploy-Assistant-Prod.ps1` against Hetzner)

Don't add structure or interfaces speculatively — each phase's brief owns its own surface.

## Workflow

Per the project's git rules: feature branches off `main`; PR creation auto-approved; PR bodies include a checkbox list of test cases; user merges and deletes branches.

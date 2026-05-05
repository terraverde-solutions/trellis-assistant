# Trellis.Assistant

Always-on personal AI surface for the [TerraVerde Trellis system](https://github.com/terraverde-solutions/trellis-docs). Voice-first multi-channel presence, MCP-based tool dispatch, cross-surface memory.

The full design is in [`trellis-docs/MarkdownFiles/59-trellis-assistant.md`](https://github.com/terraverde-solutions/trellis-docs/blob/main/MarkdownFiles/59-trellis-assistant.md). Read that first.

**Status:** Phase 2 — real Ollama wiring landed. The orchestrator now calls `Trellis.Core.Services.OllamaClient` against a configured Ollama server (defaults to `http://localhost:11434/`), per-conversation model selection is a stored column, `/readyz` reports warm-up state, and the per-request timeout bumped from 60s to 180s for cold 70B model loads. Channel adapters, tool dispatch, and voice land in Phase 3+.

## Build / test / run

```bash
dotnet restore
dotnet build  --configuration Release
dotnet test   --configuration Release
dotnet run    --project Trellis.Assistant
```

`dotnet test` runs 26 tests across five files:

- `HealthCheckTests` — 3 tests: `/healthz` 200 + body shape (Phase 1) + `/readyz` toggle from 503 → 200 on `OllamaReadinessState.MarkReady()` (Phase 2).
- `Integration/ConversationEndpointTests` — 17 end-to-end tests against a Testcontainers-backed Postgres 16 with `IOllamaClient` overridden to `StubLlmClient`: create + read + append, role/position invariants, 401 on missing/blank tenant or user headers, 400 on malformed ulid + empty content, 404 on unknown conversation + cross-tenant + cross-user, 5-way concurrency test pinning the advisory-lock contract, and Phase 2 per-conversation model coverage (default + caller-supplied).
- `Integration/EFMigrationSmokeTests` — 4 tests pinning the migration's column shape via `information_schema` (now including the Phase 2 `model` column), FK ON DELETE CASCADE, channel `DEFAULT 'api'`, and model `DEFAULT 'mistral-small:24b'`.
- `Integration/RealOllamaSmokeTests` — 1 SkippableFact gated on `OLLAMA_BASE_URL`. Posts a single user turn against a real Ollama, asserts non-empty response + role-alternation + 240s upper bound. Skips cleanly when the env var is unset.
- `AppsettingsConventionsTests` — 2 tests: XML-shape pin on `Trellis.Assistant.csproj` (canonical `<None Remove>` + `<Content Remove>` exclude rule for `appsettings.user.json`) plus a publish-output integration test (`[Trait("Category", "Integration")]`).

The `Integration` tests need a running Docker daemon for Testcontainers; they self-skip via `[SkippableFact]` + an `IsDockerAvailable()` probe when Docker isn't reachable. On a CI runner without Docker the integration tests show as **Skipped** (not failed). The real-Ollama smoke skips additionally when `OLLAMA_BASE_URL` is unset. Use `--filter "Category!=Integration"` for the fast unit-only suite.

To run the real-Ollama smoke locally:

```bash
$env:OLLAMA_BASE_URL = "http://localhost:11434/"
$env:OLLAMA_TEST_MODEL = "mistral-small:24b"   # optional; default is mistral-small:24b
dotnet test --configuration Release --filter "FullyQualifiedName~RealOllamaSmokeTests"
```

Local Kestrel binding picks the default ASP.NET Core dev port (5000); QA + Production deploys pin the unit's `--urls` to 127.0.0.1:5117.

### Trellis.Core dependency

`Trellis.Assistant.csproj` references `..\..\core\Trellis.Core\Trellis.Core.csproj`. Clone the sibling [trellis-core repo](https://github.com/terraverde-solutions/trellis-core) into `c:\dev\core\` (the canonical worker-tree path) before building. Phase 2 consumes:

- `IAssistantConversationStore` + `AssistantConversation` (now carries `Model`) + `AssistantTurn` / `NewAssistantTurn` / `AssistantTurnRole` from `Trellis.Core.Services` (Phase 1 add, Phase 2 extension).
- `IOllamaClient` (Phase 1) + the production `OllamaClient` impl (Phase 2 — was the stub before).
- `ChatMessage` + `ChatRole` for the orchestrator's LLM call.

### Postgres

Phase 1+2 reads + writes against a Postgres 16 database. The connection string lives at `ConnectionStrings:Postgres` in configuration. The local-dev default in `appsettings.json` points at `localhost:5432` with placeholder credentials — override via `appsettings.user.json` (gitignored, excluded from publish output) or environment. EF Core migrations apply automatically on host startup when `Assistant:AutoMigrate=true` (default); set to `false` in environments where migrations run out-of-band.

Phase 2's migration adds `conversations.model varchar(64) NOT NULL DEFAULT 'mistral-small:24b'`. Existing rows from Phase 1 get backfilled to `mistral-small:24b` automatically; the column is otherwise immutable for Phase 2 (no re-pin endpoint).

**Late-resolution rule (load-bearing):** the connection-string lookup runs INSIDE the `AddDbContext` factory, not eagerly at builder time. The same rule applies to `Ollama:BaseUrl` — read inside the `AddTypedClient` factory's `Func<Uri>` lambda. Eager capture defeats `WebApplicationFactory<Program>`'s `ConfigureAppConfiguration` overlay. Phase 5's JWT swap follows the same pattern: see the comment block in `Program.cs` for the canonical `AddOptions<JwtBearerOptions>().Configure<IConfiguration>(...)` form.

### Ollama

Phase 2 wires `IOllamaClient` to the production `Trellis.Core.Services.OllamaClient` via `IHttpClientFactory`. The `Ollama:BaseUrl` config key controls the dial address; on GB10 production this is `http://127.0.0.1:11434/` (loopback — Assistant lives on the same box as Ollama). Per-conversation model selection (Phase 2 Q4=B): the model tag is pinned at create time, stored on the conversation row, and threaded through to `OllamaClient.StreamChatAsync` for every turn. Phase 2 ships no allowlist — invalid model tags surface as a 502-style error from Ollama on the first turn rather than a 400 at create time.

Cold 70B models take 60–90s to load into VRAM. The startup `OllamaWarmupHostedService` calls Ollama with a tiny prompt against `Assistant:WarmupModel` (default `mistral-small:24b`) so the model is warm by the time the first user turn lands. Failure mode: if Ollama is unreachable or the warm-up model isn't loaded, `/readyz` stays 503 indefinitely while the host logs retry warnings on a 10/30/60s backoff (capped at 60s to prevent hour-scale gaps); `/healthz` stays 200; user requests fall through lazily and pay the cold-load cost on first turn. Set `Assistant:WarmupModel` to empty string to disable warm-up entirely (dev-without-Ollama setups).

## Configuration

`appsettings.json` carries:

- `Logging` — standard ASP.NET Core minimum-level config.
- `ConnectionStrings:Postgres` — local-dev default; QA reads from `/etc/trellis-assistant-qa.env` per the deploy bootstrap.
- `Ollama:BaseUrl` — Ollama dial URL (Phase 2; default `http://localhost:11434/`).
- `Assistant:TurnRequestTimeoutSeconds` — wall-clock budget for one POST /turns request, in integer seconds (default 180; was hardcoded 60s in Phase 1). Read as int because `IConfiguration.Get<TimeSpan>()` is culture-dependent and a foot-gun for QA + production deploys.
- `Assistant:WarmupModel` — model tag for the startup warm-up call (default `mistral-small:24b`; empty disables warm-up).
- `Assistant:AutoMigrate` — `true` by default; flip to `false` to skip startup migration.
- `AllowedHosts` — standard.

No top-level `Urls` key (the `AppsettingsConventionsTests` pin enforces this — Kestrel binding lives in deploy scripts, not config). Operator-local Dev convenience goes in `appsettings.user.json` (gitignored, excluded from publish output via the canonical csproj rule from trainer #41 / web #14 / server #56).

## Auth

Phase 1+2 trusts two request headers on every `/api/*` request:

- `X-Trellis-Tenant-Id`
- `X-Trellis-User-Id`

Both must be present and non-blank or the request returns 401. The `TenantHeadersMiddleware` validates and stashes the trusted (tenant, user) tuple in `HttpContext.Items`; endpoints never read the headers directly. `/healthz` + `/readyz` bypass the middleware. Phase 5 swaps the middleware for JWT-claim extraction without endpoint changes — the seam is documented in both the middleware class doc and `Program.cs`.

## QA deploy

Steady-state QA redeploys: [`trellis-deploy/scripts/qa/Deploy-Assistant-Standalone.ps1`](https://github.com/terraverde-solutions/trellis-deploy/blob/main/scripts/qa/Deploy-Assistant-Standalone.ps1). Mirrors the trainer-qa + web-qa shape: publish → tar → scp → ssh-and-extract → systemctl restart → in-tunnel `/healthz` smoke. A sibling deploy PR adds a `/readyz` poll (120s budget) after the `/healthz` check so the deploy script declares "live" only after Ollama warm-up completes. Public hostname `assistant-qa.chat.terraverdellc.com`, gated by Hetzner-side HTTP Basic auth using the shared `trellisqa` credential.

First-time bootstrap walkthrough: [`trellis-deploy/scripts/qa/bootstrap-assistant.md`](https://github.com/terraverde-solutions/trellis-deploy/blob/main/scripts/qa/bootstrap-assistant.md). Adds the `trellis_assistant_qa` Postgres role + database alongside the existing trainer-qa Postgres on GB10.

## What's NOT in this repo (yet)

Phase 2 is intentionally narrow. The following live in `59-trellis-assistant.md`'s roadmap and arrive in Phase 3+:

- First tool: `search_documents` → Trainer integration (Phase 3)
- Streaming responses to the HTTP caller via SSE (Phase 3+ — Phase 2 buffers + returns full text)
- Channel adapters (Slack / WhatsApp / Telegram webhook handlers — Phase 4)
- Tool dispatch (MCP client, `send_message`, etc.)
- Voice (TTS / STT, push-to-talk, wake word)
- Cross-surface memory + `IConversationStore` integration
- Identity / sandboxing / DM allowlist (Phase 5 — paired with JWT auth + Postgres Row-Level Security)
- Production deploy (`Deploy-Assistant-Prod.ps1` against Hetzner)

Don't add structure or interfaces speculatively — each phase's brief owns its own surface.

## Workflow

Per the project's git rules: feature branches off `main`; PR creation auto-approved; PR bodies include a checkbox list of test cases; user merges and deletes branches.

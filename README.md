# Trellis.Assistant

Always-on personal AI surface for the [TerraVerde Trellis system](https://github.com/terraverde-solutions/trellis-docs). Voice-first multi-channel presence, MCP-based tool dispatch, cross-surface memory.

The full design is in [`trellis-docs/MarkdownFiles/59-trellis-assistant.md`](https://github.com/terraverde-solutions/trellis-docs/blob/main/MarkdownFiles/59-trellis-assistant.md). Read that first.

**Status:** Phase 0 scaffold. Just `WebApplication.CreateBuilder(args)` + a `/healthz` endpoint that returns `{"status":"ok"}`. Orchestrator, channel adapters, tool dispatch, and voice land in Phase 1+.

## Build / test / run

```bash
dotnet restore
dotnet build  --configuration Release
dotnet test   --configuration Release
dotnet run    --project Trellis.Assistant
```

`dotnet test` runs four tests: two `/healthz` integration tests, one publish-output integration test (`[Trait("Category", "Integration")]` — pin the canonical `appsettings.user.json` no-leak contract), and one XML-shape test on `appsettings.json`. Use `--filter "Category!=Integration"` for the fast unit-only suite.

Local Kestrel binding picks the default ASP.NET Core dev port (5000); QA + Production deploys pin the unit's `--urls` to 127.0.0.1:5117.

### Trellis.Core dependency

`Trellis.Assistant.csproj` references `..\..\core\Trellis.Core\Trellis.Core.csproj`. Clone the sibling [trellis-core repo](https://github.com/terraverde-solutions/trellis-core) into `c:\dev\trellis-kimi\core\` (or wherever your worker tree lives) before building. Phase 0 doesn't actually consume any `Trellis.Core` types yet — the reference is wired up so Phase 1 can pull in `BearerAuthHandler` / `ITenantIdProvider` / `IOllamaClient` without a project-shape change.

## Configuration

Phase 0's `appsettings.json` carries only `Logging` + `AllowedHosts`. No `Gateway:*`, no `Trainer:*`, no orchestrator config — those land in Phase 1 alongside the code that reads them. Operator-local Dev convenience goes in `appsettings.user.json` (gitignored, excluded from publish output via the canonical csproj rule from trainer #41 / web #14 / server #56).

## QA deploy

Steady-state QA redeploys: [`trellis-deploy/scripts/qa/Deploy-Assistant-Standalone.ps1`](https://github.com/terraverde-solutions/trellis-deploy/blob/main/scripts/qa/Deploy-Assistant-Standalone.ps1) (lands alongside this scaffold). Mirrors the trainer-qa + web-qa shape: publish → tar → scp → ssh-and-extract → systemctl restart → in-tunnel `/healthz` smoke. Public hostname `assistant-qa.chat.terraverdellc.com`, gated by Hetzner-side HTTP Basic auth using the shared `trellisqa` credential.

First-time bootstrap walkthrough: [`trellis-deploy/scripts/qa/bootstrap-assistant.md`](https://github.com/terraverde-solutions/trellis-deploy/blob/main/scripts/qa/bootstrap-assistant.md).

## What's NOT in this repo (yet)

Phase 0 is intentionally minimal. The following live in `59-trellis-assistant.md`'s roadmap and arrive in Phase 1+:

- Orchestrator (`IAssistantOrchestrator`)
- Channel adapters (Slack / WhatsApp / Telegram webhook handlers)
- Tool dispatch (MCP client, `send_message`, `search_documents`, etc.)
- Voice (TTS / STT, push-to-talk, wake word)
- Cross-surface memory + `IConversationStore` integration
- Identity / sandboxing / DM allowlist
- Production deploy (`Deploy-Assistant-Prod.ps1` against Hetzner)

Don't add structure or interfaces speculatively — Phase 1's brief owns the orchestrator surface.

## Workflow

Per the project's git rules: feature branches off `main`; PR creation requires explicit user approval; PR bodies include a checkbox list of test cases; user merges and deletes branches.

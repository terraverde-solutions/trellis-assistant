# Trellis Assistant (AI agent orientation)

Part of the **TerraVerde Trellis System**. Canonical system docs are in the
`trellis-docs` repo at <https://github.com/terraverde-solutions/trellis-docs>
(or locally at `c:\dev\trellis-kimi\docs\MarkdownFiles\` if you have the
sibling clone).

Read first:
- [`MarkdownFiles/50-trellis-for-llms.md`](https://github.com/terraverde-solutions/trellis-docs/blob/main/MarkdownFiles/50-trellis-for-llms.md) — system overview
- [`MarkdownFiles/59-trellis-assistant.md`](https://github.com/terraverde-solutions/trellis-docs/blob/main/MarkdownFiles/59-trellis-assistant.md) — Assistant design (the source of truth)
- [`MarkdownFiles/56-project-structure.md`](https://github.com/terraverde-solutions/trellis-docs/blob/main/MarkdownFiles/56-project-structure.md) — repo layout + sibling-repo project-reference convention

## What this component is

The always-on personal AI surface. Voice-first multi-channel presence. MCP-based
tool dispatch (`send_message`, `search_documents`, etc.). Cross-surface memory
across Desktop, Web, and Chat. Hosted inside Trellis Server on the customer
box (alongside Trainer + Workflow); deployable standalone for QA on GB10.

The component design lives in [`59-trellis-assistant.md`](https://github.com/terraverde-solutions/trellis-docs/blob/main/MarkdownFiles/59-trellis-assistant.md). This CLAUDE.md is the agent-side orientation: scope of the current
phase + guard-rails for what NOT to do yet.

## Status

**Phase 0 scaffold.** What exists:

- ASP.NET Core net10.0 minimal-API project (`Microsoft.NET.Sdk.Web`)
- A single `/healthz` endpoint returning `{"status":"ok"}`
- `Trellis.Assistant.Tests` xUnit project with 4 tests:
  - `Get_Healthz_Returns200`
  - `Get_Healthz_BodyHasStatusOk`
  - `DotnetPublish_DoesNotCopy_AppsettingsUserJson_IntoPublishOutput` (Integration trait)
  - `Appsettings_DoesNotPin_UrlsKey`
- `<None Remove>` + `<Content Remove>` for `appsettings.user.json` from day one (canonical pattern from trainer #41 / web #14 / server #56)
- ProjectReference to sibling `..\..\core\Trellis.Core\Trellis.Core.csproj` (wired but not consumed in Phase 0)
- Deploy infrastructure in [`trellis-deploy/scripts/qa/`](https://github.com/terraverde-solutions/trellis-deploy/tree/main/scripts/qa) (parallel PR): unit + env file + nginx vhosts (GB10 + Hetzner) + bootstrap walkthrough + `Deploy-Assistant-Standalone.ps1` with the post-deploy smoke wrapper

What does NOT exist (Phase 1+):

- Orchestrator (`IAssistantOrchestrator`)
- Channel adapters (Slack / WhatsApp / Telegram webhook handlers)
- Tool dispatch (MCP client, `send_message`, `search_documents`, etc.)
- Voice (TTS / STT, push-to-talk, wake word)
- Cross-surface memory / `IConversationStore` integration
- Identity / sandboxing / DM allowlist
- Production deploy (Hetzner)

## Solution layout

```
Trellis.Assistant.sln
├── Trellis.Assistant/
│   ├── Trellis.Assistant.csproj   Microsoft.NET.Sdk.Web, net10.0,
│   │                                ProjectReference Trellis.Core,
│   │                                canonical appsettings.user.json
│   │                                exclude rule (trainer #41 / web #14 /
│   │                                server #56 pattern)
│   ├── Program.cs                 minimal: WebApplication.CreateBuilder
│   │                                + /healthz + app.Run()
│   │                                + `public partial class Program {}`
│   │                                so WebApplicationFactory<Program>
│   │                                can host the app in-process for tests
│   └── appsettings.json           Logging + AllowedHosts; NOTHING else.
│                                  No "Urls" key. No "Gateway" key. No
│                                  "Trainer" key. Phase 1 adds those when
│                                  the code that reads them lands.
└── Trellis.Assistant.Tests/       xUnit + FluentAssertions +
    │                                Microsoft.AspNetCore.Mvc.Testing
    ├── HealthCheckTests.cs        WebApplicationFactory<Program> +
    │                                /healthz returns 200 + body
    ├── DotnetPublishTests.cs      [Trait("Category", "Integration")];
    │                                invokes real `dotnet publish` against
    │                                Trellis.Assistant.csproj + asserts
    │                                appsettings.user.json absent +
    │                                production appsettings.json IS
    │                                present (over-broad-Remove guard)
    └── AppsettingsConventionsTests.cs
                                    XML-inspection of appsettings.json;
                                    asserts no top-level "Urls" key
                                    (case-insensitive)
```

## Tech stack

- ASP.NET Core .NET 10 (`Microsoft.NET.Sdk.Web`)
- xUnit + FluentAssertions + `Microsoft.AspNetCore.Mvc.Testing` + coverlet (mirrors `trellis-web` / `trellis-trainer` test stacks)
- `Trellis.Core` consumed via sibling-repo project reference (`..\..\core\Trellis.Core\Trellis.Core.csproj`); same pattern as `trellis-web` / `trellis-server` / `trellis-desktop` (per `docs/56-project-structure.md`)

Phase 1+ will add:
- Channel-adapter SDKs (Slack / WhatsApp / Telegram)
- MCP client library (TBD; see 59-doc for the dispatch design)
- Voice toolchain (Whisper.net for STT — already in trainer for audio ingestion; TTS choice TBD)
- Identity (likely the same OIDC posture as the rest of the system once it lands)

## Build / test / run

```bash
dotnet restore
dotnet build Trellis.Assistant.sln --configuration Release
dotnet test  Trellis.Assistant.sln --configuration Release
dotnet run   --project Trellis.Assistant
```

Fast suite (skips the publish-output integration test):

```bash
dotnet test Trellis.Assistant.sln --configuration Release --filter "Category!=Integration"
```

## QA deploy

Steady-state QA redeploys: `trellis-deploy/scripts/qa/Deploy-Assistant-Standalone.ps1` (sibling repo, parallel PR). Mirrors the trainer-qa + web-qa shape: publish → tar → scp → ssh-and-extract → systemctl restart → in-tunnel `/healthz` smoke from the deploy box.

- Public hostname: `assistant-qa.chat.terraverdellc.com`
- Loopback bind: `127.0.0.1:5117`
- Unit: `trellis-assistant-qa.service`
- Auth: shared `trellisqa` HTTP Basic credential at the Hetzner edge (same as web-qa + trainer-qa); GB10-side nginx is auth-free
- Bootstrap walkthrough: `trellis-deploy/scripts/qa/bootstrap-assistant.md`

The deploy script ends with a real HTTP probe via `curl -s -o /dev/null -w '%{http_code}' -m 5 http://127.0.0.1:5117/healthz` over the SSH session and exits non-zero (after dumping 30 lines of `journalctl -u trellis-assistant-qa`) if the unit isn't responsive within 30 s. `systemctl status active(running)` alone isn't enough — that's the lesson from the trainer-qa bootstrap day.

## Don't (Phase 0)

- **Don't add an orchestrator skeleton, interface, or DI seam.** Phase 1's brief owns that surface; pre-empting it locks in shape decisions before the design conversation has happened. If you find yourself reaching for `IAssistantOrchestrator`, stop.
- **Don't add channel adapters.** Slack / WhatsApp / Telegram webhook handlers all live in Phase 1+. Adding them now would force premature decisions about identity, payload shape, and tool dispatch.
- **Don't add tool dispatch / MCP plumbing.** Same reasoning — the tool surface is Phase 1+'s scope.
- **Don't add voice surface.** TTS / STT / push-to-talk / wake-word — all Phase 2+.
- **Don't add `appsettings.user.json` to the repo.** Gitignored. Operator-local Dev convenience only. The csproj's `<None Remove>` + `<Content Remove>` rules ensure it never rides into a publish bundle even if it appears in the source tree (the same canonical defense from trainer #41 / web #14 / server #56).
- **Don't pin a top-level `"Urls"` key in `appsettings.json`.** Defense-in-depth against the trainer-qa bootstrap-day port-binding bug. Dev port pinning lives in launchSettings.json (none yet — Phase 1 adds when needed); deploy port pinning lives in the systemd unit's `--urls` + `Environment=ASPNETCORE_URLS` directives.
- **Don't add `Gateway:*` / `Trainer:*` config sections.** Phase 0 has no code that consumes them. Add them in Phase 1 alongside the consumers.
- **Don't fork wire types or auth handlers from `Trellis.Core`.** The project reference is already wired; consume the canonical types when Phase 1 needs them.
- **Don't bind ports outside 5117 without coordinating with the box's other QA services.** Trainer is on 5114, Web is on 5116, Gateway is on 5111 (see [`31-qa-environment.md` port table](https://github.com/terraverde-solutions/trellis-docs/blob/main/MarkdownFiles/31-qa-environment.md)).

## Cross-component changes

Phase 0 doesn't touch any cross-component contract. Phase 1+ will:
- Tool dispatch hits Trellis.Server's chat-proxy + Trellis.Trainer's `/api/search`
- Cross-surface memory shares an `IConversationStore` shape with Trellis.Web (DB-backed once Web's persistence lands)
- Voice transcription likely reuses the trainer's Whisper.net path

Use the **Feature Orchestrator** persona in trellis-docs for any change crossing those boundaries.

## Workflow

Per the project's git rules: feature branches off `main`; PR creation requires explicit user approval; PR bodies include a checkbox list of test cases; user merges and deletes branches.

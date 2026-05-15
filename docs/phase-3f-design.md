# Phase 3.F design — per-tenant tool exposure gating

**Status:** scaffolded on `kimi/phase3f-per-tenant-tool-gating`. Pairs with Phase 3.E (PR #12, merged 2026-05-14) which shipped multi-tool dispatch. With two production tools (`search_documents` + `chat_recent`) both unconditionally exposed and more tools planned, Phase 3.F lands the gating mechanism BEFORE the next tool ships so we don't paint into a corner.

## Three baseline corrections from hub's brief

The brief referenced three artifacts that didn't exist in the codebase; pre-scaffold ratify resolved each:

| Brief said | Actual state | Resolution |
|---|---|---|
| `AgentToolTenancy` struct exists (Phase 3.D) | Phase 3.D ratified Option 1a — `UserId : string` added directly to `AgentToolInput`, NOT a Tenancy record | Created `AgentToolTenancy` as a NEW local record in `Trellis.Assistant.AgentExecution` (Option B from Q1 ratify). Single-consumer rule — lift to Core when Workflow/Server needs the same shape. |
| `tenant_role` claim is extracted from `HttpContext.User` | `TenantClaimsMiddleware` reads `tenant_id` + `sub` only; no `tenant_role` extraction | Extended `TenantClaimsMiddleware` to read `tenant_role` claim + `X-Trellis-Tenant-Role` deprecated-header fallback. Stashed in `HttpContext.Items[TenantRoleKey]` as nullable string (Q2 Option A ratify). |
| `IToolRegistry.GetAllExposed()` method exists | Current surface: `Descriptors` property + `GetTool(name)` only | Added `GetExposedDescriptorsFor(AgentToolTenancy)` returning filtered descriptor list. Existing `Descriptors` property preserved unchanged for the standalone path (Q3 ratify; pin #6: untenanted standalone path bypasses policy). |

## Seven ratified pins

| # | Pin | Implementation |
|---|---|---|
| 1 | Default behavior preserved: no PerTool config → all tools exposed | `DefaultToolExposurePolicy` returns `true` when no rule matches a tool name. Back-compat with Phase 3.E (no PerTool entries today = unchanged behavior). |
| 2 | EchoTool stays gated by existing `ExposeEcho` boolean | Policy special-cases `tool.Descriptor.Name == EchoTool.ToolName` BEFORE consulting PerTool. Listing "echo" in PerTool is harmless but ignored. |
| 3 | `AllowedTenants` is a `List<Guid>` | Matches the JWT's `tenant_id` claim shape (uuid-format string per Phase 3.A C1; parsed to Guid by the orchestrator before policy invocation). |
| 4 | `RequiredRole` is a single string (not a list) | Macro 2 ships single-role-per-user. Ordinal case-sensitive compare. Future multi-role upgrade is a config-shape change. |
| 5 | Filter at `IToolRegistry` boundary, not in executor | `ToolRegistry.GetExposedDescriptorsFor(tenancy)` invokes the policy per tool, returns filtered descriptor list. Executor's catalog construction unchanged from 3.E. |
| 6 | Standalone path (`Descriptors` property) preserves untenanted behavior | `Descriptors` filters by `ExposeEcho` ONLY (Phase 3.C); does NOT consult Phase 3.F policy. `POST /api/agent-runs` operator-facing runs see the full catalog. |
| 7 | Denial logging at `LogDebug` | Gating is normal operation, not alert-worthy. Format: `"Tool {Name} hidden from tenant {TenantId} (role={Role}, reason={Reason})"`. |

## Policy logic

```csharp
public bool IsExposedTo(IAgentTool tool, AgentToolTenancy tenancy)
{
    // 1. EchoTool special case: ExposeEcho only.
    if (tool.Descriptor.Name == EchoTool.ToolName)
        return opts.ExposeEcho;

    // 2. No rule → default exposed (pin #1).
    if (!opts.PerTool.TryGetValue(name, out var rule))
        return true;

    // 3. AllowedTenants gate (AND with role gate below).
    if (rule.AllowedTenants.Count > 0
        && !rule.AllowedTenants.Contains(tenancy.TenantId))
        return false;

    // 4. RequiredRole gate. Fail-closed on null role + non-null
    //    RequiredRole (Macro 2 PR 6.7 missing-claim defense).
    if (!string.IsNullOrEmpty(rule.RequiredRole))
    {
        if (string.IsNullOrEmpty(tenancy.TenantRole))
            return false;
        if (!string.Equals(tenancy.TenantRole, rule.RequiredRole, StringComparison.Ordinal))
            return false;
    }

    return true;
}
```

## Cross-cutting changes

- **`TenantClaimsMiddleware`** — new `TenantRoleClaimName = "tenant_role"`, `TenantRoleHeaderName = "X-Trellis-Tenant-Role"`, `TenantRoleKey = "trellis.tenant_role"` constants. JWT path: `context.User.FindFirst(TenantRoleClaimName)?.Value` stashed in `HttpContext.Items`. Deprecated-header path: read `X-Trellis-Tenant-Role`, attach to synthesized `ClaimsPrincipal`, stash in `HttpContext.Items`. Permissive — null when neither source has the claim/header.
- **`ConversationOrchestrator.HandleUserTurnAsync`** — new optional `tenantRole : string?` param. Default route resolves agent descriptors via `_toolRegistry.GetExposedDescriptorsFor(tenancy)` when tenantId parses as Guid.
- **`ConversationEndpoints.AppendTurnAsync`** — forwards `(string?)HttpContext.Items[TenantRoleKey]` into the orchestrator call.
- **`ToolRegistry`** — ctor takes `IToolExposurePolicy`. `GetExposedDescriptorsFor(tenancy)` invokes policy per registered tool, returns filtered descriptor list. `Descriptors` property unchanged (still uses `IsExposedUntenanted` private helper that knows only `ExposeEcho`).
- **`TestAuthenticationHandler`** — synthesizes `tenant_role` claim from `X-Trellis-Tenant-Role` header when present. Test factory short-circuits the JWT path; tests that exercise the role gate need the claim attached at the handler.

## Dispatch is intentionally untenanted (defense-in-depth deferred)

`IToolRegistry.GetTool(name)` is NOT tenancy-gated. The catalogue-only gate (`GetExposedDescriptorsFor`) is the v0 protection: an LLM that never sees a tool can't emit a tool_call for it. Schema validation gate, AgentStep audit log, and the orchestrator's filter-route pre-resolution all stack on top.

A dispatch-time tenancy check would be defense-in-depth against post-jailbreak attempts (LLM somehow learns about a hidden tool and emits a tool_call). Phase 3.G+ if real evidence surfaces. For v0, catalogue-only is sufficient + matches OAI/Anthropic function-calling shape (the LLM "knows" only what's in the tools array).

## Macro 2 emission status

The `tenant_role` JWT claim may or may not be emitted by Macro 2's QA token issuer today. Phase 3.F's middleware is **permissive on null** — when the claim is absent, `tenancy.TenantRole = null`; policy fails-closed on any `RequiredRole` gate (the tool is hidden). This is the correct safety posture regardless of upstream emission: a tool config that requires a role and a request without one → tool hidden. RequiredRole gates with an unset upstream `tenant_role` claim cause the tool to be hidden **at runtime per request** — the LLM doesn't see the tool in its catalogue on any call. The misconfiguration is runtime-visible, not boot-time-detectable; there is no startup validation against the upstream JWT contract (the Assistant has no knowledge of which claims Macro 2 emits). Operators inspecting Debug logs see the `Tool {Name} hidden ... reason=RequiredRole={Required} but request carries no role claim` line.

When Macro 2 starts emitting `tenant_role` (or when operators add `X-Trellis-Tenant-Role` headers to legacy callers during the deprecation window), `RequiredRole` rules become functional without any Assistant-side change.

## Test plan

- **`DefaultToolExposurePolicyTests`** — 13 pure-unit pins:
  - EchoTool ExposeEcho gate (false → hidden; true → exposed)
  - No PerTool rule → default exposed
  - AllowedTenants empty list / in list / not in list
  - RequiredRole null / match / mismatch / null-role-fail-closed / case-sensitive
  - Combined AllowedTenants + RequiredRole AND combinations
  - "echo" in PerTool config ignored in favor of ExposeEcho
- **`ToolRegistryTests`** +4 Phase 3.F pins:
  - `GetExposedDescriptorsFor_DelegatesToPolicy_ReturnsFilteredList`
  - `GetExposedDescriptorsFor_DifferentTenancies_DifferentLists`
  - `GetExposedDescriptorsFor_EmptyResult_ReturnsEmptyListNotNull`
  - `Descriptors_UntenantedPath_BypassesPolicy` (pin #6 verification)
- **E2E in `ConversationEndpointTests`** — 3 Docker-gated pins:
  - `PostTurns_RoleGatedTool_HiddenWhenRoleClaimAbsent` — chat_recent gated by RequiredRole=admin; null-role caller doesn't see it
  - `PostTurns_RoleGatedTool_VisibleWhenRoleMatches` — admin caller sees it
  - `PostTurns_AllowedTenantsGate_HidesToolFromNonAllowedTenant` — TenantB hidden when AllowedTenants=[TenantA]
- **Existing tests retrofitted** — ToolRegistry ctor sites + AssistantAgentExecutor's NewExecutor helper updated to pass an `AllowAllExposurePolicy` stub (preserves pre-Phase-3.F behavior for tests that don't specifically exercise gating).

## Out of scope (deferred to 3.G+)

- Per-user (within tenant) gating
- Tool-specific quota limits (call counts per tenant)
- Time-of-day or environment-based gating
- A real management UI for editing PerTool config (config file edit only in 3.F)
- Dispatch-time tenancy check (defense-in-depth)
- Multi-role-per-user (`RequiredRoles: ["admin", "owner"]`)
- Migration of `AgentToolTenancy` to `Trellis.Core` (single-consumer rule until Workflow/Server needs it)
- **Orchestrator non-UUID tenantId double-parse cleanup** — `ConversationOrchestrator.HandleUserTurnAsync` parses tenantId once via `Guid.TryParse` to decide between tenancy-aware and untenanted descriptor paths; the downstream `HandleAgentPathAsync` parses it again to derive `orgId`, throwing on non-UUID inputs (the existing Phase 3.A C1 contract path → 400 Bad Request at the endpoint). Not a security defect — the agent-path 400 still fires for malformed tenants. But the double-parse is wasted work + the untenanted fallback in Path 1 silently bypasses the Phase 3.F policy for the brief moment before the 400 surfaces. Phase 3.G could move the UUID-parse validation up to the `TenantClaimsMiddleware` boundary (reject non-UUID tenant_ids at 400 before they reach the orchestrator at all). Forward-flag for the next phase.

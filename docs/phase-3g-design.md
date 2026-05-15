# Phase 3.G design — middleware-level UUID validation for tenantId

**Status:** scaffolded on `kimi/phase3g-tenant-uuid-at-middleware`. Closes the Phase 3.F forward-flag: orchestrator's untenanted-fallback branch silently bypassed the Phase 3.F per-tenant policy for the brief window before the agent-path 400 surfaced downstream. Phase 3.G moves the UUID validation up to `TenantClaimsMiddleware`, makes the malformed-tenant case unreachable in the orchestrator, and collapses redundant `Guid.TryParse + defensive 400` branches at downstream endpoints.

## Brief baseline corrections

The brief assumed four artifacts in shapes that didn't match the codebase. Pre-scaffold ratify resolved each:

| Brief | Actual | Resolution |
|---|---|---|
| `HttpContext.Items` stores `Guid` not `string` (pin #3) | `IAssistantConversationStore` in Trellis.Core takes `string tenantId` across every method (cross-repo contract; 5+ endpoint consumers cast to string today) | **Deviated from pin #3.** Kept `string` in HttpContext.Items. Middleware validation gives downstream consumers the "string is UUID-parseable" guarantee, which is what they actually need. Flipping to Guid would require either a cross-repo Core widening OR `.ToString("D")` at every store call (net more code, no real safety gain). See "Pin #3 deferral" below. |
| "Standalone path doesn't go through TenantClaimsMiddleware" (pin #4) | Every `/api/*` route goes through the middleware. The standalone path's `AnonymousStandaloneUserId` sentinel is on `AgentToolInput.UserId` (Phase 3.D), not tenant-side. | Pin #4 dropped — no action needed. Standalone path also benefits from the UUID validation. |
| Existing `TenantClaimsMiddlewareTests.cs` to extend | No such file; middleware coverage lives in integration tests today (`ConversationEndpointTests` exercises it indirectly via the X-Trellis-* header path) | New pins added to `ConversationEndpointTests` (the natural home given the WebApplicationFactory-based integration shape). Dedicated middleware test class is deferred — would require a new fixture pattern. |
| "Drop the double-parse" | Orchestrator parses at 2 sites: routing-decision (Phase 3.F line 166) + `HandleAgentPathAsync` orgId derivation. Plus 1× at `AgentRunEndpoints.cs:96` (defensive 400). Total: 3 parses; all redundant once middleware validates. | All 3 collapsed to `Guid.Parse` (middleware guarantees parseability). The defensive 400 fallback at AgentRunEndpoints becomes unreachable for the UUID-malformed case → deleted. |

## What landed

**Middleware** (`TenantClaimsMiddleware.cs`):
- JWT-path validation: after extracting `tenant_id` claim, `Guid.TryParse` → reject 401 if non-UUID. Same path for missing claim (existing behavior preserved).
- Deprecated-header-path validation: same `Guid.TryParse` gate on `X-Trellis-Tenant-Id` header (pin #5). Legacy callers can't bypass the contract by virtue of being on the deprecated path.
- `Write401Async` upgraded from bespoke `{"error": "..."}` JSON to RFC 7807 ProblemDetails (`application/problem+json` content type, fields: `type`, `title`, `status`, `detail`). Existing 401 tests assert status code only; body upgrade is back-compat-safe at the wire (pin #2).

**Orchestrator** (`ConversationOrchestrator.cs`):
- Default route's `Guid.TryParse + else { agentDescriptors = _toolRegistry.Descriptors; }` untenanted-fallback branch DELETED. This was the actual security gap Phase 3.F forward-flagged.
- Collapsed to `Guid.Parse(tenantId)` (known-safe — middleware validates upstream).
- `HandleAgentPathAsync`'s `Guid.TryParse + throw` collapsed to `Guid.Parse + Guid.Empty defense`. The throw stays as a separate concern: middleware validates UUID **shape** but not Guid.Empty (a UUID-shaped string of all zeros is syntactically valid).

**AgentRunEndpoints** (`AgentRunEndpoints.cs`):
- Defensive `Guid.TryParse + 400-fallback` branch collapsed to `Guid.Parse` (known-safe). The 400 case for "tenant_id is not a valid uuid" becomes unreachable; deleted. `Guid.Empty` defense preserved.

## Pin #3 deferral

Hub's pin #3 said: "HttpContext.Items stores `Guid` not `string` — orchestrator + downstream type-cast directly."

Deferring this because the actual win is not at the HttpContext boundary (which is already safe via middleware validation post-3.G), but at the downstream call sites — and those sites all cross the `IAssistantConversationStore` (Trellis.Core) boundary which takes `string tenantId` across every method. Flipping HttpContext.Items to Guid means every endpoint becomes:

```csharp
var tenantIdGuid = (Guid)context.Items[TenantClaimsMiddleware.TenantIdKey]!;
var tenantIdString = tenantIdGuid.ToString("D");  // for the store call
store.X(tenantIdString, ...);
```

…which is MORE code than the current `(string)Items[...]` cast + direct pass-through. The Guid stash provides type-safety at the HttpContext boundary but loses it across the store boundary anyway.

The real cleanup needs a cross-repo Core PR widening `IAssistantConversationStore` to take `Guid tenantId`. That ripples through Workflow/Server/Desktop consumers and is out of Phase 3.G scope. **Pin #3 forward-flagged for a future cross-repo cleanup.**

## Wire-shape changes (operator-visible)

| Pre-3.G | Post-3.G |
|---|---|
| Non-UUID tenant_id → 200 from middleware → 400 from endpoint OR orchestrator throw OR untenanted-fallback descriptor leak | Non-UUID tenant_id → **401 from middleware** with `application/problem+json` body |
| Missing claim → bespoke `{"error": "..."}` JSON 401 | Missing claim → RFC 7807 ProblemDetails 401 |
| Bespoke error JSON at content-type `application/json` | RFC 7807 ProblemDetails at content-type `application/problem+json` |

Existing 401 tests assert status code only — wire-shape upgrade is back-compat-safe. The 400→401 change is the only behavioral status-code shift; affects callers that explicitly sent non-UUID tenant_ids (i.e., misconfigured callers — production callers are already on uuid-shaped tenants per Phase 3.A C1).

## Test plan

- **`PostConversations_NonUuidTenantId_Returns401_FromMiddleware`** — `[Theory]` with 4 inline non-UUID forms (not-a-uuid, 12345, abcdef, zzz-flavored fake). Pins middleware-level 401.
- **`PostConversations_NonUuidTenantId_ResponseShape_IsProblemDetails`** — asserts `application/problem+json` content type + RFC 7807 fields in the body.
- **`PostConversations_NonUuidTenantId_DeprecatedHeaderPath_AlsoReturns401`** — pin #5: deprecated `X-Trellis-Tenant-Id` callers get the same gate.
- **`PostAgentRuns_NonUuidTenantId_Returns401_FromMiddleware`** (retrofit; was `_Returns400`) — assertion flipped from 400 to 401 since the middleware now rejects upstream.
- **`PostTurns_AgentPath_NonUuidTenant_Returns401_FromMiddleware`** (retrofit; was `_Returns400`) — same.
- **Existing 233 tests preserved** — no behavioral change for UUID-valid paths.

## Out of scope (deferred)

- Cross-repo `IAssistantConversationStore` widening to `Guid tenantId` (pin #3 real fix)
- Removing the deprecated `X-Trellis-Tenant-Id` header path entirely
- Per-tenant rate limiting at middleware (Phase 3.H+)
- Dedicated `TenantClaimsMiddlewareTests` class with isolated fixture (current integration coverage is sufficient)

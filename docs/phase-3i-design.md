# Phase 3.I design — outcome=cancelled OTel pin + WorkflowScheduleTool

**Status:** scaffolded on `kimi/phase-3i-cancellation-pin-plus-workflow-schedule-tool`. **PR #16 fix-up applied** post-review: client-credentials token issuer added; verbatim user-JWT forwarding removed (was the 401-every-call defect); 3 test gaps closed. Two threads bundled into one PR:

- **Thread 1 (small):** test-only pin closing the Phase 3.H non-blocking suggestion — verifies that mid-dispatch cancellation records `outcome=cancelled` on the metric (distinct from `failed` / `succeeded` / `budget_exhausted`) AND leaves the failure counter empty. Cancellation is operator-driven (caller dropped the token), not a tool fault — conflating it with failure would trip failure-ratio dashboards on every user disconnect.
- **Thread 2 (new tool):** third production tool. `WorkflowScheduleTool` lets the LLM schedule a workflow run via trellis-workflow's `POST /api/workflows/runs` (qwen Phase 4.A). Schedule-by-id branch ONLY — the inline-JSON branch takes a full `WorkflowDefinitionJson` blob an LLM has no business emitting (hallucination + injection risk per the brief).

## Thread 1 — cancellation-outcome pin

`AssistantAgentExecutor.DispatchOneToolCallAsync` already emits the cancelled outcome at the right path: when the tool's `RunAsync` throws `OperationCanceledException` mid-flight AND the caller's CT is requested, `EmitDispatchMetrics(..., Outcomes.Cancelled, ...)` fires. Activity status stays `Unset` (cancellation isn't an error condition). Phase 3.H added this path; what was missing was a direct pin verifying the wire-shape.

The pin is `ToolDispatch_CancellationToken_RecordsOutcomeCancelled` in `AgentTelemetryTests.cs`. Setup:

- A `HangingTool` that calls `_onEntered?.Invoke()` then awaits `Task.Delay(10s, ct)`. The `onEntered` callback fires `cts.Cancel()`. Deterministic synchronization avoids the race where a `CancelAfter` timer fires before the executor reaches `DispatchOneToolCallAsync` — in that race the LLM-call OCE path catches the cancellation BEFORE the dispatch metric site, and the test would always see 0 dispatches (the actual failure when an earlier draft used `CancelAfter(75ms)`).
- Assertions:
  - `ToolDispatchCount` fires exactly once with `outcome=cancelled` (NOT `succeeded` NOT `failed` NOT `budget_exhausted`).
  - `ToolDispatchFailureCount` is empty (cancellation NOT counted as a tool failure).
  - Activity status is `ActivityStatusCode.Unset` (matches Phase 3.H pin: cancellation surfaces as an outcome tag, not an Activity error status).

## Thread 2 — WorkflowScheduleTool

### Wire shape (schedule-by-id branch only)

LLM-emitted tool_call shape:

```json
{
  "tool_call": {
    "name": "workflow_schedule",
    "arguments": "{\"workflow_definition_id\":\"<guid>\",\"initial_input_json\":{...}}"
  }
}
```

`workflow_definition_id` (required, format=uuid) and an optional `initial_input_json` (object). The schema rejects the inline-JSON branch (`workflow_definition_json: string`) by absence — the LLM never sees that field.

HTTP request to qwen:

```
POST /api/workflows/runs
Authorization: Bearer <propagated user JWT>
Content-Type: application/json

{
  "workflowDefinitionId": "<guid>",
  "initialInputJson": { ... }    // omitted when not supplied
}
```

201 Created response (qwen Phase 4.A canonical shape):

```json
{ "id": "<guid>", "status": 1 }
```

The LLM sees this body verbatim via `AgentToolOutput.ResultJson`.

### Failure mapping → LLM-readable envelope

`HttpWorkflowClient` classifies qwen's response into one of four `WorkflowScheduleErrorCode` values. The tool builds a structured envelope per code so the LLM can pattern-match:

| qwen status | ErrorCode | LLM envelope | LLM retry posture |
|---|---|---|---|
| 201 Created (or 200) | _success_ | qwen body verbatim | (success path) |
| 401 Unauthorized | `AuthFailed` | `{error: "auth_failed", message, workflow_definition_id}` | DO NOT retry — credentials issue |
| 404 Not Found | `DefinitionNotFound` | `{error: "definition_not_found", message, workflow_definition_id}` | ask user for different id |
| Other 4xx | `BadRequest` | `{error: "bad_request", message: <ProblemDetails.detail>, workflow_definition_id}` | LLM reads detail + decides |
| 5xx / transport / timeout | `Transient` | `{error: "transient", message, workflow_definition_id}` | retry-with-backoff or surface to user |

`ErrorMessage` (operator-facing, AgentStep.ErrorMessage column) carries the same description in unstructured form. The LLM-visible structured envelope lives in `ResultJson`.

### Auth (fix-up: client-credentials token exchange)

**Original (broken) shape:** verbatim-forward the inbound user JWT to qwen. Defect: the user JWT carries `aud=trellis-assistant` but qwen validates `aud=trellis-workflow` — every call 401s. Caught by hub review against `appsettings.json:17`, `Trellis.Workflow.Api/Program.cs:35,58-59`, and `HttpWorkflowClient.cs:275-300`.

**Fixed (Path A) shape:** the Assistant mints its own service token per outbound call via OAuth2 client-credentials.

- `IInternalTokenIssuer` (`Services/IInternalTokenIssuer.cs`) — one method: `GetAccessTokenAsync(targetResource, ct)`. Per-resource cache keyed on `targetResource`, refresh-skew window before `exp` (default 60s margin), stampede control via per-resource `SemaphoreSlim`. Failures surface as `InternalTokenIssuanceException`.
- `OpenIddictInternalTokenIssuer` — production implementation. POSTs `grant_type=client_credentials&client_id=…&client_secret=…&resource=<aud>` to `Assistant:Auth:InternalClient:TokenEndpoint`. Parses `{ access_token, expires_in, token_type }`. Caches until `exp - RefreshSkewSeconds`. Singleton in DI (cache must persist) — registered via named-HttpClient + manual singleton factory matching Trellis.Core's `JwtClientCredentialsTokenStore` pattern (Macro 2 PR 6).
- `HttpWorkflowClient`:
  - Mints `aud=trellis-workflow` token via `_tokenIssuer.GetAccessTokenAsync(TargetResource, ct)`.
  - Sets outbound `Authorization: Bearer <minted-token>`.
  - Reads `tenant_id` claim from `HttpContext.User`; throws if absent (the tool is only meaningful from a JWT-authenticated agent path).
  - Forwards the tenant identity out-of-band as `X-Trellis-Tenant-Id: <claim>`.
- Token-issuance failures (Auth unreachable / 5xx / rejected credentials) surface to the LLM as `WorkflowScheduleErrorCode.AuthFailed` — operator-actionable, NOT a transient the LLM should retry-loop against.

Config bound from `Assistant:Auth:InternalClient`:
```json
{
  "TokenEndpoint": "http://127.0.0.1:5119/connect/token",
  "ClientId": "trellis-assistant-internal",
  "ClientSecret": "",
  "RequestTimeoutSeconds": 5,
  "RefreshSkewSeconds": 60
}
```

Production deploys override `ClientId` + `ClientSecret` via env var (`Assistant__Auth__InternalClient__ClientId` / `…__ClientSecret`); secrets MUST NOT be inlined in appsettings.json.

**Forward-flag (paired qwen brief required for production functionality):** qwen's `TenantClaimsMiddleware` currently reads tenant_id ONLY from the JWT claim. The `X-Trellis-Tenant-Id` header forwarding is meaningless until qwen accepts it. Hub is drafting a paired qwen brief adding header-based tenant_id support gated on the CC client_id (only honored when the request comes from a known internal client like `trellis-assistant-internal`). The Assistant-side change is shipped under the assumption qwen-side acceptance lands separately; until then, the production wire is auth'd correctly (qwen validates the service token) but tenant context isn't yet plumbed at qwen's middleware.

### Exposure gating (opt-in)

`ToolCatalogueOptions.ExposeWorkflowSchedule` boolean (mirrors `ExposeEcho` precedent). Defaults `false` in production — the tool is side-effecting (kicks off real workflow runs) and untested in prod. Opt-in flag pattern lets QA validate before flipping default on.

`DefaultToolExposurePolicy` special-cases `WorkflowScheduleTool.ToolName` against the flag. When the flag is `false`, the tool is hidden regardless of PerTool config. When `true`, the policy falls through to the existing PerTool path so per-tenant `AllowedTenants` + `RequiredRole` gating layers on top. Pinned by `WorkflowScheduleTool_ExposeTrue_PerToolGateStillApplies`.

Test environments override via `WebApplicationFactory` config overlay. Standalone `POST /api/agent-runs` operator path bypasses the policy (Phase 3.F pin #6 — `IToolRegistry.Descriptors` is untenanted by design).

### Category: Act

`AgentToolCategory.Act` — side-effecting. The planner LLM is steered (via Core's `AgentToolCategory` docstring) to prefer Search/Inspect tools when investigating and Act when committing, which discourages speculative chained schedule calls.

## Brief baseline + decisions

| Brief | Actual | Resolution |
|---|---|---|
| File path `Trellis.Assistant/Tools/WorkflowScheduleTool.cs` | Existing tools live in `AgentExecution/` (SearchDocumentsTool + ChatRecentTool + EchoTool) | Placed in `AgentExecution/` to match the established layout; same for `Services/HttpWorkflowClient.cs` matching `HttpSearchClient.cs` |
| First-round: "verbatim user-JWT forwarding via IHttpContextAccessor" | User JWT carries `aud=trellis-assistant`; qwen validates `aud=trellis-workflow`. Verbatim forwarding 401s every call. | **Fix-up applied:** replaced with client-credentials token exchange (see Auth section). Assistant mints `aud=trellis-workflow` token; tenant identity forwarded out-of-band as `X-Trellis-Tenant-Id`. |
| `WorkflowScheduleResponse.cs` DTO | Brief said "return as JsonElement unchanged" — pass-through (a) per Phase 3.B precedent obsoletes the DTO | DTO not created; the tool returns the body bytes verbatim via `AgentToolOutput.ResultJson`. |

## Wire-shape changes (operator-visible)

| Pre-3.I | Post-3.I |
|---|---|
| LLM has no workflow_schedule tool | LLM has `workflow_schedule` when `ExposeWorkflowSchedule=true` (default false) |
| No outbound calls to trellis-workflow | Outbound POST /api/workflows/runs when the LLM dispatches workflow_schedule |
| Cancellation outcome metric path existed but had no direct test pin | New pin verifies `outcome=cancelled` + failure counter stays empty |

No status-code changes; no breaking API changes. Phase 3.I is additive-only.

## Test plan

**Cancellation pin** (1):
- `ToolDispatch_CancellationToken_RecordsOutcomeCancelled` — pinned per Thread 1 brief.

**Fix-up: token-issuer pin tests** (5 in `OpenIddictInternalTokenIssuerTests.cs`):
- `GetAccessTokenAsync_HappyPath_CachesUntilExpiry` — first call mints, second call within skew returns cached.
- `GetAccessTokenAsync_AfterExpirySkew_RefreshesAutomatically` — FakeTimeProvider advanced past `exp - skew` triggers fresh mint.
- `GetAccessTokenAsync_AuthEndpointReturns500_ThrowsInternalTokenIssuanceException` — 5xx maps to throw.
- `GetAccessTokenAsync_AuthEndpointTransportError_ThrowsInternalTokenIssuanceException` — transport failure maps to throw.
- `GetAccessTokenAsync_ConcurrentCallsDuringRefresh_OnlyOneOutboundCall` — 32 concurrent callers serialize through per-resource semaphore, exactly 1 outbound mint.

**Fix-up: HttpWorkflowClient auth/tenant pin tests** (3 new in `HttpWorkflowClientTests.cs`):
- `ScheduleByIdAsync_201Created_OutboundCarriesServiceTokenAndTenantHeader` — outbound has minted service token + `X-Trellis-Tenant-Id` from inbound JWT claim.
- `ScheduleByIdAsync_NoHttpContext_ReturnsAuthFailed_WithoutHttpCall` — no inbound JWT context → AuthFailed, no outbound HTTP.
- `ScheduleByIdAsync_HttpContextMissingTenantClaim_ReturnsAuthFailed_WithoutHttpCall` — inbound JWT missing tenant_id → AuthFailed.
- `ScheduleByIdAsync_TokenIssuerThrows_ReturnsAuthFailed_WithoutHttpCall` — `InternalTokenIssuanceException` → AuthFailed envelope, no outbound HTTP.

**Fix-up: agent-loop E2E** (1 new in `ConversationEndpointTests.cs`):
- `ConversationTurn_AskingToRunWorkflow_InvokesWorkflowScheduleAndReturnsAugmentedReply` — closes brief Pin 1. Stub LLM emits `workflow_schedule` tool_call; stub workflow client returns 201; agent loop produces final assistant text carrying the run id. 3-turn persistence verified.

**Fix-up: BadRequest envelope** (1 new in `WorkflowScheduleToolTests.cs`):
- `RunAsync_QwenReturnsBadRequest_LlmGetsBadRequestEnvelope` — closes brief Pin 4 category. 4xx other than 401/404 → `error: "bad_request"` + ProblemDetails detail surfaced.

**Fix-up: real-handler probe** (1 new in `WorkflowScheduleToolTests.cs`):
- `RunAsync_SchemaBypass_MissingWorkflowDefinitionId_NoOutboundHttp` — pins the no-outbound-HTTP invariant against the actual `HttpMessageHandler`, not just the `IWorkflowClient` interface. Closes brief B4.

**WorkflowScheduleTool pin tests** (9 in `WorkflowScheduleToolTests.cs`):
- `Descriptor_HasExpectedShape` — Name + Category=Act + Description + Schema contains workflow_definition_id.
- `Descriptor_ParameterSchema_IsValidJsonSchema` — startup ToolRegistry validation safety.
- `RunAsync_HappyPath_ReturnsRunIdFromQwen` — Phase 3.I pin #1; 201 body passes through verbatim.
- `RunAsync_SchemaBypass_MissingWorkflowDefinitionId_ReturnsStructuredError_WithoutDispatch` — pin #2; pre-flight validation; verify no HTTP call.
- `RunAsync_QwenReturns404_LlmGetsDefinitionNotFoundEnvelope` — pin #3; structured envelope with id echoed back.
- `RunAsync_QwenReturnsTransient_LlmGetsTransientEnvelope` — pin #4; 5xx/timeout → transient envelope.
- `RunAsync_QwenReturns401_LlmGetsAuthFailedEnvelope` — auth_failed distinct from transient.
- `SerializeInitialInputIfPresent_ObjectPresent_ReturnsRawJson` + `_NoObject_ReturnsNull` + `_CamelCaseKey_AlsoExtracts` — pure-function pins on the helper.

**HttpWorkflowClient pin tests** (12 in `HttpWorkflowClientTests.cs`):
- `BuildRequestBody_NoInput` + `BuildRequestBody_WithInitialInput` — pure URL-builder pins on the wire shape.
- `ScheduleByIdAsync_201Created_ReturnsSuccessWithBodyJson` — happy path.
- `ScheduleByIdAsync_401_MapsToAuthFailed`.
- `ScheduleByIdAsync_404_MapsToDefinitionNotFound_WithIdInMessage`.
- `ScheduleByIdAsync_5xx_MapsToTransient_WithProblemDetailsDetail`.
- `ScheduleByIdAsync_OtherFourXx_MapsToBadRequest_WithDetail` — surfaces ProblemDetails.detail.
- `ScheduleByIdAsync_TransportError_MapsToTransient`.
- `ScheduleByIdAsync_Timeout_MapsToTransient`.
- `ScheduleByIdAsync_CallerCancellation_RethrowsOperationCanceled` — caller-CT propagation matches `HttpSearchClient`.
- `ScheduleByIdAsync_EmptyDefinitionId_ShortCircuitsBeforeAnyHttpCall`.
- `ScheduleByIdAsync_HttpContextWithBearer_PropagatesAuthorizationHeader` — JWT propagation.
- `ScheduleByIdAsync_NoHttpContext_OmitsAuthorizationHeader` — graceful no-op.

**Exposure-gate pin tests** (3 in `DefaultToolExposurePolicyTests.cs`):
- `WorkflowScheduleTool_DefaultExposeFalse_Hidden` — pin #5 negative-space pin; default false hides the tool.
- `WorkflowScheduleTool_ExposeWorkflowScheduleTrue_Exposed` — flip flag → advertised.
- `WorkflowScheduleTool_ExposeTrue_PerToolGateStillApplies` — flag-true path doesn't bypass PerTool gating; AllowedTenants AND with the flag.

## LoC + brief deviation

- Production: +806 (Observability/ +0, AgentExecution/ +304, Services/ +455, Program.cs +41, ToolCatalogueOptions +19, appsettings +6).
- Tests: +705 (WorkflowScheduleToolTests +257, HttpWorkflowClientTests +262, DefaultToolExposurePolicyTests +58, AgentTelemetryTests +128).
- Cumulative: +1511.

**Brief deviation:** the brief estimated ~600-800 LoC; actual landed at +1511 (~88% over the upper bound). Drivers:
1. `HttpWorkflowClient` carries 334 LoC of failure-mapping + JWT propagation. `HttpSearchClient` (its closest precedent) is similarly large (~285 LoC).
2. `WorkflowScheduleTool` carries 269 LoC of LLM-visible envelope shape + the boundary translation.
3. The full failure-mapping pin coverage for `HttpWorkflowClient` (12 pins) is the test-discipline-mandated ratio for the production failure paths; trimming would leave failure modes uncovered.

Work is bounded — every pin green, no half-finished pieces. Surface this in the PR description per the [[feedback_surface_mid_scaffold_inflight]] rule (in-flight surface window passed; retroactive note acknowledged).

## Don't (forward-flag for Phase 3.J+)

- **Don't expose the inline-JSON schedule branch.** `WorkflowDefinitionJson` is a full DAG; LLMs hallucinating one would create runs that fail or worse, execute unintended steps. If a future need surfaces, that's a separate brief with a constrained sub-shape — don't widen this tool's schema.
- **Don't reuse trellis-web's HttpWorkflowClient code.** The brief explicitly said "copy the pattern, not the code." Trellis-web's variant has UI-side concerns (user-facing error rendering); this client's failure mapping is LLM-readable + operator-actionable.
- **Don't default `ExposeWorkflowSchedule=true`.** Production-safe default per Phase 3.I brief. Flip default after QA validates the tool against real workflow definitions. The PerTool gate layers on top once the flag is on, so per-tenant rollout is possible.
- **Don't cache `WorkflowDefinition` metadata Assistant-side.** Out of scope per brief. If a future tool needs to inspect or validate definitions ahead of scheduling, that's a separate tool (`workflow_describe` or similar).
- **Don't add scheduling-futures (cron, delayed).** Hangfire already handles delayed runs upstream; the tool is fire-now. If a future need surfaces, surface a brief.
- **Don't move `HttpWorkflowClient`'s JWT propagation into a shared `DelegatingHandler`.** Single-consumer rule (same posture Phase 3.B + 3.D + 3.F + 3.H took for their seams). Lift when a second tool needs the same inbound-JWT-propagation shape.
- **Don't reshape qwen's 201 body inside `WorkflowScheduleTool`.** Pass-through (a) per Phase 3.B precedent — the LLM sees qwen-canonical PascalCase fields. If qwen renames a field, we re-bake the tool descriptor's example in the prompt rather than introducing a translation layer.
- **Don't add a tool dispatching cancellation-token propagation override.** The caller's CT flows through to the tool; the executor's OCE filter handles cancellation in the canonical path. No tool-side cancellation budget.

## Cross-component dependencies

- Depends on trellis-workflow (qwen) exposing `POST /api/workflows/runs` schedule-by-id branch with the wire shape documented above. No Assistant-side change can fix a qwen-side wire-shape divergence; coordinate via hub if the contract shifts.
- Depends on qwen's `TenantClaimsMiddleware` accepting the propagated user JWT — Macro 3 JWT bearer flow is shared across the Trellis surface, so this should hold per cross-component design.

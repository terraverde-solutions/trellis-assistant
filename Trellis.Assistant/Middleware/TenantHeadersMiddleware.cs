namespace Trellis.Assistant.Middleware;

/// <summary>
/// Phase-1 STUB middleware. Reads <c>X-Trellis-Tenant-Id</c> +
/// <c>X-Trellis-User-Id</c> from the inbound request, requires both to
/// be non-empty, and stashes them in <see cref="HttpContext.Items"/>
/// under <see cref="TenantIdKey"/> + <see cref="UserIdKey"/> for
/// downstream endpoint handlers. Absent or empty header → 401.
///
/// TRUST BOUNDARY (Phase 1 only):
///
/// The headers are TRUSTED because the listener is loopback-bound
/// (127.0.0.1:5117) and the public edge is the Hetzner nginx
/// vhost gated by HTTP Basic auth. There is no client-side
/// authentication of the (TenantId, UserId) pair — anyone past the
/// htpasswd gate can claim any tenant/user. This is the same QA-only
/// posture as web-qa + trainer-qa + the Phase 0 assistant-qa
/// /healthz surface.
///
/// PHASE 5 SEAM (forward reference):
///
/// When real auth lands (per docs § 59 + the Macro-2 Auth roadmap),
/// the middleware reshape is:
///
///   1. Replace header reads with JWT claim extraction. The auth
///      middleware ahead of this one (likely AddJwtBearer + a
///      Trellis-specific claims transformation) populates
///      ClaimsPrincipal. This middleware then maps the
///      "tenant_id" + "user_id" claims into HttpContext.Items.
///   2. Header naming alignment with the gateway's forwarded
///      X-Trellis-User + X-Trellis-Roles is NOT required — JWT
///      claims become the canonical source past Phase 5; the
///      Phase 1 header pair is irrelevant once real auth lands.
///   3. The downstream endpoint handlers don't change — they keep
///      reading from HttpContext.Items via the same keys. Only this
///      middleware swaps its source.
///
/// Tracking memo: Phase 5 swap to JWT claim extraction. Header-trust
/// is Phase-1-test-only. RLS pairing on conversations + turns lands
/// alongside.
/// </summary>
public sealed class TenantHeadersMiddleware
{
    public const string TenantHeaderName = "X-Trellis-Tenant-Id";
    public const string UserHeaderName = "X-Trellis-User-Id";

    /// <summary>
    /// HttpContext.Items key for the trusted tenant id. Endpoint handlers
    /// resolve via <c>(string)context.Items[TenantHeadersMiddleware.TenantIdKey]!</c>
    /// — non-null guaranteed once this middleware lets the request through.
    /// </summary>
    public const string TenantIdKey = "trellis.tenant_id";

    public const string UserIdKey = "trellis.user_id";

    private readonly RequestDelegate _next;

    public TenantHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Surfaces this middleware applies to: anything under /api/. Other
        // routes (/healthz today, future static pages) bypass auth entirely.
        // Implemented as a path check rather than a per-endpoint attribute
        // so the failure mode is "forgot the attribute" → request reaches
        // the handler unauthenticated, which is the failure mode the
        // middleware exists to prevent.
        var path = context.Request.Path.Value;
        if (path is null || !path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var tenant = context.Request.Headers[TenantHeaderName].ToString();
        var user = context.Request.Headers[UserHeaderName].ToString();

        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(user))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            // Plain text body (not a typed ProblemDetails) so the auth-
            // gate failure surface is unambiguous in operator logs:
            // 401 + "missing tenant/user" is a misconfigured client, not
            // a server fault.
            await context.Response
                .WriteAsync("{\"error\":\"missing X-Trellis-Tenant-Id or X-Trellis-User-Id\"}")
                .ConfigureAwait(false);
            return;
        }

        context.Items[TenantIdKey] = tenant;
        context.Items[UserIdKey] = user;
        await _next(context).ConfigureAwait(false);
    }
}

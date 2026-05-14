using System.Diagnostics.Metrics;
using System.Security.Claims;

namespace Trellis.Assistant.Middleware;

/// <summary>
/// Resolves <c>tenant_id</c> + <c>user_id</c> from the inbound request
/// and stashes them in <see cref="HttpContext.Items"/> under
/// <see cref="TenantIdKey"/> + <see cref="UserIdKey"/> for downstream
/// endpoint handlers. Endpoint handlers don't change shape — they keep
/// reading from <see cref="HttpContext.Items"/> via these keys; this
/// middleware swaps its source.
///
/// <para>
/// Resolution order (Macro 3 PR 2 — JWT validation):
/// <list type="number">
/// <item><b>JWT claims (canonical).</b> When
/// <see cref="HttpContext.User"/> is authenticated, read the custom
/// <c>tenant_id</c> claim + the canonical <c>sub</c> (user_id) claim.
/// Both required; missing either → 401 (the JWT was issued without the
/// shape the Assistant expects).</item>
/// <item><b>Header fallback (DEPRECATED).</b> When
/// <see cref="HttpContext.User"/> is unauthenticated, read
/// <c>X-Trellis-Tenant-Id</c> + <c>X-Trellis-User-Id</c> headers. Both
/// required; missing either → 401. <b>Each request that takes this
/// path emits a structured warning log + bumps a counter so operators
/// can identify remaining legacy callers via journalctl + Prometheus
/// scrape before the eventual removal PR.</b></item>
/// </list>
/// </para>
///
/// <para>
/// The Phase 5 directive in <c>Program.cs</c>'s LATE-RESOLUTION RULE
/// pointed at this exact reshape — JWT-claim extraction with
/// header-trust as a deprecated fallback. The header-side path stays
/// only long enough for telemetry to confirm it's dead; remove via
/// 1-PR follow-up after deprecation window.
/// </para>
///
/// <para>
/// Path scope: only <c>/api/*</c> routes go through this middleware's
/// resolution. <c>/healthz</c> + <c>/readyz</c> bypass entirely
/// (operator probes; <c>[AllowAnonymous]</c> in the route registration).
/// </para>
/// </summary>
public sealed class TenantClaimsMiddleware
{
    /// <summary>JWT claim name carrying the tenant Guid (uuid-format string per Phase 3.A C1).</summary>
    public const string TenantIdClaimName = "tenant_id";

    /// <summary>
    /// Phase 3.F: JWT claim name carrying the tenant-role string (drives
    /// <c>IToolExposurePolicy</c>'s RequiredRole gate). Optional —
    /// requests without this claim get
    /// <c>HttpContext.Items[TenantRoleKey] = null</c>; policy treats
    /// missing role as fail-closed for any RequiredRole gate. Macro 2's
    /// QA token issuer may or may not emit this claim; the middleware
    /// is permissive (null when absent), not 401-on-absent.
    /// </summary>
    public const string TenantRoleClaimName = "tenant_role";

    /// <summary>Deprecated Phase 1 header — fallback path emits a deprecation warning per request.</summary>
    public const string TenantHeaderName = "X-Trellis-Tenant-Id";

    /// <summary>Deprecated Phase 1 header — fallback path emits a deprecation warning per request.</summary>
    public const string UserHeaderName = "X-Trellis-User-Id";

    /// <summary>
    /// Phase 3.F: deprecated-header fallback for the tenant-role claim.
    /// Optional — header-path requests without this header land with
    /// <c>HttpContext.Items[TenantRoleKey] = null</c>, same fail-closed
    /// behavior as the JWT path with the claim absent.
    /// </summary>
    public const string TenantRoleHeaderName = "X-Trellis-Tenant-Role";

    /// <summary>
    /// HttpContext.Items key for the resolved tenant id. Endpoint handlers
    /// resolve via <c>(string)context.Items[TenantClaimsMiddleware.TenantIdKey]!</c>
    /// — non-null guaranteed once this middleware lets the request through.
    /// </summary>
    public const string TenantIdKey = "trellis.tenant_id";

    public const string UserIdKey = "trellis.user_id";

    /// <summary>
    /// Phase 3.F: HttpContext.Items key for the resolved tenant-role
    /// string. NULLABLE — value is <c>string?</c> (cast as
    /// <c>(string?)context.Items[TenantClaimsMiddleware.TenantRoleKey]</c>).
    /// Null when neither the JWT's <c>tenant_role</c> claim NOR the
    /// deprecated <c>X-Trellis-Tenant-Role</c> header was present.
    /// </summary>
    public const string TenantRoleKey = "trellis.tenant_role";

    /// <summary>
    /// Meter for deprecation telemetry. Counter
    /// <c>trellis_assistant_tenant_claims_deprecated_header_fallback_total</c>
    /// increments per request that took the header-fallback path. Operators
    /// scrape this from the host's metrics endpoint (when added) or grep
    /// journalctl for the structured warning.
    /// </summary>
    public const string MeterName = "Trellis.Assistant.TenantClaims";

    private static readonly Meter _meter = new(MeterName, "1.0.0");

    private static readonly Counter<long> _deprecatedHeaderFallbackCounter =
        _meter.CreateCounter<long>(
            "trellis_assistant_tenant_claims_deprecated_header_fallback_total",
            unit: "{request}",
            description: "Number of requests that fell back to deprecated header-based tenant resolution. Target: zero post-Macro-3-deprecation.");

    private readonly RequestDelegate _next;
    private readonly ILogger<TenantClaimsMiddleware> _logger;

    public TenantClaimsMiddleware(RequestDelegate next, ILogger<TenantClaimsMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Auth-scheme name applied to the synthesized <see cref="ClaimsPrincipal"/>
    /// when the deprecated header-fallback path runs. Distinct from the
    /// JWT bearer scheme so operators can grep auth events by scheme +
    /// future structured-logging filters can route header-path requests
    /// to the deprecation telemetry stream.
    /// </summary>
    public const string DeprecatedHeaderAuthScheme = "DeprecatedHeader";

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value;
        if (path is null || !path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // ---- Path 1: JWT claims (canonical) ----
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var tenantClaim = context.User.FindFirst(TenantIdClaimName)?.Value;
            // sub claim: ASP.NET Core's JwtBearer maps "sub" to ClaimTypes.NameIdentifier
            // by default (per JwtSecurityTokenHandler.InboundClaimTypeMap); some test
            // configurations leave the literal "sub" claim type unmapped, so check both.
            var userClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? context.User.FindFirst("sub")?.Value;

            if (string.IsNullOrWhiteSpace(tenantClaim) || string.IsNullOrWhiteSpace(userClaim))
            {
                await Write401Async(context,
                    "JWT validated but required claims missing: 'tenant_id' or 'sub' (NameIdentifier).")
                    .ConfigureAwait(false);
                return;
            }

            context.Items[TenantIdKey] = tenantClaim;
            context.Items[UserIdKey] = userClaim;
            // Phase 3.F: optional tenant_role claim. Null when absent —
            // IToolExposurePolicy treats null as fail-closed for any
            // RequiredRole gate. Macro 2 may not emit this claim today;
            // permissive read so existing JWTs continue working.
            context.Items[TenantRoleKey] = context.User.FindFirst(TenantRoleClaimName)?.Value;
            await _next(context).ConfigureAwait(false);
            return;
        }

        // ---- Path 2: Header fallback (DEPRECATED) ----
        var tenant = context.Request.Headers[TenantHeaderName].ToString();
        var user = context.Request.Headers[UserHeaderName].ToString();

        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(user))
        {
            // Don't 401 here — let UseAuthorization handle it. With no JWT
            // AND no headers, HttpContext.User stays unauthenticated and
            // [Authorize]-protected routes naturally produce 401 from
            // ASP.NET Core's authorization middleware. Anonymous routes
            // (/healthz, /readyz) bypass /api/ entirely + don't reach here.
            //
            // For the /api/* routes specifically — without the synthesized
            // principal, UseAuthorization's 401 fires. With a synthesized
            // principal (when headers ARE present), we proceed past
            // UseAuthorization. Same wire shape either way.
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Deprecation telemetry — structured warning + counter increment
        // so operators can identify legacy callers (cron jobs, direct
        // curl scripts) via journalctl OR Prometheus scrape before the
        // eventual removal PR.
        var callerIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        _deprecatedHeaderFallbackCounter.Add(
            1, new KeyValuePair<string, object?>("caller_ip", callerIp));
        _logger.LogWarning(
            "Deprecated header-based tenant resolution: caller did not present a JWT bearer. " +
            "Resolved tenantId={TenantId} userId={UserId} from X-Trellis-* headers; caller_ip={CallerIp}. " +
            "Migrate caller to JWT — header path slated for removal.",
            tenant, user, callerIp);

        // Synthesize a ClaimsPrincipal so UseAuthorization sees an
        // authenticated user + RequireAuthorization-protected routes
        // pass through to the endpoint. The DeprecatedHeader scheme name
        // distinguishes header-path principals from JWT principals in
        // any downstream auth-event logging.
        // Phase 3.F: include the optional tenant-role claim when the
        // X-Trellis-Tenant-Role header was present.
        var roleHeader = context.Request.Headers[TenantRoleHeaderName].ToString();
        var claims = new List<Claim>(3)
        {
            new(TenantIdClaimName, tenant),
            new(ClaimTypes.NameIdentifier, user),
        };
        if (!string.IsNullOrWhiteSpace(roleHeader))
        {
            claims.Add(new Claim(TenantRoleClaimName, roleHeader));
        }
        var identity = new ClaimsIdentity(claims, authenticationType: DeprecatedHeaderAuthScheme);
        context.User = new ClaimsPrincipal(identity);

        context.Items[TenantIdKey] = tenant;
        context.Items[UserIdKey] = user;
        context.Items[TenantRoleKey] = string.IsNullOrWhiteSpace(roleHeader) ? null : roleHeader;
        await _next(context).ConfigureAwait(false);
    }

    private static async Task Write401Async(HttpContext context, string error)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";
        await context.Response
            .WriteAsync("{\"error\":\"" + error + "\"}")
            .ConfigureAwait(false);
    }
}

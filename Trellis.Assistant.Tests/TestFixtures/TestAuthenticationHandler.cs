using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Trellis.Assistant.Middleware;

namespace Trellis.Assistant.Tests.TestFixtures;

/// <summary>
/// Test-only authentication handler that synthesizes a
/// <see cref="ClaimsPrincipal"/> from the deprecated
/// <c>X-Trellis-Tenant-Id</c> + <c>X-Trellis-User-Id</c> headers (Phase 1+2
/// fixture pattern). Lets the existing 116 prior tests' setups continue
/// working unchanged after Macro 3 PR 2's JWT validation lands —
/// production validates real bearers; the test factory swaps in this
/// handler via <c>ConfigureTestServices</c>.
///
/// <para>
/// Distinct from <see cref="TenantClaimsMiddleware"/>'s deprecated-header
/// fallback: that path emits a warning + counter telemetry on every
/// request (it's the legacy-caller production tracker). This handler is
/// the test-fixture short-circuit — it makes the request appear to have
/// arrived already-authenticated, so <see cref="TenantClaimsMiddleware"/>
/// takes the canonical "JWT claims" path (no warning, no counter
/// increment) and tests don't pollute the deprecation telemetry stream.
/// </para>
///
/// <para>
/// Returns <see cref="AuthenticateResult.NoResult"/> when both headers
/// are missing — preserves the existing 401 behavior for the
/// "<c>PostConversations_WithoutTenantHeader_Returns401</c>" family of
/// tests. With no result + a <see cref="RequireAuthorization"/>-protected
/// endpoint, ASP.NET Core's authorization middleware produces the 401.
/// </para>
/// </summary>
public sealed class TestAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "TestAuth";

    public TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var tenant = Request.Headers[TenantClaimsMiddleware.TenantHeaderName].ToString();
        var user = Request.Headers[TenantClaimsMiddleware.UserHeaderName].ToString();

        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(user))
        {
            // No headers → no synthesized auth. The downstream
            // RequireAuthorization-protected endpoint sees an
            // unauthenticated principal + ASP.NET Core's authz
            // middleware produces 401. Preserves the prior
            // PostConversations_WithoutTenantHeader_Returns401 behavior.
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(TenantClaimsMiddleware.TenantIdClaimName, tenant),
                new Claim(ClaimTypes.NameIdentifier, user),
            },
            authenticationType: SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.IdentityModel.Tokens;
using Trellis.Assistant.Endpoints;
using Trellis.Assistant.Tests.TestFixtures;
using Xunit;

namespace Trellis.Assistant.Tests.Integration;

/// <summary>
/// Pinned tests for Macro 3 PR 2 — JWT bearer validation on the
/// Trellis.Assistant API. Distinct from <see cref="ConversationEndpointTests"/>
/// (which uses <see cref="TestAuthenticationHandler"/> for fast
/// header-based fixture setup): these tests exercise the production
/// <c>JwtBearerHandler</c> validation pipeline (signature, audience,
/// issuer, expiry) against test-minted RSA-signed JWTs.
///
/// <para>
/// The <see cref="JwtBearerTestWebApplicationFactory"/> generates a
/// fresh RSA key per fixture instance; the production <c>AddJwtBearer</c>
/// registration is overridden to validate against that key + a test
/// issuer/audience. Tokens minted via
/// <see cref="JwtBearerTestWebApplicationFactory.MintToken"/> are
/// accepted on the happy path; tokens minted with the wrong key, wrong
/// audience, or past expiry hit the production validation pipeline +
/// produce 401.
/// </para>
/// </summary>
public sealed class JwtAuthenticationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private JwtBearerTestWebApplicationFactory? _factory;

    public JwtAuthenticationTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    public Task InitializeAsync()
    {
        if (_pg.IsAvailable)
        {
            _factory = new JwtBearerTestWebApplicationFactory(_pg.ConnectionString);
        }
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task JwtAuth_ValidToken_AccessGranted_TenantAndUserDerivedFromClaims()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var token = _factory!.MintToken(
            tenantId: TestTenants.TenantA,
            userId: TestTenants.UserA);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // POST /api/conversations exercises the full
        // UseAuthentication → UseAuthorization → TenantClaimsMiddleware
        // → endpoint flow. Success = 201 Created with the conversation
        // owned by the (tenantId, userId) extracted from JWT claims.
        var resp = await client.PostAsJsonAsync(
            "/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        resp.StatusCode.Should().Be(HttpStatusCode.Created,
            "valid JWT bearer with tenant_id + sub claims grants access; tenant + user derived from claims by TenantClaimsMiddleware");

        var body = await resp.Content.ReadFromJsonAsync<ConversationEndpoints.CreateConversationResponse>();
        body.Should().NotBeNull();
        body!.Id.Should().NotBeNullOrEmpty();
    }

    [SkippableFact]
    public async Task JwtAuth_NoToken_Returns401()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var client = _factory!.CreateClient();
        // No Authorization header AND no X-Trellis-* fallback headers.
        var resp = await client.PostAsJsonAsync(
            "/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "missing both JWT bearer and deprecated X-Trellis-* headers → ASP.NET Core's authorization middleware fires 401");
    }

    [SkippableFact]
    public async Task JwtAuth_InvalidSignature_Returns401()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        // Mint with a DIFFERENT RSA key — the factory's validator only
        // accepts tokens signed by its own key. Simulates a malicious
        // token issued by a rogue signer.
        using var rogueKey = RSA.Create(2048);
        var rogueToken = _factory!.MintToken(
            tenantId: TestTenants.TenantA,
            userId: TestTenants.UserA,
            signingKey: new RsaSecurityKey(rogueKey.ExportParameters(includePrivateParameters: true)));

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rogueToken);

        var resp = await client.PostAsJsonAsync(
            "/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "JWT signature validation must reject tokens signed by an unknown key — IssuerSigningKey check is the canonical bearer-auth security boundary");
    }

    [SkippableFact]
    public async Task JwtAuth_WrongAudience_Returns401()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var wrongAudienceToken = _factory!.MintToken(
            tenantId: TestTenants.TenantA,
            userId: TestTenants.UserA,
            audience: "trellis-trainer-not-assistant");  // explicitly different audience

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", wrongAudienceToken);

        var resp = await client.PostAsJsonAsync(
            "/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "JWT audience validation must reject tokens minted for a different service — Auth:Audience check pins per-service authorization");
    }

    [SkippableFact]
    public async Task JwtAuth_ExpiredToken_Returns401()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        // Token expired 1 hour ago. Factory's TokenValidationParameters
        // sets ClockSkew=Zero so the expiry check fires reliably (default
        // 5-minute skew would mask short-expiry tests).
        var expiredToken = _factory!.MintToken(
            tenantId: TestTenants.TenantA,
            userId: TestTenants.UserA,
            expires: DateTime.UtcNow.AddHours(-1));

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", expiredToken);

        var resp = await client.PostAsJsonAsync(
            "/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "expired JWT must be rejected — ValidateLifetime check is the canonical token-rotation security boundary");
    }

    [SkippableFact]
    public async Task JwtAuth_TokenMissingTenantIdClaim_Returns401()
    {
        // Defensive pin per Macro 3 PR 2 design: a JWT signed by the
        // right key + correct audience + not expired but lacking the
        // custom tenant_id claim must still 401. TenantClaimsMiddleware
        // detects the missing claim post-authentication.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        // Mint a valid-shaped token but with an empty tenant_id claim.
        // The factory's MintToken always sets tenant_id; we use a token
        // with whitespace-only tenant_id to exercise the
        // string.IsNullOrWhiteSpace check in TenantClaimsMiddleware.
        var tokenWithBlankTenant = _factory!.MintToken(
            tenantId: "   ",  // whitespace; passes JWT validation but fails the middleware's claim check
            userId: TestTenants.UserA);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenWithBlankTenant);

        var resp = await client.PostAsJsonAsync(
            "/api/conversations",
            new ConversationEndpoints.CreateConversationRequest(Channel: "api"));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "JWT validated successfully but tenant_id claim is whitespace — TenantClaimsMiddleware rejects past the claims-extraction step");
    }

    [SkippableFact]
    public async Task JwtAuth_HealthzAndReadyz_StayAnonymous_NoTokenNeeded()
    {
        // Operator probes preserved per scope item 3. Even without a
        // token, /healthz + /readyz return their canonical status —
        // [AllowAnonymous]-style behavior since neither has
        // RequireAuthorization in Program.cs.
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        using var client = _factory!.CreateClient();
        // No Authorization header.
        var healthzResp = await client.GetAsync("/healthz");
        healthzResp.StatusCode.Should().Be(HttpStatusCode.OK,
            "/healthz must stay anonymous — operator probes don't carry JWT bearers");

        var readyzResp = await client.GetAsync("/readyz");
        ((int)readyzResp.StatusCode).Should().Match(
            code => code == 200 || code == 503,
            "/readyz returns 200 or 503 depending on warm-up state, but never 401 — anonymous probe");
    }
}

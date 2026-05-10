using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Trellis.Assistant.AgentExecution;
using Trellis.Assistant.Services;
using Trellis.Core.Services;

namespace Trellis.Assistant.Tests.TestFixtures;

/// <summary>
/// Distinct from <see cref="AssistantWebApplicationFactory"/>: this
/// factory keeps the production <c>AddJwtBearer</c> registration and
/// configures it to validate against a TEST signing key (RSA, generated
/// per-factory). Callers mint JWTs against the matching private key via
/// <see cref="MintToken"/> + send them as <c>Authorization: Bearer</c>.
///
/// <para>
/// Used by <c>JwtAuthenticationTests</c> only — the 5 hub-listed
/// pinned tests need to exercise the real <see cref="JwtBearerHandler"/>
/// validation pipeline (signature check, audience check, expiry
/// check). Those wouldn't fire under the
/// <see cref="TestAuthenticationHandler"/> shortcut that
/// <see cref="AssistantWebApplicationFactory"/> uses for non-auth-
/// focused tests.
/// </para>
///
/// <para>
/// The test issuer + audience are constants below; tests that mint
/// tokens with WRONG values (wrong audience, wrong signing key)
/// exercise the negative paths.
/// </para>
/// </summary>
public sealed class JwtBearerTestWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string TestIssuer = "https://test-issuer.trellis.local/";
    public const string TestAudience = "trellis-assistant-test";

    private readonly string _connectionString;
    private readonly RSA _signingKey;
    private readonly StubAgentLlmClient _agentLlmStub = new();

    public RsaSecurityKey PublicKey => new(_signingKey.ExportParameters(includePrivateParameters: false));
    public RsaSecurityKey PrivateKey => new(_signingKey.ExportParameters(includePrivateParameters: true));

    public StubAgentLlmClient AgentLlmStub => _agentLlmStub;

    public JwtBearerTestWebApplicationFactory(string connectionString)
    {
        _connectionString = connectionString;
        // 2048-bit RSA — same key length production OIDC providers use.
        _signingKey = RSA.Create(2048);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _signingKey.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = _connectionString,
                ["Assistant:AutoMigrate"] = "true",
                ["Assistant:WarmupModel"] = "",
                ["Assistant:TurnRequestTimeoutSeconds"] = "60",
                ["Ollama:BaseUrl"] = "http://test-host-unreachable:11434/",
                // Auth:Authority points at the dummy test issuer; the
                // OIDC discovery endpoint (https://.../.well-known/openid-configuration)
                // would not actually resolve, but we override
                // TokenValidationParameters via ConfigureTestServices
                // below to bypass discovery entirely.
                ["Auth:Authority"] = TestIssuer,
                ["Auth:Audience"] = TestAudience,
                ["Auth:RequireHttpsMetadata"] = "false",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            // Stub LLM clients (same pattern as AssistantWebApplicationFactory).
            services.RemoveAll<IOllamaClient>();
            services.AddSingleton<IOllamaClient, StubLlmClient>();
            services.RemoveAll<IAgentLlmClient>();
            services.AddSingleton<IAgentLlmClient>(_agentLlmStub);

            // Override JwtBearerOptions.TokenValidationParameters to
            // skip OIDC discovery + use the in-process test signing
            // key. The validation pipeline is otherwise the production
            // path: signature → audience → issuer → expiry are all
            // checked against TestIssuer + TestAudience + the test RSA
            // public key.
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, opts =>
            {
                // Disable metadata fetch — the test-issuer URL doesn't
                // resolve. Set the signing key directly.
                opts.MetadataAddress = null!;
                opts.ConfigurationManager = null!;
                opts.Authority = null;  // null Authority + manual TokenValidationParameters bypasses discovery
                opts.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = TestIssuer,
                    ValidateAudience = true,
                    ValidAudience = TestAudience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = PublicKey,
                    // Tighten clock skew so the expired-token test fires
                    // predictably; default is 5 minutes which masks the
                    // negative path.
                    ClockSkew = TimeSpan.Zero,
                };
            });
        });
    }

    /// <summary>
    /// Mint a JWT signed with the factory's test private key. Callers
    /// override claims for negative-path tests.
    /// </summary>
    public string MintToken(
        string tenantId,
        string userId,
        string? audience = null,
        DateTime? expires = null,
        RsaSecurityKey? signingKey = null)
    {
        var actualExpires = expires ?? DateTime.UtcNow.AddMinutes(30);
        // notBefore must be strictly less than expires or
        // JwtSecurityToken's ctor rejects with "IDX12401: Expires must
        // be after NotBefore". Anchor to expires - 1h so the relation
        // holds for both the happy-path (expires = now + 30min →
        // notBefore = now - 30min) and the expired-token path
        // (expires = now - 1h → notBefore = now - 2h). Pinned by
        // MintToken_WithPastExpires_DoesNotThrow.
        var notBefore = actualExpires.AddHours(-1);

        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer: TestIssuer,
            audience: audience ?? TestAudience,
            claims: new[]
            {
                new System.Security.Claims.Claim("tenant_id", tenantId),
                new System.Security.Claims.Claim("sub", userId),
            },
            notBefore: notBefore,
            expires: actualExpires,
            signingCredentials: new SigningCredentials(
                signingKey ?? PrivateKey,
                SecurityAlgorithms.RsaSha256));
        return handler.WriteToken(token);
    }
}

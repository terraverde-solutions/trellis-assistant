using FluentAssertions;
using Xunit;

namespace Trellis.Assistant.Tests.TestFixtures;

/// <summary>
/// Unit tests for the test infrastructure itself — locks
/// <see cref="JwtBearerTestWebApplicationFactory.MintToken"/>'s argument
/// handling against regressions. Doesn't need Docker: the factory's
/// ctor stores the connection string + generates an RSA key without
/// bootstrapping the host (host creation is lazy via
/// <c>WebApplicationFactory&lt;TEntryPoint&gt;.CreateClient</c>); MintToken
/// is pure crypto + JWT construction.
///
/// <para>
/// Why this class exists: GB10 verification of Macro 3 PR 2 surfaced
/// IDX12401 ("Expires must be after NotBefore") on the
/// <c>JwtAuth_ExpiredToken_Returns401</c> integration test because
/// the original MintToken pinned <c>notBefore = now - 1min</c>
/// regardless of <c>expires</c> value — broke when <c>expires</c> was
/// in the past. Fix: derive <c>notBefore</c> from <c>expires</c>. This
/// pin guards the fix.
/// </para>
/// </summary>
public sealed class JwtBearerTestWebApplicationFactoryTests
{
    private const string DummyConnectionString = "Host=ignored;Database=ignored;Username=ignored;Password=ignored";
    private const string TestTenant = "00000000-0000-0000-0000-00000000000a";
    private const string TestUser = "user-a";

    [Fact]
    public void MintToken_WithPastExpires_DoesNotThrow_RegressionPin()
    {
        // Anti-mutation pin per GB10 verification feedback (Macro 3 PR 2):
        // a future change that pins notBefore to a fixed point relative
        // to "now" instead of relative to expires would re-introduce
        // the IDX12401 failure on the JwtAuth_ExpiredToken_Returns401
        // integration test. This unit-level pin catches the regression
        // without needing Docker / Postgres / the full integration
        // fixture.
        using var factory = new JwtBearerTestWebApplicationFactory(DummyConnectionString);

        var act = () => factory.MintToken(
            tenantId: TestTenant,
            userId: TestUser,
            expires: DateTime.UtcNow.AddHours(-1));

        act.Should().NotThrow(
            "MintToken with expires in the past must mint a syntactically valid (but expired) JWT — " +
            "the test factory's notBefore must be derived from expires, not pinned at now. " +
            "Regression of the IDX12401 'Expires must be after NotBefore' shape.");
    }

    [Fact]
    public void MintToken_WithFutureExpires_DoesNotThrow()
    {
        using var factory = new JwtBearerTestWebApplicationFactory(DummyConnectionString);
        var act = () => factory.MintToken(
            tenantId: TestTenant,
            userId: TestUser,
            expires: DateTime.UtcNow.AddHours(1));
        act.Should().NotThrow();
    }

    [Fact]
    public void MintToken_DefaultExpires_DoesNotThrow()
    {
        using var factory = new JwtBearerTestWebApplicationFactory(DummyConnectionString);
        var act = () => factory.MintToken(
            tenantId: TestTenant,
            userId: TestUser);
        act.Should().NotThrow(
            "default expires (null → factory uses now + 30min) is the happy path; " +
            "future-expires path must mint cleanly");
    }

    [Fact]
    public void MintToken_ProducesNonEmptyTokenString()
    {
        using var factory = new JwtBearerTestWebApplicationFactory(DummyConnectionString);
        var token = factory.MintToken(tenantId: TestTenant, userId: TestUser);
        token.Should().NotBeNullOrWhiteSpace();
        // JWT format is base64url(header).base64url(payload).base64url(signature)
        // — three dot-separated segments. A minimal sanity check.
        token.Split('.').Should().HaveCount(3,
            "JWT compact serialization is exactly three dot-separated segments");
    }
}

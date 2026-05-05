namespace Trellis.Assistant.Tests.TestFixtures;

/// <summary>
/// Canonical Guid-shaped tenant + user identifiers used across the test
/// suite. Phase 3.A ratification C1: production tenantIds in the JWT
/// <c>tenant_id</c> claim are uuid-format strings; the gateway issues
/// uuid-shaped tenants. The agent executor's
/// <c>AgentRunRequest.OrgId</c> is a <see cref="Guid"/>, derived via
/// <see cref="Guid"/>.Parse(tenantId) — which means tests must use
/// Guid-shaped tenant strings to mirror production parity.
///
/// Phase 1+2 originally used short labels (<c>"tenant-a"</c>,
/// <c>"user-a"</c>); Phase 3.A.1 converts those to canonical Guid
/// strings as a cross-cutting refactor in the same diff. Reading the
/// tests, the labels still convey the same intent (a-vs-b for cross-
/// tenant isolation, etc.) — the strings now ALSO satisfy
/// <see cref="Guid"/>.Parse.
///
/// Naming convention: tenant ids are <c>00000000-0000-0000-0000-00000000000X</c>
/// (zeros + a single hex digit at the tail); user ids are
/// <c>10000000-0000-0000-0000-00000000000X</c> (1 prefix). Test-name
/// constants like <see cref="TenantA"/> read the same as the prior
/// <c>"tenant-a"</c> labels for reviewer continuity.
/// </summary>
public static class TestTenants
{
    public const string TenantA = "00000000-0000-0000-0000-00000000000a";
    public const string TenantB = "00000000-0000-0000-0000-00000000000b";

    public const string UserA = "10000000-0000-0000-0000-00000000000a";
    public const string UserB = "10000000-0000-0000-0000-00000000000b";

    /// <summary>Distinct identifier for the real-Ollama smoke (avoids state collisions with the stub-driven tests).</summary>
    public const string TenantRealLlm = "20000000-0000-0000-0000-000000000001";

    /// <summary>Distinct identifier for the upstream-502 invalid-model smoke.</summary>
    public const string TenantBadModel = "20000000-0000-0000-0000-000000000002";

    /// <summary>Generic single-tenant fixture used by EFMigrationSmokeTests' raw-SQL insert tests.</summary>
    public const string TenantRaw = "30000000-0000-0000-0000-000000000001";
    public const string UserRaw = "31000000-0000-0000-0000-000000000001";

    /// <summary>Real-LLM smoke uses a single user; matches the TenantRealLlm scope.</summary>
    public const string UserRealLlm = "11000000-0000-0000-0000-000000000001";
    public const string UserBadModel = "11000000-0000-0000-0000-000000000002";
}

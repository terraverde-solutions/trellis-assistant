using System.ComponentModel.DataAnnotations;

namespace Trellis.Assistant.Services;

/// <summary>
/// Phase 3.I fix-up: configuration for
/// <see cref="OpenIddictInternalTokenIssuer"/>. Bound from
/// <c>Assistant:Auth:InternalClient</c> + the sibling-key
/// <c>Assistant:Auth:TokenEndpoint</c>.
///
/// <para>
/// Production deploys override via env vars:
/// <list type="bullet">
/// <item><c>Assistant__Auth__InternalClient__ClientId</c></item>
/// <item><c>Assistant__Auth__InternalClient__ClientSecret</c></item>
/// <item><c>Assistant__Auth__TokenEndpoint</c></item>
/// </list>
/// Dev defaults point at the loopback Auth service (port 5119).
/// </para>
/// </summary>
public sealed class InternalTokenIssuerOptions
{
    public const string SectionName = "Assistant:Auth:InternalClient";

    /// <summary>
    /// Full URL to the Auth service's OAuth2 token endpoint. Lives in
    /// the parent <c>Assistant:Auth</c> section (alongside the bearer
    /// authority used inbound) so a single Auth host configures both
    /// sides. Bound via a separate <see cref="AuthTokenEndpointKey"/>
    /// lookup in <c>Program.cs</c>.
    /// </summary>
    [Required]
    public string TokenEndpoint { get; set; } = "http://127.0.0.1:5119/connect/token";

    /// <summary>
    /// OAuth2 client_id registered with the Auth service for the
    /// Assistant's service identity. Empty = misconfiguration; startup
    /// validation throws.
    /// </summary>
    [Required]
    public string ClientId { get; set; } = "";

    /// <summary>
    /// OAuth2 client_secret. Required; in production loaded from env
    /// var to keep secrets out of appsettings.json.
    /// </summary>
    [Required]
    public string ClientSecret { get; set; } = "";

    /// <summary>
    /// Per-call timeout for the token-endpoint mint. Default 5s — the
    /// Auth service mint should be sub-second; if it's slow, surface
    /// fast as <see cref="InternalTokenIssuanceException"/> rather than
    /// holding up the agent loop.
    /// </summary>
    [Range(1, 60)]
    public int RequestTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Refresh skew window in seconds. The cache returns a token only
    /// while <c>exp - now &gt; RefreshSkewSeconds</c>. Default 60s gives
    /// the Assistant a full minute of slack against clock drift +
    /// in-flight requests; matches the
    /// <c>JwtClientCredentialsOptions.RefreshSafetyMarginSeconds</c>
    /// convention from Trellis.Core (Macro 2 PR 6).
    /// </summary>
    [Range(0, 600)]
    public int RefreshSkewSeconds { get; set; } = 60;
}

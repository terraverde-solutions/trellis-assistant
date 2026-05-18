namespace Trellis.Assistant.Services;

/// <summary>
/// Phase 3.I fix-up: issues service-to-service access tokens for the
/// Assistant's outbound calls to sibling internal services
/// (trellis-workflow, future trellis-server, etc.). The original
/// Phase 3.I shape verbatim-forwarded the inbound user JWT, which
/// carried <c>aud=trellis-assistant</c> — siblings validate against
/// their own audience and would 401 every call. The fix mints a token
/// per target resource with the matching <c>aud</c> via OAuth2
/// client-credentials.
///
/// <para>
/// Single-resource scope: one cache entry per <paramref name="targetResource"/>,
/// in-flight refresh serialized per resource. Multi-resource calls
/// (e.g. Assistant → trellis-workflow + Assistant → trellis-server in
/// the same agent run) don't block each other.
/// </para>
///
/// <para>
/// Failure surface: failures throw <see cref="InternalTokenIssuanceException"/>.
/// <see cref="HttpWorkflowClient"/> catches at the call site and surfaces
/// the failure as <see cref="WorkflowScheduleErrorCode.AuthFailed"/> to
/// the LLM (operator-actionable; LLM should NOT retry).
/// </para>
/// </summary>
public interface IInternalTokenIssuer
{
    /// <summary>
    /// Returns a valid access token for the given target resource. Cached
    /// per-resource until the refresh skew window before expiry.
    /// </summary>
    /// <param name="targetResource">The resource value the issuer
    /// includes in the request; the Auth service mints a token with
    /// <c>aud</c> = this value. Use the canonical sibling audience
    /// (e.g. <c>"trellis-workflow"</c>).</param>
    Task<string> GetAccessTokenAsync(string targetResource, CancellationToken ct);
}

/// <summary>
/// Phase 3.I fix-up: thrown by <see cref="IInternalTokenIssuer"/>
/// implementations when the Auth service is unreachable / errors / the
/// configured credentials are rejected. Operator-actionable —
/// downstream tool consumers should surface to the LLM as
/// <see cref="WorkflowScheduleErrorCode.AuthFailed"/> (NOT
/// <c>Transient</c>) so the LLM doesn't retry-loop against a
/// misconfigured deployment.
/// </summary>
public sealed class InternalTokenIssuanceException : Exception
{
    public InternalTokenIssuanceException(string message)
        : base(message) { }

    public InternalTokenIssuanceException(string message, Exception innerException)
        : base(message, innerException) { }
}

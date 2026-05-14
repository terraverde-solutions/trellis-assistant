namespace Trellis.Assistant.AgentExecution;

/// <summary>
/// Phase 3.F: tenant-level identity passed into
/// <see cref="IToolExposurePolicy.IsExposedTo"/> for per-tenant tool
/// gating decisions. Bundles the (TenantId, UserId, TenantRole) tuple
/// that drives the policy's <c>AllowedTenants</c> + <c>RequiredRole</c>
/// gates from <c>Assistant:Tools:PerTool</c> config.
///
/// <para>
/// Lives in Trellis.Assistant.AgentExecution (NOT Trellis.Core) per the
/// single-consumer rule established in Phase 3.A. The only consumer
/// today is <see cref="IToolExposurePolicy"/>; lift to Core when a
/// second component (Workflow, Server, etc.) surfaces. Phase 3.D Q2-1a
/// ratified <c>AgentToolInput.UserId</c> as a primitive-sibling-to-OrgId
/// rather than a Tenancy record — that decision stays; this record is
/// scoped to the policy-input boundary only.
/// </para>
///
/// <para>
/// <see cref="TenantRole"/> is <c>null</c> when the request's JWT (or
/// deprecated <c>X-Trellis-Tenant-Role</c> header) didn't carry a
/// <c>tenant_role</c> claim. The policy fails-closed on null role for
/// any <c>RequiredRole</c> gate — tools that require a specific role
/// are hidden from role-less callers regardless of which other claims
/// they carry. Matches the Macro 2 PR 6.7 "fail-closed on missing
/// claim" defense posture.
/// </para>
/// </summary>
public sealed record AgentToolTenancy(
    Guid TenantId,
    string UserId,
    string? TenantRole);

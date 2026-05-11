using System.ComponentModel.DataAnnotations;

namespace Trellis.Assistant.Services;

/// <summary>
/// Configuration bound from <c>Assistant:Trainer</c> in appsettings.
/// Phase 3.B Trainer integration target — loopback-trust posture against
/// trellis-trainer's <c>GET /api/search</c> endpoint on <c>127.0.0.1:5114</c>
/// (per the Phase 3.B brief; Trainer is single-tenant + no JWT, the kernel's
/// loopback filter is the trust boundary).
///
/// <para>
/// <see cref="BaseUrl"/> is the Trainer's loopback base URL. The
/// search-client URL builder appends <c>api/search?...</c>; the trailing
/// slash on <see cref="BaseUrl"/> matters for proper relative-URI
/// resolution (see <see cref="Uri"/>'s base+relative-URI semantics).
/// </para>
///
/// <para>
/// <see cref="RequestTimeoutSeconds"/> is the per-search wall-clock cap.
/// Trainer's embedding+RRF round-trip on a warm corpus is well under 5s
/// in QA; 30s gives generous cold-start headroom (first embedding model
/// load) without holding the agent loop indefinitely. Surfaces as the
/// <see cref="HttpClient.Timeout"/> on the typed client.
/// </para>
/// </summary>
public sealed class TrainerSearchOptions
{
    public const string SectionName = "Assistant:Trainer";

    [Required]
    public string BaseUrl { get; set; } = "http://127.0.0.1:5114/";

    [Range(1, 600)]
    public int RequestTimeoutSeconds { get; set; } = 30;
}

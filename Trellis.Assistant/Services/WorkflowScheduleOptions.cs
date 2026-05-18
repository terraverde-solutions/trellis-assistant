using System.ComponentModel.DataAnnotations;

namespace Trellis.Assistant.Services;

/// <summary>
/// Phase 3.I: configuration bound from <c>Assistant:Workflow:*</c> for the
/// <see cref="HttpWorkflowClient"/> that talks to trellis-workflow's
/// <c>POST /api/workflows/runs</c> (qwen Phase 4.A). Loopback-trust in
/// dev (workflow service binds <c>127.0.0.1:5118</c>); QA/production
/// overrides via <c>/etc/trellis-assistant-qa.env</c> or
/// <c>appsettings.user.json</c>.
///
/// <para>
/// Two-knob shape mirrors <see cref="TrainerSearchOptions"/>: BaseUrl
/// for the host + RequestTimeoutSeconds for the per-call wall-clock cap.
/// 10s default leaves plenty of headroom for the synchronous schedule
/// branch (Hangfire enqueue is sub-100ms; the response carries an ID
/// the LLM can poll later if needed) while still firing the
/// <c>outcome=timed_out</c> path when qwen is wedged.
/// </para>
/// </summary>
public sealed class WorkflowScheduleOptions
{
    public const string SectionName = "Assistant:Workflow";

    /// <summary>
    /// Base URL for trellis-workflow. Default <c>http://127.0.0.1:5118/</c>
    /// matches the loopback-trust posture (qwen binds 127.0.0.1 only;
    /// kernel filter is the trust boundary). Production override goes
    /// through the deploy env file.
    /// </summary>
    [Required]
    public string BaseUrl { get; set; } = "http://127.0.0.1:5118/";

    /// <summary>
    /// Per-call HTTP timeout in seconds. Maps directly to
    /// <see cref="HttpClient.Timeout"/>; a TaskCanceledException without
    /// the caller's CT being cancelled means qwen exceeded this budget,
    /// surfaced to the LLM as <c>error=transient</c>.
    /// </summary>
    [Range(1, 120)]
    public int RequestTimeoutSeconds { get; set; } = 10;
}

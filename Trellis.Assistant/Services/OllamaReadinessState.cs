namespace Trellis.Assistant.Services;

/// <summary>
/// Readiness state for the Ollama warm-up. Singleton, mutated by
/// <see cref="OllamaWarmupHostedService"/>, read by the
/// <c>/readyz</c> endpoint.
///
/// Liveness vs readiness split — Kubernetes-style. <c>/healthz</c>
/// reports process liveness (always 200 once Kestrel is listening).
/// <c>/readyz</c> reports whether the LLM warm-up has succeeded —
/// 200 once the model is loaded into Ollama, 503 until then.
///
/// Failure mode: the warm-up service retries forever on backoff.
/// If Ollama is unreachable or the configured warm-up model isn't
/// loaded, /readyz stays 503 indefinitely + the host keeps logging
/// retry warnings. /healthz stays 200 — the process is alive,
/// operators can investigate. User requests fall through lazily;
/// the first user-facing turn pays the cold-load cost.
/// </summary>
public sealed class OllamaReadinessState
{
    private int _ready;

    /// <summary>
    /// True once the warm-up has completed at least once successfully.
    /// Once set true, it never goes back to false — a previously-
    /// reachable Ollama that goes down mid-run doesn't flip the
    /// readiness signal back; per-request cancellation + 504 surface
    /// that operationally instead.
    /// </summary>
    public bool IsReady => Volatile.Read(ref _ready) != 0;

    /// <summary>
    /// Marks the assistant ready. Idempotent — multiple successful
    /// warm-ups (e.g. on a future re-warmup-on-config-change path)
    /// don't disturb the signal.
    /// </summary>
    public void MarkReady() => Volatile.Write(ref _ready, 1);
}

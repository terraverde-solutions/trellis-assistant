using Microsoft.Extensions.Options;
using Trellis.Core.Models;
using Trellis.Core.Services;

namespace Trellis.Assistant.Services;

/// <summary>
/// Background warm-up loop. On host start, calls the Ollama server with
/// a tiny prompt against the configured warm-up model so the model is
/// VRAM-resident by the time the first user turn arrives.
///
/// Cold Ollama 70B takes 60–90s to load. Without warm-up the first
/// real user request after a deploy or restart suffers that latency
/// directly. With warm-up, /readyz reports 200 once the model is
/// loaded; ops scripts can wait on /readyz before declaring "deploy
/// live" and the first user-facing request lands on a warm model.
///
/// Failure shape (load-bearing):
/// - Each warm-up attempt is bounded by a wall-clock cap (matches
///   <see cref="ConversationOrchestratorOptions.TurnRequestTimeoutSeconds"/>
///   — same upper bound as a real user turn). Stuck Ollama → cancel
///   the call, log, back off, retry. Never pin the host indefinitely.
/// - Retries follow an exponential backoff capped at 60s
///   (10 → 30 → 60 → 60 → ...). Permanently-broken Ollama keeps
///   logging warnings every 60s; doesn't slow into hour-scale gaps.
/// - <see cref="OllamaReadinessState.MarkReady"/> is set ONLY after
///   the enumerable completes successfully (Ollama returned without
///   error). Any failure leaves /readyz at 503.
/// - The host's <c>ApplicationStopping</c> CT cancels in-flight
///   warm-up calls cleanly on shutdown — the streaming reader inside
///   <see cref="OllamaClient"/> honors CT on every line read.
///
/// Lazy fallback: the user-facing path doesn't wait for /readyz.
/// If warm-up never succeeds, the first turn still hits Ollama,
/// suffers the cold-load, and (assuming it succeeds) sets
/// /readyz ready as a side effect of the model being loaded —
/// not by this hosted service, which is unaware of user-driven
/// model loads. The intentional split: warm-up is best-effort;
/// user requests are not gated on it.
/// </summary>
public sealed class OllamaWarmupHostedService : BackgroundService
{
    // Backoff schedule. Cap at 60s so a permanently-broken Ollama
    // logs at most once per minute rather than slowly stretching to
    // hour-scale gaps.
    private static readonly TimeSpan[] BackoffSchedule =
    {
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    };

    private readonly IServiceProvider _services;
    private readonly OllamaReadinessState _readiness;
    private readonly IOptions<ConversationOrchestratorOptions> _options;
    private readonly ILogger<OllamaWarmupHostedService> _logger;

    public OllamaWarmupHostedService(
        IServiceProvider services,
        OllamaReadinessState readiness,
        IOptions<ConversationOrchestratorOptions> options,
        ILogger<OllamaWarmupHostedService> logger)
    {
        _services = services;
        _readiness = readiness;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;
        var warmupModel = options.WarmupModel;
        var perAttemptCap = TimeSpan.FromSeconds(options.TurnRequestTimeoutSeconds);

        if (string.IsNullOrWhiteSpace(warmupModel))
        {
            _logger.LogInformation(
                "Ollama warm-up disabled (Assistant:WarmupModel is unset); /readyz will stay 503 until set or until the user-facing path warms a model lazily.");
            return;
        }

        var attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            attempt++;
            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                attemptCts.CancelAfter(perAttemptCap);

                _logger.LogInformation(
                    "Ollama warm-up attempt {Attempt}: pinging model {Model} (cap {CapSeconds}s).",
                    attempt, warmupModel, (int)perAttemptCap.TotalSeconds);

                await WarmupOnceAsync(warmupModel, attemptCts.Token).ConfigureAwait(false);

                _readiness.MarkReady();
                _logger.LogInformation(
                    "Ollama warm-up complete after {Attempt} attempt(s); /readyz now 200.",
                    attempt);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host shutting down; bail cleanly.
                return;
            }
            catch (Exception ex)
            {
                var delay = BackoffFor(attempt);
                _logger.LogWarning(ex,
                    "Ollama warm-up attempt {Attempt} failed; retrying in {DelaySeconds}s. /readyz stays 503 until success.",
                    attempt, (int)delay.TotalSeconds);

                try
                {
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task WarmupOnceAsync(string warmupModel, CancellationToken cancellationToken)
    {
        // Resolve a fresh IOllamaClient per attempt. The DI registration
        // for the typed client is transient (HttpClientFactory pattern),
        // so each warm-up gets its own HttpClient with the latest
        // socket-recycle state.
        await using var scope = _services.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<IOllamaClient>();

        // Tiny prompt — just enough to force the model into VRAM. The
        // single ChatMessage bypasses any conversation history loading
        // (we're warming the model, not running a real turn).
        var history = new[]
        {
            new ChatMessage { Role = ChatRole.User, Content = "ping" },
        };

        // Drain the streaming response. We don't care about the content;
        // we just need the model to be loaded. Ollama returns success
        // once the model is resident, so the enumerable completing
        // successfully IS the readiness signal.
        await foreach (var _ in client.StreamChatAsync(warmupModel, history, cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            // Discard chunks; we only need the stream to complete.
        }
    }

    private static TimeSpan BackoffFor(int attempt)
    {
        // attempts 1, 2, 3+ → 10s, 30s, 60s (and 60s thereafter).
        var idx = Math.Min(attempt - 1, BackoffSchedule.Length - 1);
        return BackoffSchedule[idx];
    }
}

using System.Runtime.CompilerServices;
using Trellis.Core.Models;
using Trellis.Core.Services;

namespace Trellis.Assistant.Services;

/// <summary>
/// Phase-1 stub <see cref="IOllamaClient"/> — yields canned text chunks so
/// the orchestrator's read-history → call-LLM → persist-turns flow can be
/// exercised end-to-end without GB10 / Ollama dependencies.
///
/// Phase 2 swaps the DI registration to <c>OllamaClient</c> from
/// <c>Trellis.Core.Services</c> (the production impl pointed at
/// <c>Gateway:BaseUrl</c> via <c>IHttpClientFactory</c>). Zero
/// orchestrator code change at the swap — both impls satisfy the same
/// <see cref="IOllamaClient"/> contract.
///
/// Faithful to today's interface contract (verified against
/// <c>c:/dev/trellis-kimi/core/Trellis.Core/Services/IOllamaClient.cs:20</c>):
/// <c>StreamChatAsync</c> returns <see cref="IAsyncEnumerable{T}"/> of
/// plain <see cref="string"/> — no <c>OllamaChatChunk</c> envelope, no
/// "done" sentinel, no token-count metadata. End-of-stream = enumerable
/// completes. Phase 1 tests that need turn-emit-time metadata
/// (chunk count, wall-clock duration) compute it Assistant-side via
/// <see cref="System.Diagnostics.Stopwatch"/> + chunk counting.
/// IOllamaClient evolution is a separate cross-cutting decision —
/// Phase 1 does not drive it.
///
/// Stub behaviour:
///   - Yields 5 chunks of canned text in roughly 50ms-spaced flush
///     intervals so streaming-aware code (Phase 4+ channel adapters,
///     SignalR push) is exercised at small but realistic cadence.
///   - Token shape: spaces between chunks, period at the end — looks
///     like a normal completion in trace logs.
///   - Honors the cancellation token between chunks. The
///     <see cref="ListModelsAsync"/> impl returns an empty list
///     (Phase 1 callers don't enumerate models; Phase 2's real client
///     surfaces the actual loaded models from Ollama).
/// </summary>
public sealed class StubLlmClient : IOllamaClient
{
    /// <summary>
    /// Per-chunk delay so a streaming consumer sees realistic cadence,
    /// not all 5 chunks landing in one CPU tick. 40 ms chunk-to-chunk =
    /// ~25 chunks/sec, well within Ollama's typical 5-50 tokens/sec
    /// streaming rate. Public so tests can override it via reflection or
    /// (cleaner) by injecting a configured instance.
    /// </summary>
    public TimeSpan PerChunkDelay { get; init; } = TimeSpan.FromMilliseconds(40);

    public Task<IReadOnlyList<OllamaModel>> ListModelsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<OllamaModel>>(Array.Empty<OllamaModel>());

    public async IAsyncEnumerable<string> StreamChatAsync(
        string model,
        IReadOnlyList<ChatMessage> history,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(history);

        // Five canned chunks. Concatenated: "Stub assistant reply for testing the orchestrator pipeline ."
        // The trailing space-period is an artefact of chunk boundaries — production Ollama emits similar
        // streaming artefacts. The orchestrator's persisted Content is the concatenation of all chunks
        // unmodified; consumers do their own trimming/joining.
        var chunks = new[]
        {
            "Stub ",
            "assistant reply ",
            "for testing the ",
            "orchestrator pipeline",
            ".",
        };

        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Delay BEFORE yielding so cancellation lands cleanly. If we
            // yielded first then delayed, an aborting caller would have
            // already received the chunk + the next iteration's cancellation
            // throw would lose nothing — but the symmetry of "delay then
            // yield" is easier to reason about for the orchestrator's
            // Stopwatch-based duration measurement in tests.
            if (PerChunkDelay > TimeSpan.Zero)
            {
                await Task.Delay(PerChunkDelay, cancellationToken).ConfigureAwait(false);
            }
            yield return chunk;
        }
    }
}

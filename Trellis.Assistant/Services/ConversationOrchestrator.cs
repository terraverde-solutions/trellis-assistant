using System.Text;
using Trellis.Core.Models;
using Trellis.Core.Services;

namespace Trellis.Assistant.Services;

/// <summary>
/// Phase 1 conversation orchestrator. Single responsibility: take a new
/// user message on an existing conversation, fetch history, call the LLM,
/// persist the user + assistant turn pair atomically, and return both
/// turns to the caller.
///
/// Phase 3+ extends this to dispatch tools between LLM calls; Phase 4+
/// adds channel-adapter inbound entry points; Phase 5+ swaps the auth
/// posture from header-trust to JWT-claim extraction. Phase 1 keeps the
/// surface lean: one method (<see cref="HandleUserTurnAsync"/>) +
/// dependency-injected <see cref="IAssistantConversationStore"/> +
/// <see cref="IOllamaClient"/>.
///
/// CONCURRENCY CONTRACT — load-bearing.
///
/// The per-conversation Postgres advisory lock is acquired INSIDE
/// <see cref="IAssistantConversationStore.AppendTurnsAsync"/>, covering
/// the position read + the INSERT pair. This serializes position
/// assignment and prevents unique-constraint races on
/// (conversation_id, position).
///
/// It does NOT serialize history reads or LLM calls: two concurrent
/// requests on the same conversation can read the same history snapshot
/// and call the LLM concurrently. For Phase 1 this is acceptable —
/// the stub LLM result is independent of history, and each append
/// writes an atomic user+assistant pair at a unique position, so
/// the persisted shape is always alternating user/assistant from an
/// even starting position regardless of read-history interleaving.
///
/// Phase 3 (tool dispatch) MUST re-evaluate this boundary: if
/// tool-dispatch rounds need to observe each other's intermediate
/// turns, the lock scope must expand to the orchestrator layer
/// (open the transaction in the orchestrator, hold for the full
/// sequence, AppendTurnsAsync becomes a persist-only step inside
/// the orchestrator's broader transaction).
///
/// The lock + the per-request timeout ARE NOT separable. ASP.NET
/// Core's default per-request timeout is "no timeout"; without a
/// wall-clock budget, a stuck LLM call holds the advisory lock
/// (on the eventual AppendTurnsAsync) until the connection drops.
/// The endpoint MUST configure an explicit per-request CT deadline:
///   - Phase 1: 60 s (well above the stub's ~200 ms 5-chunk yield budget)
///   - Phase 2: 180 s (real Ollama; cold-loading a 70B model can take
///     &gt;60 s on first call)
/// On timeout, the linked CT trips, the LLM call cancels, the
/// in-flight AppendTurnsAsync transaction (if running) aborts and
/// releases the advisory lock, the next queued append proceeds.
///
/// Pin: ConversationEndpointTests
/// .PostTurns_ConcurrentRequestsOnSameConversation_PreserveOrderingAndUniqueness
/// covers concurrent requests on the same conversation; that test
/// passes precisely because each append writes a contiguous
/// (user, assistant) pair under the lock, not because the lock
/// holds across the history-read + LLM call.
/// </summary>
public sealed class ConversationOrchestrator
{
    private readonly IAssistantConversationStore _store;
    private readonly IOllamaClient _llm;

    public ConversationOrchestrator(
        IAssistantConversationStore store,
        IOllamaClient llm)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
    }

    /// <summary>
    /// Append a user turn + a stub-LLM-generated assistant turn to the
    /// conversation, atomically. Returns both turns with their assigned
    /// positions populated.
    /// </summary>
    /// <param name="tenantId">Tenant identifier from the request context
    /// (trusted in Phase 1 from <c>X-Trellis-Tenant-Id</c> header; trusted
    /// in Phase 5+ from JWT claims).</param>
    /// <param name="userId">User identifier from the same source.</param>
    /// <param name="conversationId">Existing conversation (caller must
    /// have created it via <see cref="IAssistantConversationStore.CreateConversationAsync"/>).</param>
    /// <param name="userContent">The new user message text.</param>
    /// <param name="cancellationToken">Per-request CT — endpoint layer
    /// composes this from <c>HttpContext.RequestAborted</c> +
    /// the explicit timeout
    /// (<see cref="ConversationOrchestratorOptions.TurnRequestTimeoutSeconds"/>;
    /// Phase 2 default 180s).</param>
    public async Task<TurnPairResult> HandleUserTurnAsync(
        string tenantId,
        string userId,
        Guid conversationId,
        string userContent,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userContent);

        // Verify the conversation exists + is owned by (tenantId, userId)
        // up-front. AppendTurnsAsync re-checks inside its transaction
        // (race-safe), but failing fast here keeps the LLM call from
        // running pointlessly when the conversation is missing.
        var conv = await _store
            .GetConversationAsync(tenantId, userId, conversationId, cancellationToken)
            .ConfigureAwait(false);
        if (conv is null)
        {
            throw new InvalidOperationException(
                $"Conversation {conversationId} not found for tenant/user.");
        }

        // Read history. Phase 1 sends every prior turn to the LLM; Phase 2+
        // adds context-window-aware truncation when histories grow long.
        var prior = await _store
            .GetTurnsAsync(tenantId, userId, conversationId, cancellationToken)
            .ConfigureAwait(false);

        // Build the LLM prompt: prior turns + the new user message. The
        // ChatMessage type is Trellis.Core's wire shape; long Ids in those
        // fields are local-to-the-LLM-call and don't reference Assistant's
        // ulid-based DB ids.
        var llmHistory = new List<ChatMessage>(prior.Count + 1);
        foreach (var t in prior)
        {
            llmHistory.Add(new ChatMessage
            {
                Role = ToCoreRole(t.Role),
                Content = t.Content,
            });
        }
        llmHistory.Add(new ChatMessage
        {
            Role = ChatRole.User,
            Content = userContent,
        });

        // Stream the LLM response into a buffer. The stub yields 5 chunks
        // ~40ms apart; production Ollama yields tokens at 5-50/sec. The
        // orchestrator persists the concatenated full response — Phase 2
        // doesn't push streaming chunks back to the HTTP caller (that's
        // a Phase 3+ channel-adapter concern). The endpoint returns the
        // full assembled assistant reply.
        //
        // Model selection: per-conversation, pinned at create time. The
        // conversation row's Model column was assigned by the storage
        // layer (caller-supplied at POST /api/conversations or the
        // column DEFAULT). Phase 3+ may add a re-pin operation if
        // model-switching mid-conversation becomes a real need; Phase 2
        // is single-model-per-conversation.
        var assistantBuffer = new StringBuilder();
        await foreach (var chunk in _llm.StreamChatAsync(conv.Model, llmHistory, cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            assistantBuffer.Append(chunk);
        }
        var assistantContent = assistantBuffer.ToString();

        // Persist user + assistant turns in one AppendTurnsAsync call.
        // The store acquires the per-conversation advisory lock around
        // the position read + INSERT pair (NOT around the history read
        // or LLM call above — see CONCURRENCY CONTRACT in this class's
        // XML doc). Positions are computed under the lock, so concurrent
        // requests can't race the unique constraint on
        // (conversation_id, position).
        var newTurns = await _store
            .AppendTurnsAsync(
                tenantId,
                userId,
                conversationId,
                new[]
                {
                    new NewAssistantTurn(AssistantTurnRole.User, userContent),
                    new NewAssistantTurn(AssistantTurnRole.Assistant, assistantContent),
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (newTurns.Count != 2)
        {
            // Defensive — AppendTurnsAsync's contract is "returns the
            // persisted turns in input order." A regression in the impl
            // returning fewer or differently-ordered turns would corrupt
            // the response shape; this assertion catches it loudly.
            throw new InvalidOperationException(
                $"Expected 2 persisted turns (user + assistant), got {newTurns.Count}.");
        }

        return new TurnPairResult(UserTurn: newTurns[0], AssistantTurn: newTurns[1]);
    }

    private static ChatRole ToCoreRole(AssistantTurnRole role) => role switch
    {
        AssistantTurnRole.User => ChatRole.User,
        AssistantTurnRole.Assistant => ChatRole.Assistant,
        AssistantTurnRole.System => ChatRole.System,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown role"),
    };
}

/// <summary>
/// Configuration bound from <c>Assistant:*</c> in appsettings.
///
/// <para><see cref="TurnRequestTimeoutSeconds"/> is read as <c>int</c>
/// (not <see cref="TimeSpan"/>) on purpose — culture-dependent
/// <see cref="TimeSpan"/> parsing in <see cref="IConfiguration"/> is a
/// foot-gun for QA + production deploys where the system culture may
/// differ from dev. Integer seconds is unambiguous everywhere. The
/// endpoint converts to <see cref="TimeSpan"/> at the single use
/// site.</para>
///
/// <para><see cref="WarmupModel"/> drives
/// <see cref="OllamaWarmupHostedService"/>'s tiny ping at startup
/// to force the model into VRAM. Per-conversation model selection
/// (Phase 2 Q4=B) means there's no single "default model" anymore;
/// the warm-up model is a hint for the most-likely-needed model.
/// First user request to a different model still pays the cold-load
/// cost lazily. Set to empty/whitespace to disable warm-up entirely
/// (e.g. dev environments without Ollama running).</para>
/// </summary>
public sealed class ConversationOrchestratorOptions
{
    public const string SectionName = "Assistant";

    /// <summary>
    /// Wall-clock budget for a single POST /turns request. Bounds how
    /// long the per-conversation Postgres advisory lock can be held;
    /// see <see cref="ConversationOrchestrator"/>'s CONCURRENCY
    /// CONTRACT for why the lock + the timeout are not separable.
    /// Phase 2 default 180s — sized for cold 70B model load (60-90s)
    /// + token-heavy responses (60-90s additional). Phase 1 used 60s
    /// against the stub (which yielded ~200ms).
    /// </summary>
    public int TurnRequestTimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// LLM model tag for the startup warm-up call. Empty/whitespace
    /// disables warm-up; /readyz then stays 503 until the user-facing
    /// path warms a model lazily (which doesn't actually flip /readyz
    /// — see <see cref="OllamaReadinessState"/> docs). Operators should
    /// set this to whichever model is most-frequently-used on this
    /// instance; on GB10 production that's <c>mistral-small:24b</c>
    /// (matches the Trellis.Gateway allowlist).
    /// </summary>
    public string WarmupModel { get; set; } = "";
}

/// <summary>
/// Output of <see cref="ConversationOrchestrator.HandleUserTurnAsync"/>.
/// Both turns are populated with their persisted positions + ids.
/// </summary>
public sealed record TurnPairResult(
    AssistantTurn UserTurn,
    AssistantTurn AssistantTurn);

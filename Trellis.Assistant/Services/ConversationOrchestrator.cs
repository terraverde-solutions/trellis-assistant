using System.Text;
using Trellis.Assistant.AgentExecution;
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
    private readonly AssistantAgentExecutor _agentExecutor;

    public ConversationOrchestrator(
        IAssistantConversationStore store,
        IOllamaClient llm,
        AssistantAgentExecutor agentExecutor)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _agentExecutor = agentExecutor ?? throw new ArgumentNullException(nameof(agentExecutor));
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
        IReadOnlyList<string>? toolNameFilter = null,
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

        // Phase 3.A.2: route through the agent executor when the request
        // specifies a tools filter. Non-null + non-empty → agent path.
        // Null/empty → existing Phase 2 direct-LLM path (preserved).
        var useAgentPath = toolNameFilter is { Count: > 0 };
        if (useAgentPath)
        {
            return await HandleAgentPathAsync(
                tenantId, userId, conversationId, conv, prior, userContent,
                toolNameFilter!, cancellationToken).ConfigureAwait(false);
        }

        return await HandleDirectLlmPathAsync(
            tenantId, userId, conversationId, conv, prior, userContent,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Phase 1+2 direct-LLM path. The conversation's pinned model
    /// (Phase 2's per-conversation Model column) drives the call;
    /// no tool dispatch. Persists [user, assistant] atomically.
    /// </summary>
    private async Task<TurnPairResult> HandleDirectLlmPathAsync(
        string tenantId,
        string userId,
        Guid conversationId,
        AssistantConversation conv,
        IReadOnlyList<AssistantTurn> prior,
        string userContent,
        CancellationToken cancellationToken)
    {
        // Build the LLM prompt: prior turns + the new user message.
        var llmHistory = new List<ChatMessage>(prior.Count + 1);
        foreach (var t in prior)
        {
            llmHistory.Add(ToChatMessage(t));
        }
        llmHistory.Add(new ChatMessage
        {
            Role = ChatRole.User,
            Content = userContent,
        });

        // Stream the LLM response into a buffer. Per-conversation model
        // (Phase 2 Q4=B) drives the call.
        var assistantBuffer = new StringBuilder();
        await foreach (var chunk in _llm.StreamChatAsync(conv.Model, llmHistory, cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            assistantBuffer.Append(chunk);
        }
        var assistantContent = assistantBuffer.ToString();

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
            throw new InvalidOperationException(
                $"Expected 2 persisted turns (user + assistant), got {newTurns.Count}.");
        }

        return new TurnPairResult(UserTurn: newTurns[0], AssistantTurn: newTurns[1]);
    }

    /// <summary>
    /// Phase 3.A.2 agent-integrated path. Routes through
    /// <see cref="AssistantAgentExecutor.RunForConversationAsync"/>;
    /// persists [user, ...tool turns, assistant] atomically as one
    /// batch via <see cref="IAssistantConversationStore.AppendTurnsAsync"/>.
    ///
    /// <para>
    /// Note: the agent executor's LLM call uses the global
    /// <c>Assistant:Agent:Model</c> setting (default qwen2.5:72b — a
    /// known tool-supporting model), NOT the conversation's pinned
    /// Model. The conversation's Model is for direct-LLM calls; the
    /// agent path is tool-aware and needs a function-calling-capable
    /// model.
    /// </para>
    /// </summary>
    private async Task<TurnPairResult> HandleAgentPathAsync(
        string tenantId,
        string userId,
        Guid conversationId,
        AssistantConversation conv,
        IReadOnlyList<AssistantTurn> prior,
        string userContent,
        IReadOnlyList<string> toolNameFilter,
        CancellationToken cancellationToken)
    {
        // Build messages from prior turns + new user message. Same
        // shape as the direct-LLM path; the executor adds its own
        // system prompt internally.
        var messages = new List<ChatMessage>(prior.Count + 1);
        foreach (var t in prior)
        {
            messages.Add(ToChatMessage(t));
        }
        messages.Add(new ChatMessage
        {
            Role = ChatRole.User,
            Content = userContent,
        });

        // OrgId derivation: same Phase 3.A C1 contract as POST /api/agent-runs.
        // The endpoint layer pre-validated the tenantId is uuid-shaped; here
        // we Guid.Parse + fail loud if somehow not.
        if (!Guid.TryParse(tenantId, out var orgId) || orgId == Guid.Empty)
        {
            throw new InvalidOperationException(
                $"TenantId '{tenantId}' is not uuid-shaped — agent-path requires uuid tenantIds per Phase 3.A C1 contract. Endpoint layer should have rejected this earlier.");
        }

        // Build the tool descriptor catalogue from the registered tool set
        // intersected with the caller's filter. The executor filters again
        // internally (resolvableTools), so a name in the filter that isn't
        // registered just gets dropped by the planner side.
        var allTools = _agentExecutor is { } _;  // suppress unused-var warning; _agentExecutor used below
        var availableTools = BuildToolCatalogue(toolNameFilter);

        var result = await _agentExecutor.RunForConversationAsync(
            orgId: orgId,
            assistantTurnId: null,  // Phase 3.A.2 doesn't pre-allocate; the user_turn id is generated by the store under the advisory lock
            userPrompt: userContent,
            messages: messages,
            availableTools: availableTools,
            budgetOverrides: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Build the new-turns batch: user, then one Tool turn per
        // dispatch (in order), then the final assistant turn.
        // AppendTurnsAsync persists them atomically with sequential
        // positions under the per-conversation advisory lock.
        var newTurnInputs = new List<NewAssistantTurn>(2 + result.ToolDispatches.Count)
        {
            new(AssistantTurnRole.User, userContent),
        };
        foreach (var dispatch in result.ToolDispatches)
        {
            newTurnInputs.Add(new NewAssistantTurn(
                AssistantTurnRole.Tool,
                dispatch.ResultContent,
                dispatch.ToolCallId,
                dispatch.ToolName));
        }
        // The final assistant turn carries the model's terminal response
        // text. If the executor halted before the model emitted a final
        // text (cap_reached / loop_detected / failed / cancelled), the
        // FinalAssistantText is empty — we still persist a placeholder
        // assistant turn so the response shape stays consistent + the
        // client can surface the run's terminal status separately.
        var assistantContent = string.IsNullOrEmpty(result.FinalAssistantText)
            ? $"[agent terminated: {result.AgentRun.Status}]"
            : result.FinalAssistantText;
        newTurnInputs.Add(new NewAssistantTurn(AssistantTurnRole.Assistant, assistantContent));

        var newTurns = await _store
            .AppendTurnsAsync(tenantId, userId, conversationId, newTurnInputs, cancellationToken)
            .ConfigureAwait(false);

        var expectedCount = 2 + result.ToolDispatches.Count;
        if (newTurns.Count != expectedCount)
        {
            throw new InvalidOperationException(
                $"Expected {expectedCount} persisted turns (user + {result.ToolDispatches.Count} tool turns + assistant), got {newTurns.Count}.");
        }

        // Slice: index 0 = user, [1..N+1) = tool turns, last = assistant.
        var userTurn = newTurns[0];
        var assistantTurn = newTurns[^1];
        var toolTurns = result.ToolDispatches.Count == 0
            ? Array.Empty<AssistantTurn>()
            : newTurns.Skip(1).Take(result.ToolDispatches.Count).ToArray();

        return new TurnPairResult(userTurn, assistantTurn) { ToolTurns = toolTurns };
    }

    /// <summary>
    /// Build the agent-path tool descriptor catalogue. The orchestrator
    /// only knows tool NAMES from the request body; it asks the executor
    /// (via the registry surface) to resolve them. Phase 3.A.2 keeps this
    /// simple: pass an empty descriptor list when the filter is empty
    /// (lets the executor's resolvableTools filter handle the rest).
    /// </summary>
    private static IReadOnlyList<AgentToolDescriptor> BuildToolCatalogue(
        IReadOnlyList<string> filter)
    {
        // The executor filters internally based on registered tool names.
        // We pass placeholder descriptors carrying the requested names —
        // the executor's `resolvableTools` filter intersects with the
        // actual registry, so unknown names get dropped before the LLM
        // sees them. ParameterSchema is filled by the executor at the
        // tools-array build site (OllamaAgentLlmClient pulls from each
        // registered IAgentTool's Descriptor when constructing the
        // function-calling wire body — placeholder ParameterSchema
        // here is overridden when the registry resolves the actual tool).
        //
        // Phase 3.B may surface a richer "filter by name" call on the
        // registry directly to avoid this placeholder shape. For 3.A.2,
        // the placeholder works because OllamaAgentLlmClient looks up
        // descriptors by name when building the wire body.
        return filter.Select(name => new AgentToolDescriptor
        {
            Name = name,
            Description = $"(filter placeholder for '{name}'; executor resolves via registry)",
            ParameterSchema = """{"type":"object"}""",
            Category = AgentToolCategory.Inspect,
        }).ToList();
    }

    private static ChatMessage ToChatMessage(AssistantTurn turn)
    {
        return new ChatMessage
        {
            Role = ToCoreRole(turn.Role),
            Content = turn.Content,
        };
    }

    private static ChatRole ToCoreRole(AssistantTurnRole role) => role switch
    {
        AssistantTurnRole.User => ChatRole.User,
        AssistantTurnRole.Assistant => ChatRole.Assistant,
        AssistantTurnRole.System => ChatRole.System,
        // Phase 3.A.2: Tool turns carry tool result content. Map to
        // ChatRole.System for the LLM context build — Trellis.Core's
        // ChatRole enum (Phase 1+2 wire shape for IOllamaClient.StreamChatAsync)
        // doesn't have a Tool value, and IAgentLlmClient's tools-array
        // protocol doesn't strictly require role=tool messages on the
        // wire (Ollama accepts the result as a system-role envelope).
        // Phase 3.B may widen ChatRole if model behaviour quality
        // benefits from explicit role=tool messages.
        AssistantTurnRole.Tool => ChatRole.System,
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
/// Both required turns are populated with their persisted positions + ids.
///
/// <para>
/// Phase 3.A.2: <see cref="ToolTurns"/> defaults to an empty list for
/// the Phase 1+2 direct-LLM path (no tool dispatches). When the agent
/// path runs (caller specified a tools filter), this carries the
/// Role=Tool turns persisted between the user + final assistant turns,
/// in the order the executor dispatched them. Positions are contiguous
/// across [UserTurn, ...ToolTurns, AssistantTurn].
/// </para>
/// </summary>
public sealed record TurnPairResult(
    AssistantTurn UserTurn,
    AssistantTurn AssistantTurn)
{
    public IReadOnlyList<AssistantTurn> ToolTurns { get; init; } = Array.Empty<AssistantTurn>();
}

using Trellis.Core.Models;

namespace Trellis.Assistant.AgentExecution;

/// <summary>
/// Function-calling LLM surface for the agent execution loop. Distinct
/// from <see cref="Trellis.Core.Services.IOllamaClient"/> on purpose:
/// <see cref="Trellis.Core.Services.IOllamaClient"/> streams plain text
/// (<see cref="IAsyncEnumerable{String}"/>) — the contract Phase 1+2's
/// conversation orchestrator depends on. Function-calling needs a
/// structured response that distinguishes "model emitted text" from
/// "model emitted tool_calls" — different streaming semantics, different
/// JSON parsing on the wire.
///
/// <para>
/// Phase 3.A.1 ships this interface in
/// <see cref="Trellis.Assistant.AgentExecution"/> rather than lifting it
/// to Trellis.Core. The lift to Core happens when a SECOND consumer
/// shows up (qwen's Workflow side will likely need it for tool-aware
/// LLM calls — Phase B onwards). One-consumer Core widening is the
/// trap qwen's DefaultBudgetGate-location ratification narrowly avoided;
/// Phase 3.A applies the same principle.
/// </para>
///
/// <para>
/// The Ollama-backed impl uses the same <c>POST /api/chat</c> endpoint
/// as <see cref="Trellis.Core.Services.OllamaClient"/> but adds a
/// <c>tools</c> array to the request body + parses
/// <c>message.tool_calls</c> from the (final) NDJSON chunk in the
/// response. Function-calling responses don't stream chunk-by-chunk
/// the way text does — Ollama emits the full tool_calls array in one
/// final message.
/// </para>
/// </summary>
public interface IAgentLlmClient
{
    /// <summary>
    /// Send a chat request with the given tool catalogue. Returns one
    /// of two response shapes:
    /// <list type="bullet">
    /// <item><c>AssistantText</c> populated, <c>ToolCalls</c> empty —
    /// the model produced a final assistant turn with no tool dispatch
    /// requested. Executor terminates the loop + persists the
    /// assistant turn.</item>
    /// <item><c>ToolCalls</c> populated, <c>AssistantText</c> may be
    /// empty or carry the model's reasoning prelude — the model
    /// requested one or more tool dispatches. Executor splits each
    /// into a separate <see cref="AgentStep"/> and continues the loop
    /// after each tool returns its result.</item>
    /// </list>
    /// </summary>
    Task<AgentLlmResponse> ChatWithToolsAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<AgentToolDescriptor> tools,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Response from <see cref="IAgentLlmClient.ChatWithToolsAsync"/>.
/// Discriminated by <see cref="ToolCalls"/> count: zero = final
/// assistant turn; one or more = tool dispatch requests.
/// </summary>
public sealed record AgentLlmResponse
{
    /// <summary>
    /// Model's assistant text. Populated when the model produces a final
    /// turn; may also be populated alongside ToolCalls if the model
    /// emits a "thinking out loud" prelude before the tool requests.
    /// </summary>
    public string AssistantText { get; init; } = "";

    /// <summary>
    /// Tool call requests emitted by the model. Empty list = no tool
    /// dispatch; non-empty = each entry becomes one
    /// <see cref="AgentStep"/>.
    /// </summary>
    public IReadOnlyList<AgentLlmToolCall> ToolCalls { get; init; } = Array.Empty<AgentLlmToolCall>();

    /// <summary>
    /// Model-reported total tokens used for this call (prompt + completion).
    /// 0 when the model didn't report usage. The executor accumulates
    /// across the run for <see cref="AgentRun.TokensUsed"/>.
    /// </summary>
    public long TokensUsed { get; init; }
}

/// <summary>
/// One tool call emitted by the model. <see cref="ToolName"/> resolves
/// against the tool registry (Descriptor.Name);
/// <see cref="ArgumentsJson"/> is the JSON-serialized parameter object
/// the executor passes to <see cref="Trellis.Core.Services.IAgentTool.RunAsync"/>.
/// </summary>
public sealed record AgentLlmToolCall
{
    public required string ToolName { get; init; }

    public required string ArgumentsJson { get; init; }
}

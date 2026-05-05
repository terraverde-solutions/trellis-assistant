using Trellis.Assistant.AgentExecution;
using Trellis.Core.Models;

namespace Trellis.Assistant.Tests.TestFixtures;

/// <summary>
/// Test double for <see cref="IAgentLlmClient"/>. Returns a pre-canned
/// sequence of <see cref="AgentLlmResponse"/> values, one per call.
/// Records every invocation in <see cref="Calls"/> for assertions.
///
/// <para>
/// Used by <c>AgentExecutorTests</c> + <c>AgentRunEndpointTests</c> to
/// exercise the executor loop without depending on a real Ollama. The
/// X1 split: stub-driven tests verify the executor + budget gate + tool
/// registry invariants; OLLAMA_BASE_URL-gated <c>RealOllamaSmokeTests</c>
/// verify the real-LLM path.
/// </para>
/// </summary>
public sealed class StubAgentLlmClient : IAgentLlmClient
{
    private readonly Queue<AgentLlmResponse> _scriptedResponses = new();

    public List<RecordedCall> Calls { get; } = new();

    /// <summary>Last-resort default when the script is exhausted: empty assistant text + no tool calls (ends the loop).</summary>
    public AgentLlmResponse FallbackResponse { get; init; } = new()
    {
        AssistantText = "stub fallback (script exhausted)",
        ToolCalls = Array.Empty<AgentLlmToolCall>(),
        TokensUsed = 0,
    };

    public StubAgentLlmClient EnqueueResponse(AgentLlmResponse response)
    {
        _scriptedResponses.Enqueue(response);
        return this;
    }

    /// <summary>Convenience: enqueue a pure-text response (loop terminates after this).</summary>
    public StubAgentLlmClient EnqueueAssistantText(string text, long tokensUsed = 0)
        => EnqueueResponse(new AgentLlmResponse
        {
            AssistantText = text,
            ToolCalls = Array.Empty<AgentLlmToolCall>(),
            TokensUsed = tokensUsed,
        });

    /// <summary>Convenience: enqueue a single tool_call response.</summary>
    public StubAgentLlmClient EnqueueToolCall(string toolName, string argumentsJson, long tokensUsed = 0)
        => EnqueueResponse(new AgentLlmResponse
        {
            AssistantText = "",
            ToolCalls = new[] { new AgentLlmToolCall { ToolName = toolName, ArgumentsJson = argumentsJson } },
            TokensUsed = tokensUsed,
        });

    public Task<AgentLlmResponse> ChatWithToolsAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<AgentToolDescriptor> tools,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(new RecordedCall(
            Model: model,
            MessageCount: messages.Count,
            ToolCount: tools.Count,
            LastMessageContent: messages.Count > 0 ? messages[^1].Content : ""));
        var response = _scriptedResponses.Count > 0 ? _scriptedResponses.Dequeue() : FallbackResponse;
        return Task.FromResult(response);
    }

    public sealed record RecordedCall(string Model, int MessageCount, int ToolCount, string LastMessageContent);
}

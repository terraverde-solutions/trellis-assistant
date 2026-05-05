using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trellis.Core.Models;

namespace Trellis.Assistant.AgentExecution;

/// <summary>
/// Ollama-backed <see cref="IAgentLlmClient"/>. Calls the same
/// <c>POST /api/chat</c> endpoint as <see cref="Trellis.Core.Services.OllamaClient"/>
/// but adds a <c>tools</c> array to the request body + parses
/// <c>message.tool_calls</c> from the response.
///
/// <para>
/// Function-calling-mode responses don't stream chunk-by-chunk like
/// plain text does — Ollama emits the full tool_calls array in the
/// (final) NDJSON message of the stream. Phase 3.A.1 sets
/// <c>"stream": false</c> on the request to get a single JSON
/// response back; streaming inside a tool-call turn doesn't
/// usefully partition (the entire tool_calls array has to be
/// complete before the executor can dispatch). Phase 3.B+ may
/// revisit if streaming partial text alongside tool_calls becomes
/// a UX want for the conversation surface.
/// </para>
///
/// <para>
/// Same <c>Func&lt;Uri&gt;</c> base-URL provider as
/// <see cref="Trellis.Core.Services.OllamaClient"/> — late-resolution
/// rule preserved for <c>WebApplicationFactory&lt;Program&gt;</c>
/// test overlays + future runtime config changes.
/// </para>
/// </summary>
public sealed class OllamaAgentLlmClient : IAgentLlmClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly Func<Uri> _baseUrlProvider;

    public OllamaAgentLlmClient(HttpClient http, Func<Uri> baseUrlProvider)
    {
        _http = http;
        _baseUrlProvider = baseUrlProvider;
    }

    public async Task<AgentLlmResponse> ChatWithToolsAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<AgentToolDescriptor> tools,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(tools);

        var uri = new Uri(_baseUrlProvider(), "api/chat");

        // Translate Trellis.Core.ChatMessage → Ollama wire shape.
        var wireMessages = messages
            .Select(m => new OllamaWireMessage(m.Role.ToWireString(), m.Content))
            .ToList();

        // Translate AgentToolDescriptor → Ollama tools array. Ollama
        // expects OpenAI-style: { type: "function", function: { name,
        // description, parameters: <JSON Schema object> } }. The
        // descriptor's ParameterSchema field is a JSON-Schema string
        // (per Phase 0 PR #10 docstring); we deserialize + re-embed
        // so the wire body is well-formed.
        var wireTools = tools
            .Select(d => new OllamaWireTool(
                Type: "function",
                Function: new OllamaWireToolFunction(
                    Name: d.Name,
                    Description: d.Description,
                    Parameters: ParseSchemaOrEmpty(d.ParameterSchema))))
            .ToList();

        var request = new OllamaWireChatRequest(
            Model: model,
            Messages: wireMessages,
            Tools: wireTools.Count == 0 ? null : wireTools,
            Stream: false);

        using var response = await _http.PostAsJsonAsync(uri, request, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content
            .ReadFromJsonAsync<OllamaWireChatResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        if (body is null)
        {
            throw new InvalidOperationException(
                "Ollama returned an empty body for /api/chat with tools.");
        }

        var toolCalls = body.Message?.ToolCalls?
            .Select(tc => new AgentLlmToolCall
            {
                ToolName = tc.Function?.Name ?? "",
                ArgumentsJson = SerializeArgs(tc.Function?.Arguments),
            })
            .Where(c => !string.IsNullOrWhiteSpace(c.ToolName))
            .ToList() ?? new List<AgentLlmToolCall>();

        // EvalCount + PromptEvalCount are Ollama's per-request token
        // counters (sum = total). Older builds may not return them; 0
        // means "model didn't report usage", surfaced verbatim.
        var tokensUsed = (long)((body.PromptEvalCount ?? 0) + (body.EvalCount ?? 0));

        return new AgentLlmResponse
        {
            AssistantText = body.Message?.Content ?? "",
            ToolCalls = toolCalls,
            TokensUsed = tokensUsed,
        };
    }

    /// <summary>
    /// Parse the <see cref="AgentToolDescriptor.ParameterSchema"/> string
    /// as a JSON object so it can be embedded verbatim in the Ollama
    /// tools wire body. Empty / blank schemas surface as
    /// <c>{ "type": "object" }</c> (vacuously valid; tells the model
    /// "no parameters required"). Malformed schemas throw — that's a
    /// configuration bug at registration time, not a runtime concern.
    /// </summary>
    private static JsonElement ParseSchemaOrEmpty(string schema)
    {
        if (string.IsNullOrWhiteSpace(schema))
        {
            using var emptyDoc = JsonDocument.Parse("""{"type":"object"}""");
            return emptyDoc.RootElement.Clone();
        }
        using var doc = JsonDocument.Parse(schema);
        return doc.RootElement.Clone();
    }

    private static readonly JsonSerializerOptions ArgsCanonicalizeOpts = new()
    {
        WriteIndented = false,
    };

    /// <summary>
    /// Ollama emits tool-call <c>arguments</c> as either an inline JSON
    /// object or a JSON string (varies by model + Ollama version). Both
    /// shapes round-trip through <see cref="JsonSerializer.Serialize{T}(T,JsonSerializerOptions)"/>
    /// to a canonical compact JSON string for storage in
    /// <see cref="AgentStep.ToolInputJson"/>. Whitespace is normalized
    /// — important so loop detection in
    /// <see cref="AssistantBudgetGate"/> sees consistent identity for
    /// "same args" across requests, regardless of Ollama's source-side
    /// whitespace. Null arguments → empty JSON object (the tool's
    /// RunAsync sees <c>"{}"</c>).
    /// </summary>
    private static string SerializeArgs(JsonElement? args)
    {
        if (args is null || args.Value.ValueKind == JsonValueKind.Undefined)
        {
            return "{}";
        }
        // If the model gave us a JSON string, unwrap then re-canonicalize.
        if (args.Value.ValueKind == JsonValueKind.String)
        {
            var inner = args.Value.GetString();
            if (string.IsNullOrWhiteSpace(inner))
            {
                return "{}";
            }
            try
            {
                using var inDoc = JsonDocument.Parse(inner);
                return JsonSerializer.Serialize(inDoc.RootElement, ArgsCanonicalizeOpts);
            }
            catch (JsonException)
            {
                return inner;
            }
        }
        // Inline object: round-trip through serializer so whitespace
        // is normalized. GetRawText() preserves source whitespace,
        // which would break canonicalization for loop detection.
        return JsonSerializer.Serialize(args.Value, ArgsCanonicalizeOpts);
    }

    // ---- Ollama wire types ----
    //
    // Mirror the on-the-wire JSON shape exactly. Property names use
    // PropertyNameCaseInsensitive on the deserializer + JsonPropertyName
    // attributes on the request side (so we send canonical lowercase).

    private sealed record OllamaWireChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<OllamaWireMessage> Messages,
        [property: JsonPropertyName("tools")] IReadOnlyList<OllamaWireTool>? Tools,
        [property: JsonPropertyName("stream")] bool Stream);

    private sealed record OllamaWireMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record OllamaWireTool(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("function")] OllamaWireToolFunction Function);

    private sealed record OllamaWireToolFunction(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("parameters")] JsonElement Parameters);

    private sealed record OllamaWireChatResponse
    {
        public OllamaWireResponseMessage? Message { get; init; }
        [JsonPropertyName("prompt_eval_count")]
        public int? PromptEvalCount { get; init; }
        [JsonPropertyName("eval_count")]
        public int? EvalCount { get; init; }
    }

    private sealed record OllamaWireResponseMessage
    {
        public string? Content { get; init; }
        [JsonPropertyName("tool_calls")]
        public IReadOnlyList<OllamaWireResponseToolCall>? ToolCalls { get; init; }
    }

    private sealed record OllamaWireResponseToolCall
    {
        public OllamaWireResponseToolCallFunction? Function { get; init; }
    }

    private sealed record OllamaWireResponseToolCallFunction
    {
        public string? Name { get; init; }
        public JsonElement? Arguments { get; init; }
    }
}

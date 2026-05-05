using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Trellis.Assistant.AgentExecution;
using Trellis.Core.Models;
using Xunit;

namespace Trellis.Assistant.Tests.AgentExecution;

/// <summary>
/// Unit tests for <see cref="OllamaAgentLlmClient"/>. Uses an in-process
/// <see cref="DelegatingHandler"/> to intercept the HTTP call —
/// no real Ollama needed. Verifies:
/// <list type="bullet">
/// <item>Request body shape (model, messages, tools array per Ollama
/// function-calling protocol)</item>
/// <item>Response parsing for plain assistant text</item>
/// <item>Response parsing for tool_calls (with both inline-object and
/// JSON-string argument shapes — Ollama emits both depending on
/// version / model)</item>
/// <item>Tokens accumulation from <c>prompt_eval_count</c> +
/// <c>eval_count</c></item>
/// <item>Late-resolution of <c>baseUrlProvider</c> (Func is called per
/// request, not captured at ctor time)</item>
/// </list>
/// </summary>
public sealed class OllamaAgentLlmClientTests
{
    [Fact]
    public async Task ChatWithToolsAsync_PlainAssistantText_ReturnsTextWithEmptyToolCalls()
    {
        var (client, handler) = NewClient(
            responseJson: """
            {
                "message": { "role": "assistant", "content": "the answer is 42" },
                "prompt_eval_count": 100,
                "eval_count": 25
            }
            """);

        var result = await client.ChatWithToolsAsync(
            model: "qwen2.5:72b",
            messages: new[] { new ChatMessage { Role = ChatRole.User, Content = "what's the answer?" } },
            tools: Array.Empty<AgentToolDescriptor>());

        result.AssistantText.Should().Be("the answer is 42");
        result.ToolCalls.Should().BeEmpty();
        result.TokensUsed.Should().Be(125, "prompt_eval_count + eval_count");

        handler.LastRequestBody.Should().NotBeNull();
        var sentBody = JsonDocument.Parse(handler.LastRequestBody!);
        sentBody.RootElement.GetProperty("model").GetString().Should().Be("qwen2.5:72b");
        sentBody.RootElement.GetProperty("stream").GetBoolean().Should().BeFalse(
            "Phase 3.A.1 sets stream=false for tool-call responses");
    }

    [Fact]
    public async Task ChatWithToolsAsync_WithToolsArray_RendersOpenAiStyleWireBody()
    {
        var (client, handler) = NewClient(
            responseJson: """{"message":{"content":""}, "prompt_eval_count": 1, "eval_count": 1}""");

        var tools = new[]
        {
            new AgentToolDescriptor
            {
                Name = "echo",
                Description = "Echo text back.",
                ParameterSchema = """{"type":"object","properties":{"text":{"type":"string"}}}""",
                Category = AgentToolCategory.Inspect,
            },
        };

        await client.ChatWithToolsAsync(
            model: "qwen2.5:72b",
            messages: new[] { new ChatMessage { Role = ChatRole.User, Content = "hi" } },
            tools: tools);

        var sent = JsonDocument.Parse(handler.LastRequestBody!);
        var toolsArray = sent.RootElement.GetProperty("tools");
        toolsArray.GetArrayLength().Should().Be(1);
        var firstTool = toolsArray[0];
        firstTool.GetProperty("type").GetString().Should().Be("function",
            "Ollama function-calling wire body uses OpenAI-style { type: 'function', function: {...} }");
        var fn = firstTool.GetProperty("function");
        fn.GetProperty("name").GetString().Should().Be("echo");
        fn.GetProperty("description").GetString().Should().Be("Echo text back.");
        // parameters must be embedded as a JSON object, not a string-of-JSON.
        fn.GetProperty("parameters").ValueKind.Should().Be(JsonValueKind.Object,
            "ParameterSchema must round-trip as an embedded JSON object, not a string-of-JSON");
    }

    [Fact]
    public async Task ChatWithToolsAsync_WithEmptyToolList_OmitsToolsKey()
    {
        var (client, handler) = NewClient(
            responseJson: """{"message":{"content":"ok"}, "prompt_eval_count": 1, "eval_count": 1}""");

        await client.ChatWithToolsAsync(
            model: "qwen2.5:72b",
            messages: new[] { new ChatMessage { Role = ChatRole.User, Content = "hi" } },
            tools: Array.Empty<AgentToolDescriptor>());

        var sent = JsonDocument.Parse(handler.LastRequestBody!);
        sent.RootElement.TryGetProperty("tools", out var toolsKey).Should().BeFalse(
            "empty tools list serializes to no 'tools' key (vs an empty array) — keeps the wire body minimal");
    }

    [Fact]
    public async Task ChatWithToolsAsync_ToolCallWithObjectArgs_ParsesIntoArgumentsJson()
    {
        // Ollama emits arguments as an inline JSON object (more recent
        // models / versions). The client must serialize-back to
        // ArgumentsJson string for the executor.
        var (client, _) = NewClient(
            responseJson: """
            {
                "message": {
                    "role": "assistant",
                    "content": "",
                    "tool_calls": [
                        {
                            "function": {
                                "name": "echo",
                                "arguments": { "text": "hello" }
                            }
                        }
                    ]
                },
                "prompt_eval_count": 50,
                "eval_count": 10
            }
            """);

        var result = await client.ChatWithToolsAsync(
            "qwen2.5:72b",
            new[] { new ChatMessage { Role = ChatRole.User, Content = "echo hello" } },
            Array.Empty<AgentToolDescriptor>());

        result.ToolCalls.Should().HaveCount(1);
        result.ToolCalls[0].ToolName.Should().Be("echo");
        result.ToolCalls[0].ArgumentsJson.Should().Contain("\"text\":\"hello\"");
    }

    [Fact]
    public async Task ChatWithToolsAsync_ToolCallWithStringArgs_UnwrapsToCanonicalJson()
    {
        // Older Ollama builds + some models emit arguments as a JSON
        // string (the inner JSON wrapped in quotes). The client must
        // unwrap to canonical JSON so AgentStep.ToolInputJson stores
        // the same shape regardless of version.
        var (client, _) = NewClient(
            responseJson: """
            {
                "message": {
                    "role": "assistant",
                    "content": "",
                    "tool_calls": [
                        {
                            "function": {
                                "name": "echo",
                                "arguments": "{\"text\":\"world\"}"
                            }
                        }
                    ]
                },
                "prompt_eval_count": 1,
                "eval_count": 1
            }
            """);

        var result = await client.ChatWithToolsAsync(
            "qwen2.5:72b",
            new[] { new ChatMessage { Role = ChatRole.User, Content = "x" } },
            Array.Empty<AgentToolDescriptor>());

        result.ToolCalls.Should().HaveCount(1);
        result.ToolCalls[0].ArgumentsJson.Should().Contain("\"text\":\"world\"",
            "JSON-string arguments shape must unwrap to inner JSON for storage consistency");
    }

    [Fact]
    public async Task ChatWithToolsAsync_ToolCallWithMissingName_FiltersOutOfResults()
    {
        // Defensive parsing — if Ollama returns a tool_call without a
        // name (malformed), the client filters it out rather than
        // surfacing an empty-name dispatch. The executor would dispatch
        // an empty-name "tool" and fail in the registry; better to
        // surface the malformed call as "no tool calls" + let the
        // model retry.
        var (client, _) = NewClient(
            responseJson: """
            {
                "message": {
                    "tool_calls": [{"function":{"name":"","arguments":{}}}]
                },
                "prompt_eval_count": 1,
                "eval_count": 1
            }
            """);

        var result = await client.ChatWithToolsAsync(
            "qwen2.5:72b",
            new[] { new ChatMessage { Role = ChatRole.User, Content = "x" } },
            Array.Empty<AgentToolDescriptor>());

        result.ToolCalls.Should().BeEmpty(
            "malformed tool_call (empty name) is filtered out; executor sees no tool calls and proceeds");
    }

    [Fact]
    public async Task ChatWithToolsAsync_BaseUrlProvider_CalledPerRequest()
    {
        // Verifies the late-resolution Func<Uri> contract. The
        // baseUrlProvider must be invoked at request time, not captured
        // at ctor time — same WebApplicationFactory<Program> overlay
        // pattern as Phase 1's connection string.
        var callCount = 0;
        var handler = new ScriptableHandler("""{"message":{"content":"ok"}, "prompt_eval_count":0, "eval_count":0}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://default.invalid/") };
        var client = new OllamaAgentLlmClient(http, () =>
        {
            callCount++;
            return new Uri("http://test.invalid:11434/");
        });

        await client.ChatWithToolsAsync("m", new[] { new ChatMessage { Role = ChatRole.User, Content = "x" } }, Array.Empty<AgentToolDescriptor>());
        await client.ChatWithToolsAsync("m", new[] { new ChatMessage { Role = ChatRole.User, Content = "y" } }, Array.Empty<AgentToolDescriptor>());

        callCount.Should().Be(2,
            "baseUrlProvider must be invoked per request, not memoized — late-resolution rule preserved across both Phase 1's connection string + Phase 2's Ollama:BaseUrl + Phase 3.A.1's IAgentLlmClient");
    }

    [Fact]
    public async Task ChatWithToolsAsync_NonSuccessHttpStatus_Throws()
    {
        // Ollama returning 4xx/5xx (e.g. unknown model) → HttpRequestException
        // bubbles up. The endpoint layer (ConversationEndpoints +
        // AgentRunEndpoints) catches this and maps to 502 Bad Gateway.
        var handler = new ScriptableHandler(
            responseJson: """{"error":"model not found"}""",
            statusCode: HttpStatusCode.NotFound);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://test.invalid/") };
        var client = new OllamaAgentLlmClient(http, () => new Uri("http://test.invalid:11434/"));

        var act = () => client.ChatWithToolsAsync(
            "bad-model",
            new[] { new ChatMessage { Role = ChatRole.User, Content = "x" } },
            Array.Empty<AgentToolDescriptor>());
        await act.Should().ThrowAsync<HttpRequestException>(
            "non-2xx upstream Ollama responses must throw HttpRequestException so the endpoint layer can map to 502");
    }

    private static (OllamaAgentLlmClient client, ScriptableHandler handler) NewClient(string responseJson)
    {
        var handler = new ScriptableHandler(responseJson);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://test.invalid/") };
        var client = new OllamaAgentLlmClient(http, () => new Uri("http://test.invalid:11434/"));
        return (client, handler);
    }

    private sealed class ScriptableHandler : DelegatingHandler
    {
        private readonly string _responseJson;
        private readonly HttpStatusCode _statusCode;

        public string? LastRequestBody { get; private set; }

        public ScriptableHandler(string responseJson, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responseJson = responseJson;
            _statusCode = statusCode;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }
}

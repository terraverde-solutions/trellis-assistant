using System.Text.Json;
using Trellis.Core.Models;
using Trellis.Core.Services;

namespace Trellis.Assistant.AgentExecution;

/// <summary>
/// Phase 3.A.1 stub tool. Returns whatever <c>text</c> input the planner
/// supplied, wrapped in <c>{"output": "..."}</c>. Lets the executor +
/// LLM tool-call parsing + AgentStep persistence flow be exercised
/// end-to-end without depending on Trainer's RAG corpus or any other
/// external surface.
///
/// <para>
/// Phase 3.B replaces this with the real <c>SearchDocumentsTool</c> →
/// Trainer integration. EchoTool ships forward as a
/// development/debugging aid (operators can include it in a
/// conversation's tool catalogue to verify the agentic loop is
/// functioning without invoking real downstream services).
/// </para>
/// </summary>
public sealed class EchoTool : IAgentTool
{
    public AgentToolDescriptor Descriptor { get; } = new()
    {
        Name = "echo",
        Description =
            "Echo the supplied text back in a structured response. Use this when you want " +
            "to confirm the agent loop is dispatching tools correctly without invoking any " +
            "external service. Returns {output: <text>}.",
        ParameterSchema = """
        {
            "type": "object",
            "additionalProperties": false,
            "properties": {
                "text": {
                    "type": "string",
                    "description": "The text to echo back."
                }
            },
            "required": ["text"]
        }
        """,
        Category = AgentToolCategory.Inspect,
    };

    public Task<AgentToolOutput> RunAsync(
        AgentToolInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        // Parse the args. The executor schema-validates BEFORE dispatch
        // (Phase 0 contract), but Phase 3.A.1's executor doesn't ship
        // schema validation (deferred to Phase 3.B). For EchoTool's
        // trivial schema, parse defensively + surface a structured
        // failure on missing/bad input rather than throwing.
        EchoArgs? args;
        try
        {
            args = JsonSerializer.Deserialize<EchoArgs>(input.ParametersJson, JsonOpts);
        }
        catch (JsonException ex)
        {
            return Task.FromResult(new AgentToolOutput
            {
                Success = false,
                ResultJson = null,
                ErrorMessage = $"echo: failed to parse arguments JSON: {ex.Message}",
            });
        }

        if (args is null || string.IsNullOrEmpty(args.Text))
        {
            return Task.FromResult(new AgentToolOutput
            {
                Success = false,
                ResultJson = null,
                ErrorMessage = "echo: required argument 'text' was missing or empty.",
            });
        }

        var resultJson = JsonSerializer.Serialize(new EchoResult { Output = args.Text }, JsonOpts);
        return Task.FromResult(new AgentToolOutput
        {
            Success = true,
            ResultJson = resultJson,
            ErrorMessage = null,
        });
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed record EchoArgs
    {
        public string? Text { get; init; }
    }

    private sealed record EchoResult
    {
        public required string Output { get; init; }
    }
}

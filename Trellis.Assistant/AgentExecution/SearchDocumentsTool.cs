using System.Text.Json;
using Trellis.Assistant.Services;
using Trellis.Core.Models;
using Trellis.Core.Services;

namespace Trellis.Assistant.AgentExecution;

/// <summary>
/// Phase 3.B's first real tool. Dispatches LLM-emitted search calls to
/// trellis-trainer's <c>GET /api/search</c> via <see cref="ISearchClient"/>.
/// Replaces <see cref="EchoTool"/> as the "actually does something" tool
/// in the registry; EchoTool ships forward as a debugging aid (operators
/// include it in a conversation's tool catalogue to verify the agent
/// loop is alive without invoking real downstream services).
///
/// <para>
/// Wire shape: pass-through (a) per the pre-scaffold ratify. The
/// LLM-emitted arguments JSON is deserialized to <see cref="ToolArgs"/>,
/// translated to a <see cref="SearchQuery"/>, dispatched via
/// <see cref="ISearchClient"/>, and the 2xx body bytes are returned
/// verbatim as <see cref="AgentToolOutput.ResultJson"/>. Trainer-canonical
/// PascalCase field names (<c>DocumentId</c>, <c>ChunkContent</c>,
/// <c>Snippet</c>, etc.) flow through to the LLM unchanged.
/// </para>
///
/// <para>
/// Schema validation: the executor validates
/// <see cref="AgentToolInput.ParametersJson"/> against
/// <see cref="AgentToolDescriptor.ParameterSchema"/> BEFORE dispatch
/// (Phase 0 contract; turned on for Phase 3.B). By the time RunAsync
/// fires, args are guaranteed to parse + satisfy the declared shape.
/// The defensive parse here uses
/// <see cref="JsonSerializer.Deserialize{T}(string,JsonSerializerOptions)"/>
/// with case-insensitive property naming for forward-compat against
/// LLM emitters that drift between camelCase and snake_case casing.
/// </para>
/// </summary>
public sealed class SearchDocumentsTool : IAgentTool
{
    /// <summary>
    /// JSON Schema (Draft 2020-12) for the tool's argument surface.
    /// Maps directly to Trainer's <c>GET /api/search</c> query
    /// parameters with Assistant-canonical naming
    /// (<c>document_ids</c>/<c>content_types</c> arrays vs Trainer's
    /// repeated single-value params — the client flattens them).
    /// </summary>
    public const string ParameterSchemaJson = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "query": {
          "type": "string",
          "minLength": 1,
          "description": "The natural-language search query."
        },
        "top_k": {
          "type": "integer",
          "minimum": 1,
          "maximum": 50,
          "description": "Number of results to return. Default 5 if omitted."
        },
        "mode": {
          "type": "string",
          "enum": ["vector", "lexical", "hybrid"],
          "description": "Retrieval mode. Default 'vector' if omitted. Prefer 'hybrid' for English natural-language queries (combines vector + lexical via RRF); 'lexical' for exact-term-match needs."
        },
        "since": {
          "type": "string",
          "format": "date-time",
          "description": "ISO 8601 timestamp; filters to documents updated at or after this instant."
        },
        "filter": {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "document_ids": {
              "type": "array",
              "items": { "type": "string", "format": "uuid" },
              "description": "Restrict to these document GUIDs."
            },
            "content_types": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Restrict to these MIME types (e.g. 'application/pdf', 'text/markdown')."
            }
          }
        }
      },
      "required": ["query"]
    }
    """;

    public const string ToolName = "search_documents";

    public AgentToolDescriptor Descriptor { get; } = new()
    {
        Name = ToolName,
        Description =
            "Search the customer's indexed document corpus and return the top-K matching chunks. " +
            "Use this when the user asks a question whose answer is likely in their documents, or " +
            "when you need to ground a reply in cited material. Returns an array of result records, " +
            "each carrying DocumentId, DocumentTitle, DocumentSource, ChunkIndex, ChunkContent, Score, " +
            "and Snippet. An empty array means no matches; pass-through Trainer field names + casing.",
        ParameterSchema = ParameterSchemaJson,
        Category = AgentToolCategory.Search,
    };

    private static readonly JsonSerializerOptions ArgsJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Func<ISearchClient> _clientFactory;

    /// <summary>
    /// Take a <see cref="Func{ISearchClient}"/> rather than an
    /// <see cref="ISearchClient"/> directly: <see cref="ISearchClient"/>
    /// is registered as a typed-HttpClient client
    /// (<c>AddHttpClient&lt;ISearchClient, HttpSearchClient&gt;</c>),
    /// which the IHttpClientFactory contract makes effectively
    /// transient — capturing it in this singleton tool would pin the
    /// first transient instance + its handler for the host's lifetime,
    /// bypassing IHttpClientFactory's default 2-minute handler rotation.
    ///
    /// <para>
    /// The factory delegate resolves fresh per <see cref="RunAsync"/>
    /// call, so each dispatch picks up a current <see cref="ISearchClient"/>
    /// + rotated handler. The DI registration is
    /// <c>AddSingleton&lt;Func&lt;ISearchClient&gt;&gt;(sp =&gt; () =&gt; sp.GetRequiredService&lt;ISearchClient&gt;())</c>
    /// — singleton wrapper, transient resolution per invocation.
    /// </para>
    /// </summary>
    public SearchDocumentsTool(Func<ISearchClient> clientFactory)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    }

    public async Task<AgentToolOutput> RunAsync(
        AgentToolInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        ToolArgs args;
        try
        {
            args = JsonSerializer.Deserialize<ToolArgs>(input.ParametersJson, ArgsJsonOpts)
                ?? throw new JsonException("arguments deserialized to null");
        }
        catch (JsonException ex)
        {
            // Schema validation runs upstack; reaching this branch means
            // a schema/library mismatch. Surface a structured failure
            // rather than throwing — Core's contract is "return Success=false,
            // don't throw" for tool-level failures.
            return new AgentToolOutput
            {
                Success = false,
                ResultJson = null,
                ErrorMessage = $"{ToolName}: failed to parse arguments JSON: {ex.Message}",
            };
        }

        if (string.IsNullOrWhiteSpace(args.Query))
        {
            return new AgentToolOutput
            {
                Success = false,
                ResultJson = null,
                ErrorMessage = $"{ToolName}: required argument 'query' was missing or empty.",
            };
        }

        var query = MapToSearchQuery(args);
        var client = _clientFactory();
        var result = await client.SearchAsync(query, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            return new AgentToolOutput
            {
                Success = false,
                ResultJson = null,
                ErrorMessage = result.ErrorMessage ?? $"{ToolName}: trainer search failed.",
            };
        }

        // Pass-through (a): the 2xx body bytes go verbatim into ResultJson.
        // Empty array "[]" is a valid pass-through; the LLM interprets
        // "no documents matched" itself per decide-and-document #3.
        return new AgentToolOutput
        {
            Success = true,
            ResultJson = result.ResponseBodyJson ?? "[]",
            ErrorMessage = null,
        };
    }

    /// <summary>
    /// Translate the LLM-emitted argument shape (snake_case, with a
    /// nested <c>filter</c> object for document/content-type filtering)
    /// into the Trainer wire-aligned <see cref="SearchQuery"/> shape
    /// (flat record, multi-value lists). Visible for unit tests pinning
    /// the boundary mapping.
    /// </summary>
    public static SearchQuery MapToSearchQuery(ToolArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var mode = args.Mode switch
        {
            null => (SearchMode?)null,
            "vector" => SearchMode.Vector,
            "lexical" => SearchMode.Lexical,
            "hybrid" => SearchMode.Hybrid,
            // Schema validation guarantees this is one of the three;
            // the throw is defense against a future schema/code drift.
            _ => throw new ArgumentOutOfRangeException(
                nameof(args), args.Mode, "Unknown SearchMode wire value (schema validation should have rejected upstack)"),
        };

        return new SearchQuery
        {
            Q = args.Query!,
            K = args.TopK,
            Mode = mode,
            Since = args.Since,
            DocumentIds = args.Filter?.DocumentIds,
            ContentTypes = args.Filter?.ContentTypes,
        };
    }

    /// <summary>
    /// Deserialized LLM-emitted argument shape. snake_case to match the
    /// JSON Schema property names; <see cref="JsonSerializerOptions.PropertyNameCaseInsensitive"/>
    /// + System.Text.Json's default behavior matches camelCase + snake_case
    /// against PascalCase property names (case-insensitive),
    /// so this works whether the LLM emits <c>top_k</c> or <c>topK</c>.
    /// </summary>
    public sealed record ToolArgs
    {
        public string? Query { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("top_k")]
        public int? TopK { get; init; }
        public string? Mode { get; init; }
        public DateTimeOffset? Since { get; init; }
        public ToolFilter? Filter { get; init; }
    }

    public sealed record ToolFilter
    {
        [System.Text.Json.Serialization.JsonPropertyName("document_ids")]
        public IReadOnlyList<Guid>? DocumentIds { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("content_types")]
        public IReadOnlyList<string>? ContentTypes { get; init; }
    }
}

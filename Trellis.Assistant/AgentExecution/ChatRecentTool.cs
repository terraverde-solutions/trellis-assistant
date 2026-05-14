using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Trellis.Core.Models;
using Trellis.Core.Services;

namespace Trellis.Assistant.AgentExecution;

/// <summary>
/// Phase 3.D's second production tool. Lets the LLM fetch the
/// requesting user's recent conversation history when the current turn
/// references a prior discussion ("yesterday we talked about...",
/// "remember when you said..."). Reads from
/// <see cref="IAssistantConversationStore.SearchRecentTurnsAsync"/> with
/// the tenant + user scope threaded through
/// <see cref="AgentToolInput.OrgId"/> + <see cref="AgentToolInput.UserId"/>
/// — strict (tenant, user) chokepoint, no cross-tenant or cross-user
/// leak possible at this layer.
///
/// <para>
/// Wire shape: arguments JSON deserialized to <see cref="ToolArgs"/>
/// (snake_case fields matching the JSON Schema), translated to the
/// store's positional parameters, dispatched. Result is the projection
/// list JSON-serialized verbatim into
/// <see cref="AgentToolOutput.ResultJson"/>; the LLM sees the matching
/// turns in newest-first order with <c>conversation_id</c> +
/// <c>position</c> + <c>role</c> + <c>content</c> + <c>created_at</c>
/// so it can stitch matches that share a thread.
/// </para>
///
/// <para>
/// Schema validation: the executor schema-validates
/// <see cref="AgentToolInput.ParametersJson"/> against
/// <see cref="AgentToolDescriptor.ParameterSchema"/> BEFORE dispatch
/// (Phase 0 contract; turned on in Phase 3.B). By the time RunAsync
/// fires, args parse + match. Defensive parse + missing-field checks
/// here cover library/schema drift.
/// </para>
///
/// <para>
/// Standalone-run user-scope sentinel: when
/// <see cref="AgentToolInput.UserId"/> equals
/// <see cref="AssistantAgentExecutor.AnonymousStandaloneUserId"/>
/// (operator-facing <c>POST /api/agent-runs</c> path, no end-user
/// identity), the tool returns an empty result rather than attempting
/// a query — there's no per-user history to surface for an
/// authentication-less audit run.
/// </para>
/// </summary>
public sealed class ChatRecentTool : IAgentTool
{
    public const string ToolName = "chat_recent";

    /// <summary>
    /// JSON Schema (Draft 2020-12) for the tool's argument surface.
    /// Maps to <see cref="IAssistantConversationStore.SearchRecentTurnsAsync"/>'s
    /// (query, limit, since) parameters. Tenant + user scope flow from
    /// <see cref="AgentToolInput"/>, not the args — caller doesn't get
    /// to specify them.
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
          "description": "Free-text query to match against turn content. Case-insensitive substring match for v0."
        },
        "limit": {
          "type": "integer",
          "minimum": 1,
          "maximum": 50,
          "description": "Maximum number of results to return. Default 10 if omitted."
        },
        "since": {
          "type": "string",
          "format": "date-time",
          "description": "Optional ISO 8601 timestamp. Returns only turns whose created_at >= this instant. UTC required."
        }
      },
      "required": ["query"]
    }
    """;

    public AgentToolDescriptor Descriptor { get; } = new()
    {
        Name = ToolName,
        Description =
            "Search the user's recent conversation history for relevant prior context. " +
            "Use this when the user references a prior conversation (\"yesterday we discussed...\", " +
            "\"remember when you said...\"), or when grounding the current turn in earlier " +
            "discussion would help. Returns matching turns sorted newest-first with " +
            "conversation_id, position, role, content, and created_at — strictly scoped to " +
            "the current user's own conversations within the current tenant.",
        ParameterSchema = ParameterSchemaJson,
        Category = AgentToolCategory.Search,
    };

    private static readonly JsonSerializerOptions ArgsJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions ResultJsonOpts = new()
    {
        WriteIndented = false,
        // snake_case the projection on the way out so LLMs trained on
        // OpenAI-style tool responses see the conventional field names.
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private const int DefaultLimit = 10;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ChatRecentTool> _logger;

    /// <summary>
    /// Take <see cref="IServiceScopeFactory"/> rather than
    /// <see cref="IAssistantConversationStore"/> directly: the store is
    /// registered scoped (lifetime-bound to <c>AssistantDbContext</c>,
    /// which is itself scoped per <c>AddDbContext</c>'s default), but
    /// this tool is registered Singleton via the
    /// <c>IEnumerable&lt;IAgentTool&gt;</c> collection that
    /// <see cref="IToolRegistry"/> (also Singleton) sweeps at host
    /// startup. ASP.NET Core's scope validation blocks Singleton-from-
    /// Scoped — a direct <c>Func&lt;IAssistantConversationStore&gt;</c>
    /// pattern (Phase 3.B's SearchDocumentsTool fix) doesn't work here
    /// because <see cref="ISearchClient"/> is transient (typed-
    /// HttpClient) while the store is genuinely scoped.
    ///
    /// <para>
    /// Per-call <see cref="IServiceScopeFactory.CreateScope"/> creates a
    /// fresh scope → fresh <see cref="Data.AssistantDbContext"/> → query
    /// → dispose scope. Single Postgres round-trip per RunAsync call;
    /// no captive lifetime issues.
    /// </para>
    /// </summary>
    public ChatRecentTool(
        IServiceScopeFactory scopeFactory,
        ILogger<ChatRecentTool> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger;
    }

    public async Task<AgentToolOutput> RunAsync(
        AgentToolInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        // Standalone-run sentinel: no per-user identity → no per-user
        // history to surface. Return empty array rather than running a
        // query with a sentinel UserId (which would either match no
        // rows or, worse, match a hypothetical user named "standalone").
        if (input.UserId == AssistantAgentExecutor.AnonymousStandaloneUserId)
        {
            _logger.LogInformation(
                "AgentRun {RunId}: chat_recent invoked from standalone path (no end-user identity); returning empty.",
                input.AgentRunId);
            return new AgentToolOutput
            {
                Success = true,
                ResultJson = "[]",
                ErrorMessage = null,
            };
        }

        ToolArgs args;
        try
        {
            args = JsonSerializer.Deserialize<ToolArgs>(input.ParametersJson, ArgsJsonOpts)
                ?? throw new JsonException("arguments deserialized to null");
        }
        catch (JsonException ex)
        {
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

        // OrgId (Guid) carries the tenant per Phase 0 PR #10's contract.
        // IAssistantConversationStore speaks tenantId : string — convert
        // via the canonical "D" format (lowercase, 8-4-4-4-12) per the
        // same Phase 3.A C1 convention as
        // ConversationOrchestrator.HandleAgentPathAsync.
        var tenantId = input.OrgId.ToString("D", System.Globalization.CultureInfo.InvariantCulture);

        var effectiveLimit = args.Limit ?? DefaultLimit;
        IReadOnlyList<RecentTurnSummary> results;
        try
        {
            // Fresh scope per call → fresh AssistantDbContext. The
            // scope's `using` ensures the DbContext is disposed even on
            // cancellation. The store impl reads + returns the
            // projection list inside the await, so the materialized
            // results are safe to use after scope disposal.
            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IAssistantConversationStore>();
            results = await store
                .SearchRecentTurnsAsync(
                    tenantId, input.UserId, args.Query, effectiveLimit, args.Since, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Store failures (Postgres connection drop, EF translation
            // failure, etc.) surface as structured tool failures rather
            // than throws — the executor's broad catch would log this
            // as a generic "tool threw" warning, which is noisier than
            // a tool-typed error message. Cancellation rethrows above
            // so the executor's OCE handling fires.
            _logger.LogWarning(ex,
                "AgentRun {RunId} chat_recent store-side failure for tenant={Tenant} user={User} query={Query}.",
                input.AgentRunId, tenantId, input.UserId, args.Query);
            return new AgentToolOutput
            {
                Success = false,
                ResultJson = null,
                ErrorMessage = $"{ToolName}: store-side failure — {ex.Message}",
            };
        }

        _logger.LogInformation(
            "AgentRun {RunId} chat_recent: tenant={Tenant} user={User} query={Query} limit={Limit} → {ResultCount} match(es).",
            input.AgentRunId, tenantId, input.UserId, args.Query, effectiveLimit, results.Count);

        // Project to the LLM-facing snake_case shape. Trellis.Core's
        // RecentTurnSummary fields are PascalCase per C# record
        // convention; the SnakeCaseLower naming policy maps on the way
        // out so the LLM sees { conversation_id, turn_id, position,
        // role, content, created_at }. AssistantTurnRole has no enum
        // converter on the Core type — we project Role to the
        // lowercase wire string here so the LLM doesn't see integer
        // codes (System.Text.Json's default enum serialization). Same
        // wire vocabulary as the role column in the turns table
        // ("user"/"assistant"/"system"/"tool").
        var resultJson = JsonSerializer.Serialize(
            results.Select(r => new ResultProjection(
                ConversationId: r.ConversationId,
                TurnId: r.TurnId,
                Position: r.Position,
                Role: RoleToWire(r.Role),
                Content: r.Content,
                CreatedAt: r.CreatedAt)),
            ResultJsonOpts);

        return new AgentToolOutput
        {
            Success = true,
            ResultJson = resultJson,
            ErrorMessage = null,
        };
    }

    /// <summary>
    /// LLM-emitted argument shape. snake_case to match the JSON Schema
    /// + the wire convention; case-insensitive deserialization tolerates
    /// camelCase emitters too.
    /// </summary>
    public sealed record ToolArgs
    {
        public string? Query { get; init; }
        public int? Limit { get; init; }
        public DateTime? Since { get; init; }
    }

    /// <summary>
    /// On-the-wire projection for the LLM. Same fields as
    /// <see cref="RecentTurnSummary"/> but as a separate type so the
    /// JSON serializer applies <see cref="JsonNamingPolicy.SnakeCaseLower"/>
    /// without disturbing Core's record contract (Core records keep
    /// PascalCase via System.Text.Json's default; the SnakeCaseLower
    /// policy applies here at the boundary). Role is a string here
    /// (not the AssistantTurnRole enum) so System.Text.Json emits the
    /// lowercase wire vocabulary instead of integer codes.
    /// </summary>
    private sealed record ResultProjection(
        Guid ConversationId,
        Guid TurnId,
        int Position,
        string Role,
        string Content,
        DateTime CreatedAt);

    private static string RoleToWire(AssistantTurnRole role) => role switch
    {
        AssistantTurnRole.User => "user",
        AssistantTurnRole.Assistant => "assistant",
        AssistantTurnRole.System => "system",
        AssistantTurnRole.Tool => "tool",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown AssistantTurnRole"),
    };
}

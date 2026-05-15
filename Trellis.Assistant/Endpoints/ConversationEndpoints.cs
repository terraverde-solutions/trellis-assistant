using Microsoft.Extensions.Options;
using Trellis.Assistant.Middleware;
using Trellis.Assistant.Services;
using Trellis.Core.Services;

namespace Trellis.Assistant.Endpoints;

/// <summary>
/// HTTP endpoints for Phase 1's conversation surface. Three operations:
///   - POST /api/conversations              → create a new conversation
///   - GET  /api/conversations/{id}         → read conversation + all turns
///   - POST /api/conversations/{id}/turns   → append a user turn + get the
///                                            stub-LLM-generated assistant
///                                            reply atomically
///
/// Wire format for IDs: 26-char base32 ulid (uppercase hex of the 128-bit
/// id, Crockford base32). The endpoint converts at the boundary —
/// inbound <c>string</c> → <see cref="Ulid.Parse(string)"/> → <see cref="Guid"/>;
/// outbound <see cref="Guid"/> → <c>new Ulid(g)</c> → <c>.ToString()</c>.
/// The Postgres column stores the 16-byte uuid; the wire-format ulid
/// string preserves the time-sortable byte layout end-to-end.
///
/// Auth: every endpoint here lives under /api/ so the
/// <see cref="TenantClaimsMiddleware"/> has already validated +
/// stashed the trusted (tenant, user) tuple in
/// <see cref="HttpContext.Items"/>. The endpoint handlers read those
/// values; they never read the headers directly. When Phase 5 swaps the
/// middleware to JWT-claim extraction, no endpoint code changes.
///
/// Per-request timeout: the POST /turns endpoint links the request CT
/// with a 60s wall-clock budget so a stuck LLM call (Phase 2 cold-load
/// of a 70B model is the realistic worst case) doesn't hold the per-
/// conversation advisory lock indefinitely. The orchestrator's class
/// doc spells out why the lock + the timeout are not separable
/// concerns.
/// </summary>
public static class ConversationEndpoints
{
    public static void MapConversationEndpoints(this IEndpointRouteBuilder app)
    {
        // Macro 3 PR 2: every endpoint in the group requires either a
        // valid JWT bearer (canonical path) or X-Trellis-* headers
        // (deprecated, telemetry-tracked). TenantClaimsMiddleware
        // synthesizes a ClaimsPrincipal for the deprecated path so
        // RequireAuthorization sees an authenticated principal either
        // way; missing both → ASP.NET Core's authorization middleware
        // produces 401 before the endpoint is reached.
        var group = app.MapGroup("/api/conversations").RequireAuthorization();

        group.MapPost("/", CreateConversationAsync);
        group.MapGet("/{id}", GetConversationAsync);
        group.MapPost("/{id}/turns", AppendTurnAsync);
    }

    // ---------------- POST /api/conversations ----------------

    public sealed record CreateConversationRequest(string? Channel, string? Model = null);

    public sealed record CreateConversationResponse(
        string Id,
        string Channel,
        string Model,
        DateTime CreatedAt,
        DateTime UpdatedAt);

    private static async Task<IResult> CreateConversationAsync(
        CreateConversationRequest? request,
        HttpContext context,
        IAssistantConversationStore store,
        CancellationToken cancellationToken)
    {
        var tenantId = (string)context.Items[TenantClaimsMiddleware.TenantIdKey]!;
        var userId = (string)context.Items[TenantClaimsMiddleware.UserIdKey]!;

        // Default channel to "api" when omitted — matches the schema's
        // DEFAULT 'api'. Empty string is treated as "use default" too —
        // operators copy-pasting curl from docs sometimes leave the field
        // blank rather than omit it.
        var channel = string.IsNullOrWhiteSpace(request?.Channel) ? "api" : request.Channel;

        // Model is per-conversation, pinned at create time. Pass null/
        // whitespace through unchanged — the store applies the column
        // DEFAULT (matches the EF migration) so the resolved value is
        // visible in the read-back below.
        var model = string.IsNullOrWhiteSpace(request?.Model) ? null : request.Model;

        var newId = await store.CreateConversationAsync(tenantId, userId, channel, model, cancellationToken)
            .ConfigureAwait(false);
        var conv = await store.GetConversationAsync(tenantId, userId, newId, cancellationToken)
            .ConfigureAwait(false);
        // The just-created conversation MUST be visible to the same
        // tenant/user — if GetConversationAsync returns null here, the
        // store impl has a tenant-filter bug. Fail loud.
        if (conv is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                detail: "Created conversation not visible to its owner — store impl bug.");
        }

        return Results.Created(
            $"/api/conversations/{GuidToUlidString(conv.Id)}",
            new CreateConversationResponse(
                Id: GuidToUlidString(conv.Id),
                Channel: conv.Channel,
                Model: conv.Model,
                CreatedAt: conv.CreatedAt,
                UpdatedAt: conv.UpdatedAt));
    }

    // ---------------- GET /api/conversations/{id} ----------------

    public sealed record GetConversationResponse(
        string Id,
        string Channel,
        string Model,
        DateTime CreatedAt,
        DateTime UpdatedAt,
        IReadOnlyList<TurnDto> Turns);

    public sealed record TurnDto(
        string Id,
        int Position,
        string Role,
        string Content,
        DateTime CreatedAt)
    {
        // Phase 3.A.2: tool-turn metadata. Both null on user/assistant/
        // system turns; populated on Tool turns to identify which
        // dispatch the row carries the result of.
        public string? ToolCallId { get; init; }
        public string? ToolName { get; init; }
    }

    private static async Task<IResult> GetConversationAsync(
        string id,
        HttpContext context,
        IAssistantConversationStore store,
        CancellationToken cancellationToken)
    {
        var tenantId = (string)context.Items[TenantClaimsMiddleware.TenantIdKey]!;
        var userId = (string)context.Items[TenantClaimsMiddleware.UserIdKey]!;

        if (!TryParseUlidToGuid(id, out var conversationId))
        {
            return Results.BadRequest(new { error = "id is not a valid ulid" });
        }

        var conv = await store.GetConversationAsync(tenantId, userId, conversationId, cancellationToken)
            .ConfigureAwait(false);
        if (conv is null)
        {
            // Cross-tenant existence-probing leak prevention: 404 means
            // either "doesn't exist" OR "exists but you can't see it."
            // Caller cannot distinguish.
            return Results.NotFound(new { error = "conversation not found" });
        }

        var turns = await store.GetTurnsAsync(tenantId, userId, conversationId, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new GetConversationResponse(
            Id: GuidToUlidString(conv.Id),
            Channel: conv.Channel,
            Model: conv.Model,
            CreatedAt: conv.CreatedAt,
            UpdatedAt: conv.UpdatedAt,
            Turns: turns.Select(ToTurnDto).ToList()));
    }

    // ---------------- POST /api/conversations/{id}/turns ----------------

    public sealed record AppendTurnRequest(string Content)
    {
        /// <summary>
        /// Optional tool-name filter governing the agent-path vs
        /// direct-LLM routing decision. Phase 3.C Q1 Option A semantics:
        /// <list type="bullet">
        /// <item><c>null</c> (field omitted): routes through
        /// <see cref="AssistantAgentExecutor"/> with the registry's full
        /// exposed catalogue. Default callers get the agent loop
        /// automatically.</item>
        /// <item><c>[]</c> (explicit empty list): direct-LLM opt-out
        /// — caller explicitly doesn't want tool overhead. Preserves
        /// Phase 2 per-conversation-model semantics (uses the
        /// conversation's pinned <c>Model</c>, not
        /// <c>Assistant:Agent:Model</c>).</item>
        /// <item><c>["name", ...]</c> (non-empty filter): agent path
        /// with the supplied names. Unknown names are dropped at the
        /// orchestrator's pre-resolution layer; if the resolved
        /// catalogue is empty (all-unknowns), the request degrades to
        /// the direct-LLM path.</item>
        /// </list>
        /// v0 ships <c>EchoTool</c> (test-only, gated by
        /// <c>Assistant:Tools:ExposeEcho</c>) + <c>SearchDocumentsTool</c>
        /// (production, always exposed).
        /// </summary>
        public IReadOnlyList<string>? Tools { get; init; }
    }

    public sealed record AppendTurnResponse(
        TurnDto UserTurn,
        TurnDto AssistantTurn)
    {
        /// <summary>
        /// Phase 3.A.2: tool turns persisted inline between
        /// <see cref="UserTurn"/> and <see cref="AssistantTurn"/> when
        /// the agent path ran. Empty list for direct-LLM path
        /// (Phase 1+2 contract). The full chain
        /// <c>[UserTurn, ...ToolTurns, AssistantTurn]</c> is contiguous
        /// in position order; channel adapters that need the chain
        /// reconstruct it from these fields without a separate read.
        /// </summary>
        public IReadOnlyList<TurnDto>? ToolTurns { get; init; }
    }

    private static async Task<IResult> AppendTurnAsync(
        string id,
        AppendTurnRequest request,
        HttpContext context,
        ConversationOrchestrator orchestrator,
        IOptions<ConversationOrchestratorOptions> options,
        CancellationToken cancellationToken)
    {
        var tenantId = (string)context.Items[TenantClaimsMiddleware.TenantIdKey]!;
        var userId = (string)context.Items[TenantClaimsMiddleware.UserIdKey]!;

        if (!TryParseUlidToGuid(id, out var conversationId))
        {
            return Results.BadRequest(new { error = "id is not a valid ulid" });
        }
        if (request is null || string.IsNullOrWhiteSpace(request.Content))
        {
            return Results.BadRequest(new { error = "content is required" });
        }

        // Read as int seconds at startup; converted to TimeSpan once.
        // Cultures vary on TimeSpan format strings, so we don't trust
        // IConfiguration.Get<TimeSpan>(); explicit int conversion is
        // unambiguous. Phase 1 hardcoded 60s; Phase 2 reads from
        // Assistant:TurnRequestTimeoutSeconds (default 180 — sized for
        // cold 70B model load + token-heavy responses).
        var turnTimeout = TimeSpan.FromSeconds(options.Value.TurnRequestTimeoutSeconds);

        // Link the request CT with the configured budget. Without this, a
        // stuck LLM call (cold-loading a 70B model is the realistic
        // worst case) holds the per-conversation advisory lock
        // indefinitely + every other concurrent POST /turns to the same
        // conversation queues forever. The lock + the timeout are NOT
        // separable concerns — see ConversationOrchestrator class doc.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(turnTimeout);

        try
        {
            // Phase 3.F: forward the optional tenant_role from
            // HttpContext.Items. Null when neither JWT's tenant_role
            // claim nor the deprecated X-Trellis-Tenant-Role header
            // was present — IToolExposurePolicy fails-closed on null
            // for any RequiredRole gate.
            var tenantRole = (string?)context.Items[TenantClaimsMiddleware.TenantRoleKey];

            var result = await orchestrator
                .HandleUserTurnAsync(
                    tenantId, userId, conversationId, request.Content,
                    toolNameFilter: request.Tools,
                    tenantRole: tenantRole,
                    cancellationToken: timeoutCts.Token)
                .ConfigureAwait(false);

            // Phase 3.A.2: ToolTurns is empty for the Phase 2 direct-LLM
            // path (request.Tools null/empty); populated when the agent
            // path ran. Wire shape stays additive — Phase 1+2 clients
            // that don't expect ToolTurns just see null in the JSON
            // body.
            return Results.Ok(new AppendTurnResponse(
                UserTurn: ToTurnDto(result.UserTurn),
                AssistantTurn: ToTurnDto(result.AssistantTurn))
            {
                ToolTurns = result.ToolTurns.Count == 0
                    ? null
                    : result.ToolTurns.Select(ToTurnDto).ToList(),
            });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not uuid-shaped"))
        {
            // Phase 3.A.2: agent path requires uuid-shaped tenantId per
            // C1 contract. Surface as 400 (same shape as POST /api/agent-runs).
            return Results.BadRequest(new
            {
                error = "tenant_id is not a valid uuid; agent-path conversations require uuid-shaped tenant identifiers per Phase 3.A C1 contract",
            });
        }
        catch (OperationCanceledException) when (
            timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Distinguish "we hit the configured budget" from "client
            // disconnected mid-request." 504 = our timeout fired;
            // client-side aborts don't generate a status code at all
            // (the connection is gone).
            return Results.Problem(
                statusCode: StatusCodes.Status504GatewayTimeout,
                detail: $"orchestrator exceeded {turnTimeout.TotalSeconds:0}s wall-clock budget; the per-conversation lock has been released.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            // The orchestrator + store both surface "conversation missing"
            // as InvalidOperationException with that substring. 404 maps
            // cleanly. A future tighter exception type (NotFoundException,
            // etc.) would be cleaner; punted to v1 since Phase 2 still
            // has the single error path here.
            return Results.NotFound(new { error = "conversation not found" });
        }
        catch (HttpRequestException ex)
        {
            // Phase 2: no model allowlist at create time, so an invalid
            // model tag (or transient Ollama outage, or unreachable
            // base URL) surfaces here when OllamaClient.StreamChatAsync
            // calls EnsureSuccessStatusCode on a 4xx/5xx upstream
            // response. 502 maps cleanly — it's the canonical
            // "upstream gateway returned an error" status. The body
            // includes the upstream message so operators can pattern-
            // match the failure (model-not-found, connection-refused,
            // etc.) in journalctl without having to dig.
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                detail: $"upstream Ollama call failed: {ex.Message}");
        }
    }

    // ---------------- Helpers: Ulid <-> Guid wire converters ----------------

    private static string GuidToUlidString(Guid g) => new Ulid(g).ToString();

    private static bool TryParseUlidToGuid(string s, out Guid result)
    {
        if (Ulid.TryParse(s, out var ulid))
        {
            result = ulid.ToGuid();
            return true;
        }
        result = Guid.Empty;
        return false;
    }

    private static TurnDto ToTurnDto(AssistantTurn t) => new(
        Id: GuidToUlidString(t.Id),
        Position: t.Position,
        Role: t.Role.ToString().ToLowerInvariant(),
        Content: t.Content,
        CreatedAt: t.CreatedAt)
    {
        ToolCallId = t.ToolCallId,
        ToolName = t.ToolName,
    };
}

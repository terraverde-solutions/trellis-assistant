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
/// <see cref="TenantHeadersMiddleware"/> has already validated +
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
    /// <summary>
    /// Phase 1 wall-clock per-request timeout for POST /turns. Phase 2
    /// raises to 180s for real Ollama (cold-loading nomic-embed-text
    /// or a 70B chat model on first call after restart routinely
    /// exceeds 60s). Stub LLM yields 5 chunks at 40ms apart =
    /// ~200 ms total — 60s is a 300x safety margin for Phase 1.
    /// </summary>
    public static readonly TimeSpan TurnRequestTimeout = TimeSpan.FromSeconds(60);

    public static void MapConversationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/conversations");

        group.MapPost("/", CreateConversationAsync);
        group.MapGet("/{id}", GetConversationAsync);
        group.MapPost("/{id}/turns", AppendTurnAsync);
    }

    // ---------------- POST /api/conversations ----------------

    public sealed record CreateConversationRequest(string? Channel);

    public sealed record CreateConversationResponse(
        string Id,
        string Channel,
        DateTime CreatedAt,
        DateTime UpdatedAt);

    private static async Task<IResult> CreateConversationAsync(
        CreateConversationRequest? request,
        HttpContext context,
        IAssistantConversationStore store,
        CancellationToken cancellationToken)
    {
        var tenantId = (string)context.Items[TenantHeadersMiddleware.TenantIdKey]!;
        var userId = (string)context.Items[TenantHeadersMiddleware.UserIdKey]!;

        // Default channel to "api" when omitted — matches the schema's
        // DEFAULT 'api'. Empty string is treated as "use default" too —
        // operators copy-pasting curl from docs sometimes leave the field
        // blank rather than omit it.
        var channel = string.IsNullOrWhiteSpace(request?.Channel) ? "api" : request.Channel;

        var newId = await store.CreateConversationAsync(tenantId, userId, channel, cancellationToken)
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
                CreatedAt: conv.CreatedAt,
                UpdatedAt: conv.UpdatedAt));
    }

    // ---------------- GET /api/conversations/{id} ----------------

    public sealed record GetConversationResponse(
        string Id,
        string Channel,
        DateTime CreatedAt,
        DateTime UpdatedAt,
        IReadOnlyList<TurnDto> Turns);

    public sealed record TurnDto(
        string Id,
        int Position,
        string Role,
        string Content,
        DateTime CreatedAt);

    private static async Task<IResult> GetConversationAsync(
        string id,
        HttpContext context,
        IAssistantConversationStore store,
        CancellationToken cancellationToken)
    {
        var tenantId = (string)context.Items[TenantHeadersMiddleware.TenantIdKey]!;
        var userId = (string)context.Items[TenantHeadersMiddleware.UserIdKey]!;

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
            CreatedAt: conv.CreatedAt,
            UpdatedAt: conv.UpdatedAt,
            Turns: turns.Select(ToTurnDto).ToList()));
    }

    // ---------------- POST /api/conversations/{id}/turns ----------------

    public sealed record AppendTurnRequest(string Content);

    public sealed record AppendTurnResponse(
        TurnDto UserTurn,
        TurnDto AssistantTurn);

    private static async Task<IResult> AppendTurnAsync(
        string id,
        AppendTurnRequest request,
        HttpContext context,
        ConversationOrchestrator orchestrator,
        CancellationToken cancellationToken)
    {
        var tenantId = (string)context.Items[TenantHeadersMiddleware.TenantIdKey]!;
        var userId = (string)context.Items[TenantHeadersMiddleware.UserIdKey]!;

        if (!TryParseUlidToGuid(id, out var conversationId))
        {
            return Results.BadRequest(new { error = "id is not a valid ulid" });
        }
        if (request is null || string.IsNullOrWhiteSpace(request.Content))
        {
            return Results.BadRequest(new { error = "content is required" });
        }

        // Link the request CT with a 60s budget. Without this, a stuck
        // LLM call (Phase 2 worst case: cold-loading a 70B model) holds
        // the per-conversation advisory lock indefinitely + every other
        // concurrent POST /turns to the same conversation queues forever.
        // The lock + the timeout are NOT separable concerns — see
        // ConversationOrchestrator class doc.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TurnRequestTimeout);

        try
        {
            var result = await orchestrator
                .HandleUserTurnAsync(tenantId, userId, conversationId, request.Content, timeoutCts.Token)
                .ConfigureAwait(false);

            return Results.Ok(new AppendTurnResponse(
                UserTurn: ToTurnDto(result.UserTurn),
                AssistantTurn: ToTurnDto(result.AssistantTurn)));
        }
        catch (OperationCanceledException) when (
            timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Distinguish "we hit the 60s budget" from "client disconnected
            // mid-request." 504 = our timeout fired; client-side aborts
            // don't generate a status code at all (the connection is gone).
            return Results.Problem(
                statusCode: StatusCodes.Status504GatewayTimeout,
                detail: $"orchestrator exceeded {TurnRequestTimeout.TotalSeconds:0}s wall-clock budget; the per-conversation lock has been released.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            // The orchestrator + store both surface "conversation missing"
            // as InvalidOperationException with that substring. 404 maps
            // cleanly. A future tighter exception type (NotFoundException,
            // etc.) would be cleaner; punted to v1 since Phase 1 has the
            // single error path here.
            return Results.NotFound(new { error = "conversation not found" });
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
        CreatedAt: t.CreatedAt);
}

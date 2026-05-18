using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Trellis.Assistant.Services;

/// <summary>
/// Phase 3.I production <see cref="IWorkflowClient"/> against
/// trellis-workflow's <c>POST /api/workflows/runs</c>. Wired in
/// <c>Program.cs</c> via
/// <c>AddHttpClient&lt;IWorkflowClient, HttpWorkflowClient&gt;</c>;
/// BaseAddress + Timeout configured from
/// <see cref="WorkflowScheduleOptions"/> using the LATE-RESOLUTION
/// pattern (config read inside the factory lambda at DI resolution
/// time).
///
/// <para>
/// AUTH (Phase 3.I fix-up): the Assistant mints its own service token
/// per outbound call via <see cref="IInternalTokenIssuer"/>
/// (client-credentials grant; <c>aud=trellis-workflow</c>). The user's
/// tenant identity is forwarded out-of-band as the
/// <c>X-Trellis-Tenant-Id</c> header — read from
/// <c>HttpContext.User.FindFirst("tenant_id")</c>. qwen's
/// TenantClaimsMiddleware honors this header on requests from trusted
/// internal callers (gated by the CC client_id, per paired qwen brief).
/// Verbatim-forwarding the inbound user JWT was the prior shape; it
/// 401'd every call because the user JWT carries
/// <c>aud=trellis-assistant</c>, not <c>aud=trellis-workflow</c>.
/// </para>
///
/// <para>
/// Failure mapping: 201 Created → Success; 401 → AuthFailed; 404 →
/// DefinitionNotFound; other 4xx → BadRequest with ProblemDetails.detail;
/// 5xx + transport + qwen-timeout → Transient. Token-issuance failures
/// (Auth service unreachable / 5xx / rejected credentials) also surface
/// as AuthFailed at the LLM layer — operator-actionable, NOT a
/// transient the LLM should retry. The distinction between caller
/// cancellation (<see cref="OperationCanceledException"/> with CT
/// requested → rethrow) and qwen-side timeout
/// (<see cref="TaskCanceledException"/> with CT NOT requested →
/// Transient) matches <see cref="HttpSearchClient"/>'s pattern.
/// </para>
/// </summary>
public sealed class HttpWorkflowClient : IWorkflowClient
{
    public const string SchedulePath = "api/workflows/runs";

    /// <summary>
    /// Target resource value passed to <see cref="IInternalTokenIssuer"/>
    /// — the Auth service mints a token with <c>aud</c> equal to this.
    /// Sibling siblings define their own audience; the Assistant must
    /// match the sibling's expectation.
    /// </summary>
    public const string TargetResource = "trellis-workflow";

    /// <summary>
    /// Out-of-band tenant forwarding header. qwen's
    /// <c>TenantClaimsMiddleware</c> honors this on requests from
    /// trusted CC client_ids per paired qwen brief.
    /// </summary>
    public const string TenantIdHeader = "X-Trellis-Tenant-Id";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _http;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IInternalTokenIssuer _tokenIssuer;
    private readonly ILogger<HttpWorkflowClient> _logger;

    public HttpWorkflowClient(
        HttpClient http,
        IHttpContextAccessor httpContextAccessor,
        IInternalTokenIssuer tokenIssuer,
        ILogger<HttpWorkflowClient> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        _tokenIssuer = tokenIssuer ?? throw new ArgumentNullException(nameof(tokenIssuer));
        _logger = logger;
    }

    public async Task<WorkflowScheduleResult> ScheduleByIdAsync(
        Guid workflowDefinitionId,
        string? initialInputJson,
        CancellationToken cancellationToken = default)
    {
        if (workflowDefinitionId == Guid.Empty)
        {
            return new WorkflowScheduleResult
            {
                Success = false,
                ErrorCode = WorkflowScheduleErrorCode.BadRequest,
                ErrorMessage = "workflow_schedule: workflowDefinitionId must be a non-empty GUID.",
            };
        }

        // Pre-flight: extract tenant_id from the inbound user JWT
        // BEFORE minting a service token. If no inbound JWT context, the
        // tool is being dispatched from a path that can't supply tenant
        // identity — surface a clear failure to the LLM (the tool is
        // only meaningful from a JWT-authenticated agent path).
        string tenantIdValue;
        try
        {
            tenantIdValue = RequireTenantIdFromInboundContext();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Workflow schedule: {Message}", ex.Message);
            return new WorkflowScheduleResult
            {
                Success = false,
                ErrorCode = WorkflowScheduleErrorCode.AuthFailed,
                ErrorMessage = ex.Message,
            };
        }

        // Mint the service token. Auth failures here (Auth unreachable
        // / 5xx / credentials rejected) surface as AuthFailed to the
        // LLM — operator-actionable, NOT a transient retry candidate.
        string internalToken;
        try
        {
            internalToken = await _tokenIssuer
                .GetAccessTokenAsync(TargetResource, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InternalTokenIssuanceException ex)
        {
            _logger.LogWarning(ex, "Workflow schedule: internal token issuance failed.");
            return new WorkflowScheduleResult
            {
                Success = false,
                ErrorCode = WorkflowScheduleErrorCode.AuthFailed,
                ErrorMessage = "Assistant could not authenticate to Workflow service.",
            };
        }

        var requestBody = BuildRequestBody(workflowDefinitionId, initialInputJson);
        using var request = new HttpRequestMessage(HttpMethod.Post, SchedulePath)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", internalToken);
        request.Headers.TryAddWithoutValidation(TenantIdHeader, tenantIdValue);

        _logger.LogInformation(
            "Workflow schedule: definition={DefinitionId} hasInput={HasInput}",
            workflowDefinitionId, initialInputJson is not null);

        HttpResponseMessage response;
        try
        {
            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller cancellation propagates as-is; the executor's OCE
            // filter handles it upstack as outcome=cancelled.
            throw;
        }
        catch (TaskCanceledException ex)
        {
            // HttpClient.Timeout fired (CT NOT requested) — qwen wedged.
            _logger.LogWarning(
                "Workflow schedule timed out after {Timeout}s.", _http.Timeout.TotalSeconds);
            return new WorkflowScheduleResult
            {
                Success = false,
                ErrorCode = WorkflowScheduleErrorCode.Transient,
                ErrorMessage = $"workflow_schedule: timed out after {_http.Timeout.TotalSeconds:0}s — {ex.Message}",
            };
        }
        catch (HttpRequestException ex)
        {
            // qwen unreachable / DNS / connection-refused.
            _logger.LogWarning(ex, "Workflow schedule transport error.");
            return new WorkflowScheduleResult
            {
                Success = false,
                ErrorCode = WorkflowScheduleErrorCode.Transient,
                ErrorMessage = $"workflow_schedule: transport error — {ex.Message}",
            };
        }

        using (response)
        {
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogWarning(
                    "Workflow schedule timed out reading body after {Timeout}s.", _http.Timeout.TotalSeconds);
                return new WorkflowScheduleResult
                {
                    Success = false,
                    ErrorCode = WorkflowScheduleErrorCode.Transient,
                    ErrorMessage = $"workflow_schedule: timed out reading body after {_http.Timeout.TotalSeconds:0}s — {ex.Message}",
                };
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Workflow schedule body-read transport error.");
                return new WorkflowScheduleResult
                {
                    Success = false,
                    ErrorCode = WorkflowScheduleErrorCode.Transient,
                    ErrorMessage = $"workflow_schedule: body-read transport error — {ex.Message}",
                };
            }

            if (response.StatusCode == HttpStatusCode.Created
                || response.StatusCode == HttpStatusCode.OK)
            {
                _logger.LogInformation(
                    "Workflow schedule succeeded: status={Status} body_bytes={Bytes}",
                    (int)response.StatusCode, body.Length);
                return new WorkflowScheduleResult
                {
                    Success = true,
                    ResponseBodyJson = body,
                };
            }

            return MapErrorResponse(response.StatusCode, body, workflowDefinitionId);
        }
    }

    private WorkflowScheduleResult MapErrorResponse(
        HttpStatusCode statusCode, string body, Guid workflowDefinitionId)
    {
        var detail = TryExtractProblemDetailsDetail(body);
        var statusCodeInt = (int)statusCode;
        var logLevel = statusCodeInt >= 500 ? LogLevel.Warning : LogLevel.Information;

        if (statusCode == HttpStatusCode.Unauthorized)
        {
            _logger.Log(logLevel,
                "Workflow schedule rejected: status={Status} detail={Detail}",
                statusCodeInt, detail ?? "(no detail)");
            return new WorkflowScheduleResult
            {
                Success = false,
                ErrorCode = WorkflowScheduleErrorCode.AuthFailed,
                ErrorMessage = "Workflow service rejected credentials.",
            };
        }
        if (statusCode == HttpStatusCode.NotFound)
        {
            _logger.Log(logLevel,
                "Workflow schedule: definition {DefinitionId} not found.",
                workflowDefinitionId);
            return new WorkflowScheduleResult
            {
                Success = false,
                ErrorCode = WorkflowScheduleErrorCode.DefinitionNotFound,
                ErrorMessage = $"Workflow definition '{workflowDefinitionId:D}' not found.",
            };
        }
        if (statusCodeInt >= 500)
        {
            _logger.LogWarning(
                "Workflow schedule failed (server error): status={Status} detail={Detail}",
                statusCodeInt, detail ?? "(no detail)");
            return new WorkflowScheduleResult
            {
                Success = false,
                ErrorCode = WorkflowScheduleErrorCode.Transient,
                ErrorMessage = $"workflow_schedule: workflow service returned {statusCodeInt} — {detail ?? "(no detail)"}",
            };
        }
        // Other 4xx — surface ProblemDetails verbatim.
        _logger.Log(logLevel,
            "Workflow schedule rejected: status={Status} detail={Detail}",
            statusCodeInt, detail ?? "(no detail)");
        return new WorkflowScheduleResult
        {
            Success = false,
            ErrorCode = WorkflowScheduleErrorCode.BadRequest,
            ErrorMessage = $"workflow_schedule: workflow service returned {statusCodeInt} — {detail ?? body}",
        };
    }

    /// <summary>
    /// Build the JSON request body for the schedule-by-id branch.
    /// Pure function; isolated so unit tests pin the wire shape without
    /// spinning up an HttpClient. <c>initialInputJson</c> when supplied
    /// must be a valid JSON object literal (the tool schema rejects
    /// other shapes upstack); it's embedded into the outer envelope
    /// raw rather than parsed-and-reserialized so the LLM-emitted shape
    /// flows through verbatim.
    /// </summary>
    public static string BuildRequestBody(Guid workflowDefinitionId, string? initialInputJson)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("workflowDefinitionId", workflowDefinitionId.ToString("D"));
            if (!string.IsNullOrWhiteSpace(initialInputJson))
            {
                writer.WritePropertyName("initialInputJson");
                using var doc = JsonDocument.Parse(initialInputJson);
                doc.RootElement.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Read the tenant_id claim from the inbound user JWT
    /// (HttpContext.User). Throws when no HttpContext is current or
    /// the claim is missing — the workflow_schedule tool is only
    /// meaningful from a JWT-authenticated agent path, and surfacing
    /// a clear failure beats sending a request with no tenant
    /// scope. The schedule call must NEVER reach qwen without a
    /// tenant identifier — qwen would refuse it (single-tenant
    /// scope is a security invariant), but failing fast at the
    /// Assistant boundary keeps logs cleaner.
    /// </summary>
    private string RequireTenantIdFromInboundContext()
    {
        var httpContext = _httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException(
                "workflow_schedule requires an inbound HttpContext with a user JWT; called from a context without one.");

        var claim = httpContext.User?.FindFirst("tenant_id")?.Value;
        if (string.IsNullOrWhiteSpace(claim))
        {
            throw new InvalidOperationException(
                "workflow_schedule requires a tenant_id claim on the inbound user JWT; claim missing or empty.");
        }
        return claim;
    }

    private static string? TryExtractProblemDetailsDetail(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            if (doc.RootElement.TryGetProperty("detail", out var detail)
                && detail.ValueKind == JsonValueKind.String)
            {
                return detail.GetString();
            }
            if (doc.RootElement.TryGetProperty("title", out var title)
                && title.ValueKind == JsonValueKind.String)
            {
                return title.GetString();
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Trellis.Assistant.Services;

/// <summary>
/// Phase 3.I production <see cref="IWorkflowClient"/> against
/// trellis-workflow's loopback <c>POST /api/workflows/runs</c>. Wired in
/// <c>Program.cs</c> via
/// <c>AddHttpClient&lt;IWorkflowClient, HttpWorkflowClient&gt;</c>;
/// HttpClient.BaseAddress + Timeout configured from
/// <see cref="WorkflowScheduleOptions"/> using the LATE-RESOLUTION
/// pattern (config read inside the factory lambda at DI resolution
/// time, so WebApplicationFactory overlays + future runtime config
/// changes are visible).
///
/// <para>
/// JWT propagation: reads the inbound request's
/// <c>Authorization: Bearer ...</c> header via
/// <see cref="IHttpContextAccessor"/> + attaches it verbatim to the
/// outbound qwen call. qwen's <c>TenantClaimsMiddleware</c> re-parses
/// the token to extract tenant_id, so the schedule call lands in the
/// correct tenant's scope. No HttpContext (e.g. called from a
/// non-HTTP context or from a test that doesn't set one up) → no
/// header attached → qwen rejects 401 → tool surfaces
/// <see cref="WorkflowScheduleErrorCode.AuthFailed"/> to the LLM.
/// </para>
///
/// <para>
/// Failure mapping (per Phase 3.I brief): 201 Created → Success; 401 →
/// AuthFailed; 404 → DefinitionNotFound; other 4xx → BadRequest with
/// ProblemDetails.detail; 5xx + transport + timeout → Transient. The
/// distinction between caller cancellation
/// (<see cref="OperationCanceledException"/> with CT requested → rethrow)
/// and qwen-side timeout (<see cref="TaskCanceledException"/> with CT
/// NOT requested → Transient) matches <see cref="HttpSearchClient"/>'s
/// pattern.
/// </para>
/// </summary>
public sealed class HttpWorkflowClient : IWorkflowClient
{
    /// <summary>
    /// Schedule-by-id endpoint path (relative to BaseAddress). qwen Phase
    /// 4.A canonical route per the Phase 3.I brief.
    /// </summary>
    public const string SchedulePath = "api/workflows/runs";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _http;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<HttpWorkflowClient> _logger;

    public HttpWorkflowClient(
        HttpClient http,
        IHttpContextAccessor httpContextAccessor,
        ILogger<HttpWorkflowClient> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
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

        var requestBody = BuildRequestBody(workflowDefinitionId, initialInputJson);
        using var request = new HttpRequestMessage(HttpMethod.Post, SchedulePath)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json"),
        };
        TryPropagateBearerToken(request);

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
    /// Propagate the inbound user JWT to the outbound qwen call. Reads
    /// <c>Authorization</c> from the current HttpContext's request
    /// headers; copies the value verbatim onto the outbound request.
    /// No-op when no HttpContext is current (out-of-band call from a
    /// background service / test) or when no Authorization header is
    /// present (anonymous caller — qwen will 401, surfaced as
    /// <see cref="WorkflowScheduleErrorCode.AuthFailed"/>).
    /// </summary>
    private void TryPropagateBearerToken(HttpRequestMessage request)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext is null)
        {
            return;
        }
        if (!httpContext.Request.Headers.TryGetValue("Authorization", out var values)
            || values.Count == 0)
        {
            return;
        }
        var raw = values[0];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }
        // AuthenticationHeaderValue.TryParse handles "Bearer <token>" cleanly;
        // fallback to raw assignment if a non-standard shape slips through.
        if (AuthenticationHeaderValue.TryParse(raw, out var parsed))
        {
            request.Headers.Authorization = parsed;
        }
        else
        {
            request.Headers.TryAddWithoutValidation("Authorization", raw);
        }
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

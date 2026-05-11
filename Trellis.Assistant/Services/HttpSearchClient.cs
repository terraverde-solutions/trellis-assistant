using System.Net;
using System.Text.Json;

namespace Trellis.Assistant.Services;

/// <summary>
/// Production <see cref="ISearchClient"/> against trellis-trainer's
/// loopback <c>GET /api/search</c>. Wired in <c>Program.cs</c> via
/// <c>AddHttpClient&lt;ISearchClient, HttpSearchClient&gt;</c>; the
/// HttpClient's <see cref="HttpClient.BaseAddress"/> + <c>Timeout</c>
/// are configured from <see cref="TrainerSearchOptions"/> using the
/// LATE-RESOLUTION pattern (config read inside the factory at DI
/// resolution time, so WebApplicationFactory overlays + future runtime
/// config changes are visible).
///
/// <para>
/// The <c>source=augmentation</c> tag is baked in here, not on the
/// caller's <see cref="SearchQuery"/>: every Assistant-side search is
/// audit-tagged as agent-driven, distinct from Trainer's own UI-driven
/// searches. Trainer's audit log uses the tag to discriminate the two
/// populations.
/// </para>
///
/// <para>
/// Pass-through (a) per Phase 3.B ratification: 2xx body bytes go
/// verbatim into <see cref="SearchClientResult.ResponseBodyJson"/> —
/// no parse, no reshape, no envelope. The LLM sees Trainer's PascalCase
/// fields directly. 4xx body is parsed for <c>ProblemDetails.Detail</c>
/// only to populate the error envelope.
/// </para>
/// </summary>
public sealed class HttpSearchClient : ISearchClient
{
    /// <summary>
    /// Audit-tag literal baked into every outgoing search. Trainer's
    /// <c>SearchAuditEvent</c> stores this verbatim so operator-side
    /// dashboards can split agent-driven vs UI-driven searches without
    /// per-caller tagging discipline.
    /// </summary>
    public const string AugmentationSourceTag = "augmentation";

    private readonly HttpClient _http;
    private readonly ILogger<HttpSearchClient> _logger;

    public HttpSearchClient(HttpClient http, ILogger<HttpSearchClient> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger;
    }

    public async Task<SearchClientResult> SearchAsync(
        SearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.Q))
        {
            return new SearchClientResult
            {
                Success = false,
                ErrorMessage = "search: query 'q' must be a non-empty string.",
            };
        }

        var requestUri = BuildSearchUri(query);

        // Privacy-conscious logging: query text at Debug only so
        // production-default Information-level logs don't capture user
        // PII; outcome at Information.
        _logger.LogDebug("Trainer search: q={Query} mode={Mode} k={TopK}",
            query.Q, query.Mode, query.K);

        HttpResponseMessage response;
        try
        {
            response = await _http
                .GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller cancellation propagates as-is; loop-level handling
            // (timeout, agent cancel) catches it upstream.
            throw;
        }
        catch (TaskCanceledException ex)
        {
            // HttpClient.Timeout fired during headers-fetch
            // (TaskCanceledException without the caller's CT being
            // cancelled). Operator-visible: Trainer unreachable / overloaded.
            _logger.LogWarning(
                "Trainer search timed out after {Timeout}s.", _http.Timeout.TotalSeconds);
            return new SearchClientResult
            {
                Success = false,
                ErrorMessage = $"search: timed out after {_http.Timeout.TotalSeconds:0}s — {ex.Message}",
            };
        }
        catch (HttpRequestException ex)
        {
            // Operator-visible: Trainer down / connection refused / DNS.
            _logger.LogWarning(ex, "Trainer search transport error.");
            return new SearchClientResult
            {
                Success = false,
                ErrorMessage = $"search: transport error — {ex.Message}",
            };
        }

        using (response)
        {
            // With HttpCompletionOption.ResponseHeadersRead the connection
            // can return headers and then time out mid-body. ReadAsStringAsync
            // must carry its own try/catch so an HttpClient.Timeout-fired-
            // during-body-read surfaces as a Trainer timeout (Warning), not
            // as an uncaught throw that the executor's broad catch logs as
            // a generic tool-threw warning. Caller-CT cancellation rethrows
            // unchanged so the executor's OCE filter handles it upstack.
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
                    "Trainer search timed out reading body after {Timeout}s.",
                    _http.Timeout.TotalSeconds);
                return new SearchClientResult
                {
                    Success = false,
                    ErrorMessage = $"search: timed out reading body after {_http.Timeout.TotalSeconds:0}s — {ex.Message}",
                };
            }
            catch (HttpRequestException ex)
            {
                // Mid-stream connection break (server closed early, etc.)
                _logger.LogWarning(ex, "Trainer search body-read transport error.");
                return new SearchClientResult
                {
                    Success = false,
                    ErrorMessage = $"search: body-read transport error — {ex.Message}",
                };
            }

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Trainer search succeeded: status={Status} body_bytes={Bytes}",
                    (int)response.StatusCode, body.Length);
                return new SearchClientResult
                {
                    Success = true,
                    ResponseBodyJson = body,
                };
            }

            // Split log level by class: 5xx = Trainer errored (operator-
            // visible Warning); 4xx = LLM-arg error (schema validation
            // catches it on the agent path; Information).
            var detail = TryExtractProblemDetailsDetail(body) ?? body;
            var logLevel = (int)response.StatusCode >= 500
                ? LogLevel.Warning
                : LogLevel.Information;
            _logger.Log(logLevel,
                "Trainer search failed: status={Status} detail={Detail}",
                (int)response.StatusCode, detail);
            return new SearchClientResult
            {
                Success = false,
                ErrorMessage = $"search: trainer returned {(int)response.StatusCode} — {detail}",
            };
        }
    }

    /// <summary>
    /// Build the <c>api/search</c> request URI from a
    /// <see cref="SearchQuery"/>. Pure function; isolated from the
    /// HttpClient so unit tests can pin escaping + multi-value parameter
    /// shape without spinning up a fake handler.
    ///
    /// <para>
    /// Returns a relative URI ("api/search?...") to be combined with
    /// the HttpClient's <see cref="HttpClient.BaseAddress"/>. Trailing
    /// slash on the base URL matters for proper relative resolution.
    /// </para>
    /// </summary>
    public static Uri BuildSearchUri(SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var parts = new List<string>(8)
        {
            "q=" + WebUtility.UrlEncode(query.Q),
            "source=" + AugmentationSourceTag,
        };
        if (query.K is int k)
        {
            parts.Add("k=" + k.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (query.Mode is SearchMode mode)
        {
            parts.Add("mode=" + ModeToWire(mode));
        }
        if (query.Since is DateTimeOffset since)
        {
            // ISO 8601 round-trip (matches Trainer's [FromQuery] DateTime
            // binding default — InvariantCulture, "o" round-trip format).
            parts.Add("since=" + WebUtility.UrlEncode(
                since.ToString("o", System.Globalization.CultureInfo.InvariantCulture)));
        }
        if (query.DocumentIds is { Count: > 0 } docs)
        {
            foreach (var id in docs)
            {
                parts.Add("documentId=" + id.ToString("D",
                    System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        if (query.ContentTypes is { Count: > 0 } cts)
        {
            foreach (var ct in cts)
            {
                if (string.IsNullOrEmpty(ct))
                {
                    continue;
                }
                parts.Add("contentType=" + WebUtility.UrlEncode(ct));
            }
        }
        return new Uri("api/search?" + string.Join("&", parts), UriKind.Relative);
    }

    private static string ModeToWire(SearchMode mode) => mode switch
    {
        SearchMode.Vector => "vector",
        SearchMode.Lexical => "lexical",
        SearchMode.Hybrid => "hybrid",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown SearchMode"),
    };

    /// <summary>
    /// Best-effort extraction of <c>detail</c> from an RFC 7807
    /// <c>ProblemDetails</c> body. Returns <c>null</c> when the body
    /// doesn't parse as JSON or doesn't carry a <c>detail</c> string —
    /// the caller falls back to the raw body in that case.
    /// </summary>
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
            // Some ASP.NET Core variants emit "title" without "detail" —
            // fall back to that so we still surface something useful.
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

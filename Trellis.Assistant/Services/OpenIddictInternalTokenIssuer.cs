using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Trellis.Assistant.Services;

/// <summary>
/// Phase 3.I fix-up: OAuth2 client-credentials token issuer for the
/// Assistant's outbound calls to sibling internal services. Mints a
/// token per <c>targetResource</c> with the matching audience, caches
/// it until the configured skew window before <c>exp</c>, and serializes
/// concurrent refreshes per resource so a stampede mints once.
///
/// <para>
/// Wire shape matches the OAuth2 RFC 6749 client-credentials grant with
/// OpenIddict's <c>resource</c> parameter that requests a token whose
/// <c>aud</c> equals the resource value (the Auth service's
/// OpenIddict configuration honors this). The Auth service is the
/// shared issuer for the Trellis system; siblings validate against
/// it on inbound traffic.
/// </para>
///
/// <para>
/// Single-resource scope: one cache entry per
/// <paramref name="targetResource"/>; one SemaphoreSlim per resource so
/// in-flight refresh on one resource doesn't block another. Long-lived
/// host singleton; the per-resource state grows by the number of
/// distinct siblings (in practice: 2-3, never user-bounded), so
/// unbounded growth isn't a concern.
/// </para>
///
/// <para>
/// Failure shape: token-endpoint non-2xx, transport errors, and
/// timeouts ALL surface as
/// <see cref="InternalTokenIssuanceException"/> at the call site.
/// <see cref="HttpWorkflowClient"/> catches at the schedule call site
/// and surfaces as <see cref="WorkflowScheduleErrorCode.AuthFailed"/> to
/// the LLM. Operator-actionable (the Auth service is misconfigured /
/// unreachable / the Assistant's credentials were rejected), NOT a
/// LLM-retryable transient.
/// </para>
/// </summary>
public sealed class OpenIddictInternalTokenIssuer : IInternalTokenIssuer, IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly IOptionsMonitor<InternalTokenIssuerOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OpenIddictInternalTokenIssuer> _logger;

    private readonly ConcurrentDictionary<string, CachedToken> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshGates = new(StringComparer.Ordinal);

    public OpenIddictInternalTokenIssuer(
        HttpClient http,
        IOptionsMonitor<InternalTokenIssuerOptions> options,
        TimeProvider timeProvider,
        ILogger<OpenIddictInternalTokenIssuer> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger;
    }

    public async Task<string> GetAccessTokenAsync(string targetResource, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetResource);

        // Fast-path: cache hit with comfortable lifetime remaining.
        if (TryGetCached(targetResource, out var cached))
        {
            return cached;
        }

        // Slow-path: serialize refreshes per resource so a stampede
        // mints once. Each resource gets its own gate so trellis-workflow
        // + trellis-server (future) refreshes don't block each other.
        var gate = _refreshGates.GetOrAdd(targetResource, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check under the gate — a peer refresh may have just
            // populated the cache while we waited.
            if (TryGetCached(targetResource, out cached))
            {
                return cached;
            }

            var (accessToken, expiresAt) = await RequestTokenAsync(targetResource, ct).ConfigureAwait(false);
            _cache[targetResource] = new CachedToken(accessToken, expiresAt);
            return accessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    private bool TryGetCached(string targetResource, out string accessToken)
    {
        accessToken = "";
        if (!_cache.TryGetValue(targetResource, out var entry))
        {
            return false;
        }
        var opts = _options.CurrentValue;
        var now = _timeProvider.GetUtcNow();
        if (entry.ExpiresAt - now <= TimeSpan.FromSeconds(opts.RefreshSkewSeconds))
        {
            return false;
        }
        accessToken = entry.AccessToken;
        return true;
    }

    private async Task<(string AccessToken, DateTimeOffset ExpiresAt)> RequestTokenAsync(
        string targetResource, CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        if (string.IsNullOrWhiteSpace(opts.TokenEndpoint))
        {
            throw new InternalTokenIssuanceException(
                "Assistant:Auth:InternalClient:TokenEndpoint is not configured.");
        }
        if (string.IsNullOrWhiteSpace(opts.ClientId) || string.IsNullOrWhiteSpace(opts.ClientSecret))
        {
            throw new InternalTokenIssuanceException(
                "Assistant:Auth:InternalClient:ClientId / ClientSecret are not configured. " +
                "Production deploys set these via env var.");
        }

        var formFields = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = opts.ClientId,
            ["client_secret"] = opts.ClientSecret,
            ["resource"] = targetResource,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, opts.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(formFields),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        _logger.LogDebug(
            "InternalTokenIssuer: requesting token for resource={Resource}", targetResource);

        HttpResponseMessage response;
        try
        {
            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancellation propagates as-is (executor's OCE
            // filter handles it upstack as outcome=cancelled).
            throw;
        }
        catch (TaskCanceledException ex)
        {
            // HttpClient.Timeout fired (CT NOT requested) — Auth
            // unresponsive. Surface fast so the agent loop doesn't hang.
            _logger.LogWarning(
                "InternalTokenIssuer: token endpoint timed out after {Timeout}s.",
                _http.Timeout.TotalSeconds);
            throw new InternalTokenIssuanceException(
                $"Auth token endpoint timed out after {_http.Timeout.TotalSeconds:0}s.", ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "InternalTokenIssuer: token endpoint transport error.");
            throw new InternalTokenIssuanceException(
                $"Auth token endpoint transport error: {ex.Message}", ex);
        }

        using (response)
        {
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InternalTokenIssuanceException(
                    $"Auth token endpoint body-read error: {ex.Message}", ex);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "InternalTokenIssuer: token endpoint returned {Status} for resource={Resource}.",
                    (int)response.StatusCode, targetResource);
                throw new InternalTokenIssuanceException(
                    $"Auth token endpoint returned {(int)response.StatusCode}: {Truncate(body, 200)}");
            }

            TokenResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<TokenResponse>(body, JsonOpts);
            }
            catch (JsonException ex)
            {
                throw new InternalTokenIssuanceException(
                    "Auth token endpoint returned malformed JSON.", ex);
            }
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.AccessToken))
            {
                throw new InternalTokenIssuanceException(
                    "Auth token endpoint response missing 'access_token'.");
            }

            var expiresIn = parsed.ExpiresIn > 0 ? parsed.ExpiresIn : 300;
            var expiresAt = _timeProvider.GetUtcNow().AddSeconds(expiresIn);
            _logger.LogInformation(
                "InternalTokenIssuer: minted token for resource={Resource} expires_in={ExpiresIn}s",
                targetResource, expiresIn);
            return (parsed.AccessToken, expiresAt);
        }
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    public void Dispose()
    {
        foreach (var sem in _refreshGates.Values)
        {
            sem.Dispose();
        }
    }

    private sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAt);

    private sealed record TokenResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("access_token")]
        public string AccessToken { get; init; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("token_type")]
        public string TokenType { get; init; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }
    }
}

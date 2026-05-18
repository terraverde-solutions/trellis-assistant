using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Trellis.Assistant.Services;
using Xunit;

namespace Trellis.Assistant.Tests.Services;

/// <summary>
/// Phase 3.I fix-up pins for <see cref="OpenIddictInternalTokenIssuer"/>.
/// Five pins cover the contract: happy-path caching, expiry-skew refresh,
/// 5xx + transport failures throwing
/// <see cref="InternalTokenIssuanceException"/>, and concurrent-call
/// stampede control (one outbound mint per resource).
/// </summary>
public sealed class OpenIddictInternalTokenIssuerTests
{
    private const string TargetResource = "trellis-workflow";
    private const string SuccessBody = """
        {"access_token":"test-token-abc","token_type":"Bearer","expires_in":300}
        """;

    [Fact]
    public async Task GetAccessTokenAsync_HappyPath_CachesUntilExpiry()
    {
        // Phase 3.I fix-up pin: first call hits the token endpoint;
        // second call within (exp - skew) returns the cached token
        // without a second outbound request.
        var (issuer, handler, _) = NewIssuer(_ => SuccessResponse());

        var t1 = await issuer.GetAccessTokenAsync(TargetResource, CancellationToken.None);
        var t2 = await issuer.GetAccessTokenAsync(TargetResource, CancellationToken.None);

        t1.Should().Be("test-token-abc");
        t2.Should().Be("test-token-abc",
            "cached token returned verbatim — same string identity for the same mint");
        handler.CallCount.Should().Be(1,
            "second call inside the skew window must hit the cache, NOT the token endpoint");
    }

    [Fact]
    public async Task GetAccessTokenAsync_AfterExpirySkew_RefreshesAutomatically()
    {
        // Mint a token with expires_in=300s + RefreshSkewSeconds=60.
        // Advance the fake clock by 245s — token still has 55s left,
        // within the skew window → must refresh. Both mints succeed
        // with distinct token values so we can pin which one was
        // returned.
        var mintCount = 0;
        var (issuer, handler, fakeTime) = NewIssuer(_ =>
        {
            mintCount++;
            return MakeJsonResponse(
                HttpStatusCode.OK,
                $$"""
                {"access_token":"token-mint-{{mintCount}}","token_type":"Bearer","expires_in":300}
                """);
        });

        var first = await issuer.GetAccessTokenAsync(TargetResource, CancellationToken.None);
        first.Should().Be("token-mint-1");

        fakeTime.Advance(TimeSpan.FromSeconds(245));

        var second = await issuer.GetAccessTokenAsync(TargetResource, CancellationToken.None);

        second.Should().Be("token-mint-2",
            "clock advanced past (exp - RefreshSkewSeconds) → cache eviction → fresh mint");
        handler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task GetAccessTokenAsync_AuthEndpointReturns500_ThrowsInternalTokenIssuanceException()
    {
        var (issuer, _, _) = NewIssuer(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("auth service crashed", Encoding.UTF8, "text/plain"),
        });

        var act = () => issuer.GetAccessTokenAsync(TargetResource, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<InternalTokenIssuanceException>();
        thrown.Which.Message.Should().Contain("500",
            "operator log carries the status code so misconfiguration is visible");
    }

    [Fact]
    public async Task GetAccessTokenAsync_AuthEndpointTransportError_ThrowsInternalTokenIssuanceException()
    {
        var (issuer, _, _) = NewIssuer(_ => throw new HttpRequestException("connection refused"));

        var act = () => issuer.GetAccessTokenAsync(TargetResource, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<InternalTokenIssuanceException>();
        thrown.Which.Message.Should().Contain("transport error",
            "transport failures surface as InternalTokenIssuanceException — NOT propagated as raw HttpRequestException");
    }

    [Fact]
    public async Task GetAccessTokenAsync_ConcurrentCallsDuringRefresh_OnlyOneOutboundCall()
    {
        // Phase 3.I fix-up: stampede control. 32 concurrent first-time
        // callers must serialize through the per-resource semaphore so
        // exactly one outbound mint happens — pinning the contract
        // matching JwtClientCredentialsTokenStore_GetTokenAsync_ConcurrentColdStart_FetchesOnce
        // from Trellis.Core (Macro 2 PR 6.1).
        var gate = new SemaphoreSlim(0, 1);
        var (issuer, handler, _) = NewIssuerAsync(async _ =>
        {
            // Block the first response so concurrent callers pile up
            // on the per-resource semaphore in the issuer. When the
            // test releases the gate, the response unblocks.
            await gate.WaitAsync().ConfigureAwait(false);
            return SuccessResponse();
        });

        var tasks = Enumerable.Range(0, 32)
            .Select(_ => issuer.GetAccessTokenAsync(TargetResource, CancellationToken.None))
            .ToArray();

        // Release the mint so the in-flight mint completes; the queued
        // callers then re-check the cache + return without minting.
        gate.Release();

        var results = await Task.WhenAll(tasks);

        results.Should().AllBe("test-token-abc");
        handler.CallCount.Should().Be(1,
            "32 concurrent first-time callers must result in exactly 1 outbound mint — stampede control via per-resource semaphore");
    }

    // ---------------- helpers ----------------

    private static (OpenIddictInternalTokenIssuer issuer, RecordingHandler handler, FakeTimeProvider time) NewIssuer(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new RecordingHandler(respond);
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://test-auth/"),
            Timeout = TimeSpan.FromSeconds(5),
        };
        var options = new OptionsMonitorWrapper<InternalTokenIssuerOptions>(new InternalTokenIssuerOptions
        {
            TokenEndpoint = "http://test-auth/connect/token",
            ClientId = "trellis-assistant-internal",
            ClientSecret = "secret",
            RequestTimeoutSeconds = 5,
            RefreshSkewSeconds = 60,
        });
        var time = new FakeTimeProvider(startDateTime: DateTimeOffset.UnixEpoch.AddYears(56));
        var issuer = new OpenIddictInternalTokenIssuer(
            http, options, time, NullLogger<OpenIddictInternalTokenIssuer>.Instance);
        return (issuer, handler, time);
    }

    private static (OpenIddictInternalTokenIssuer issuer, RecordingHandler handler, FakeTimeProvider time) NewIssuerAsync(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        var handler = new RecordingHandler(respond);
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://test-auth/"),
            Timeout = TimeSpan.FromSeconds(5),
        };
        var options = new OptionsMonitorWrapper<InternalTokenIssuerOptions>(new InternalTokenIssuerOptions
        {
            TokenEndpoint = "http://test-auth/connect/token",
            ClientId = "trellis-assistant-internal",
            ClientSecret = "secret",
            RequestTimeoutSeconds = 5,
            RefreshSkewSeconds = 60,
        });
        var time = new FakeTimeProvider(startDateTime: DateTimeOffset.UnixEpoch.AddYears(56));
        var issuer = new OpenIddictInternalTokenIssuer(
            http, options, time, NullLogger<OpenIddictInternalTokenIssuer>.Instance);
        return (issuer, handler, time);
    }

    private static HttpResponseMessage SuccessResponse()
        => MakeJsonResponse(HttpStatusCode.OK, SuccessBody);

    private static HttpResponseMessage MakeJsonResponse(HttpStatusCode status, string body)
        => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = req => Task.FromResult(respond(req));
        }

        public RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        {
            _respond = respond;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return await _respond(request).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Minimal IOptionsMonitor for tests — wraps a single value so the
    /// issuer can call CurrentValue without a full OptionsMonitor
    /// infrastructure setup.
    /// </summary>
    private sealed class OptionsMonitorWrapper<T> : IOptionsMonitor<T>
        where T : class
    {
        public OptionsMonitorWrapper(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

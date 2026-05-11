using System.Net;
using System.Net.Http;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Trellis.Assistant.Services;
using Xunit;

namespace Trellis.Assistant.Tests.Services;

/// <summary>
/// Pins for <see cref="HttpSearchClient"/> — Phase 3.B Trainer
/// integration client. Two seams under test:
/// <list type="bullet">
/// <item><see cref="HttpSearchClient.BuildSearchUri"/> — pure URL
/// builder; tests pin param escaping, multi-value query parameter
/// shape (repeated <c>documentId=</c> / <c>contentType=</c>), null
/// elision, ISO 8601 <c>since</c> formatting, mode-enum-to-wire
/// translation, and the baked-in <c>source=augmentation</c>
/// tag.</item>
/// <item><see cref="HttpSearchClient.SearchAsync"/> — exercised via a
/// recording <see cref="DelegatingHandler"/>; tests pin 2xx body
/// pass-through, 4xx with <c>ProblemDetails.Detail</c> extraction,
/// 4xx with <c>title</c> fallback, 4xx with no parseable body, 5xx,
/// transport error (HttpRequestException), timeout
/// (TaskCanceledException), and the empty-Q early-return.</item>
/// </list>
/// </summary>
public sealed class HttpSearchClientTests
{
    // ----------------- BuildSearchUri pins -----------------

    [Fact]
    public void BuildSearchUri_AlwaysBakesSourceAugmentationTag()
    {
        var uri = HttpSearchClient.BuildSearchUri(new SearchQuery { Q = "refund" });
        uri.ToString().Should().Contain("source=augmentation",
            "every Assistant-side search is tagged as agent-driven for Trainer's audit-log discrimination");
    }

    [Fact]
    public void BuildSearchUri_NullOptionalParams_AreElidedFromUri()
    {
        var uri = HttpSearchClient.BuildSearchUri(new SearchQuery { Q = "refund" });
        var s = uri.ToString();
        s.Should().NotContain("k=");
        s.Should().NotContain("mode=");
        s.Should().NotContain("since=");
        s.Should().NotContain("documentId=");
        s.Should().NotContain("contentType=");
    }

    [Fact]
    public void BuildSearchUri_EncodesSpacesAndSpecialsInQuery()
    {
        var uri = HttpSearchClient.BuildSearchUri(new SearchQuery { Q = "refund & receipts?" });
        var s = uri.ToString();
        s.Should().Contain("q=refund+%26+receipts%3F",
            "WebUtility.UrlEncode percent-encodes reserved chars and spaces as +");
    }

    [Fact]
    public void BuildSearchUri_MapsAllThreeModes()
    {
        HttpSearchClient.BuildSearchUri(new SearchQuery { Q = "x", Mode = SearchMode.Vector }).ToString()
            .Should().Contain("mode=vector");
        HttpSearchClient.BuildSearchUri(new SearchQuery { Q = "x", Mode = SearchMode.Lexical }).ToString()
            .Should().Contain("mode=lexical");
        HttpSearchClient.BuildSearchUri(new SearchQuery { Q = "x", Mode = SearchMode.Hybrid }).ToString()
            .Should().Contain("mode=hybrid");
    }

    [Fact]
    public void BuildSearchUri_MultipleDocumentIds_EmitsRepeatedQueryParam()
    {
        var a = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var b = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var uri = HttpSearchClient.BuildSearchUri(new SearchQuery
        {
            Q = "x",
            DocumentIds = new[] { a, b },
        });
        var s = uri.ToString();
        s.Should().Contain("documentId=11111111-1111-1111-1111-111111111111");
        s.Should().Contain("documentId=22222222-2222-2222-2222-222222222222");
    }

    [Fact]
    public void BuildSearchUri_MultipleContentTypes_EmitsRepeatedQueryParam_EncodedSlash()
    {
        var uri = HttpSearchClient.BuildSearchUri(new SearchQuery
        {
            Q = "x",
            ContentTypes = new[] { "application/pdf", "text/markdown" },
        });
        var s = uri.ToString();
        s.Should().Contain("contentType=application%2Fpdf");
        s.Should().Contain("contentType=text%2Fmarkdown");
    }

    [Fact]
    public void BuildSearchUri_SinceFormatsAsRoundTripIso8601()
    {
        var when = new DateTimeOffset(2026, 5, 11, 14, 30, 0, TimeSpan.Zero);
        var uri = HttpSearchClient.BuildSearchUri(new SearchQuery { Q = "x", Since = when });
        var s = uri.ToString();
        // "o" round-trip format produces "2026-05-11T14:30:00.0000000+00:00";
        // colons + plus get percent-encoded.
        s.Should().Contain("since=2026-05-11T14%3A30%3A00.0000000%2B00%3A00");
    }

    [Fact]
    public void BuildSearchUri_K_FormatsAsInvariantInteger()
    {
        var uri = HttpSearchClient.BuildSearchUri(new SearchQuery { Q = "x", K = 7 });
        uri.ToString().Should().Contain("k=7");
    }

    // ----------------- SearchAsync pins -----------------

    [Fact]
    public async Task SearchAsync_EmptyQ_ReturnsEarly_Failure()
    {
        var (client, handler) = BuildClient(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var result = await client.SearchAsync(new SearchQuery { Q = "" });
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("must be a non-empty string");
        handler.CapturedRequests.Should().BeEmpty("local guard prevents HTTP round-trip on bad input");
    }

    [Fact]
    public async Task SearchAsync_2xxBody_ReturnsBodyVerbatim()
    {
        const string payload = """[{"DocumentId":"abc","Score":0.9}]""";
        var (client, _) = BuildClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        });
        var result = await client.SearchAsync(new SearchQuery { Q = "refund" });
        result.Success.Should().BeTrue();
        result.ResponseBodyJson.Should().Be(payload,
            "pass-through (a): 2xx body bytes go verbatim into ResponseBodyJson with no reshape");
    }

    [Fact]
    public async Task SearchAsync_4xxProblemDetails_ExtractsDetail()
    {
        const string body = """{"type":"about:blank","title":"Bad Request","status":400,"detail":"q must be non-empty"}""";
        var (client, _) = BuildClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/problem+json"),
        });
        var result = await client.SearchAsync(new SearchQuery { Q = "x" });
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("400");
        result.ErrorMessage.Should().Contain("q must be non-empty");
    }

    [Fact]
    public async Task SearchAsync_4xxWithoutDetail_FallsBackToTitle()
    {
        const string body = """{"title":"Bad Request","status":400}""";
        var (client, _) = BuildClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/problem+json"),
        });
        var result = await client.SearchAsync(new SearchQuery { Q = "x" });
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Bad Request");
    }

    [Fact]
    public async Task SearchAsync_4xxWithUnparseableBody_FallsBackToRawBody()
    {
        const string body = "plain text 400 — not JSON";
        var (client, _) = BuildClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain"),
        });
        var result = await client.SearchAsync(new SearchQuery { Q = "x" });
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("plain text 400");
    }

    [Fact]
    public async Task SearchAsync_5xx_MapsToFailureWithStatusCode()
    {
        var (client, _) = BuildClient(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("server boom", Encoding.UTF8, "text/plain"),
        });
        var result = await client.SearchAsync(new SearchQuery { Q = "x" });
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("500");
    }

    [Fact]
    public async Task SearchAsync_HttpRequestException_MapsToTransportErrorMessage()
    {
        var (client, _) = BuildClient(_ => throw new HttpRequestException("connection refused"));
        var result = await client.SearchAsync(new SearchQuery { Q = "x" });
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("transport error");
        result.ErrorMessage.Should().Contain("connection refused");
    }

    [Fact]
    public async Task SearchAsync_TaskCanceledFromTimeout_MapsToTimeoutErrorMessage()
    {
        // TaskCanceledException without the caller's CT being cancelled
        // signals HttpClient.Timeout fired. The client maps it to a
        // structured failure (not a throw).
        var (client, _) = BuildClient(_ => throw new TaskCanceledException("timed out"));
        var result = await client.SearchAsync(new SearchQuery { Q = "x" });
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("timed out");
    }

    [Fact]
    public async Task SearchAsync_TimeoutDuringBodyRead_MapsToTimeoutError()
    {
        // PR #9 review Blocker 1: with HttpCompletionOption.ResponseHeadersRead
        // the connection can return headers (200 OK) and then time out
        // mid-stream. The body-read TaskCanceledException must be caught
        // + mapped to a Trainer-timeout failure, NOT propagate uncaught
        // past the typed catches → executor's broad catch → mislogged as
        // a generic tool-threw warning.
        //
        // Simulated via an HttpContent subclass that throws
        // TaskCanceledException when its stream is read.
        var (client, _) = BuildClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new TimeoutDuringReadContent(),
        });
        var result = await client.SearchAsync(new SearchQuery { Q = "x" });
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("timed out reading body",
            "body-read timeout maps to a distinct error message so operators can identify the failure phase");
    }

    [Fact]
    public async Task SearchAsync_DoesNotAttachAuthorizationHeader()
    {
        // Negative-space pin: loopback-trust posture means no JWT, no
        // bearer, no header attached. Future regression that adds a
        // header (e.g. wiring JwtClientCredentialsHandler to this typed
        // client by accident) would silently change the wire shape;
        // this test fails the regression before review.
        var (client, handler) = BuildClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json"),
        });
        await client.SearchAsync(new SearchQuery { Q = "x" });
        handler.CapturedRequests.Should().HaveCount(1);
        var req = handler.CapturedRequests[0];
        req.Headers.Authorization.Should().BeNull("loopback-trust v0: no bearer");
        req.Headers.Contains("Authorization").Should().BeFalse();
        req.Headers.Contains("X-Trellis-Tenant-Id").Should().BeFalse(
            "Trainer is single-tenant; no tenant headers");
        req.Headers.Contains("X-Trellis-User-Id").Should().BeFalse();
    }

    [Fact]
    public async Task SearchAsync_CallerCancellation_PropagatesAsOperationCanceled()
    {
        // Caller-side CT cancellation should NOT be converted to a
        // structured failure — the agent loop's CT handling catches it
        // upstack. The client rethrows OperationCanceledException.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var (client, _) = BuildClient(_ => throw new TaskCanceledException("cancelled"));
        var act = () => client.SearchAsync(new SearchQuery { Q = "x" }, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ----------------- Helpers -----------------

    private static (HttpSearchClient client, RecordingHandler handler) BuildClient(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new RecordingHandler(respond);
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:5114/"),
            Timeout = TimeSpan.FromSeconds(5),
        };
        var client = new HttpSearchClient(http, NullLogger<HttpSearchClient>.Instance);
        return (client, handler);
    }

    /// <summary>
    /// HttpContent subclass that throws <see cref="TaskCanceledException"/>
    /// when its body stream is serialized. Simulates HttpClient.Timeout
    /// firing mid-body — the headers already returned 200 OK, but
    /// <see cref="HttpContent.ReadAsStringAsync"/> raises the cancellation
    /// while reading the stream. Pinned by
    /// <c>SearchAsync_TimeoutDuringBodyRead_MapsToTimeoutError</c>.
    /// </summary>
    private sealed class TimeoutDuringReadContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
            => throw new TaskCanceledException("body read timed out");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public List<HttpRequestMessage> CapturedRequests { get; } = new();

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CapturedRequests.Add(request);
            try
            {
                return Task.FromResult(_respond(request));
            }
            catch (Exception ex)
            {
                return Task.FromException<HttpResponseMessage>(ex);
            }
        }
    }
}

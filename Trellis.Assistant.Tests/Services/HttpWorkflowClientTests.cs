using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Trellis.Assistant.Services;
using Xunit;

namespace Trellis.Assistant.Tests.Services;

/// <summary>
/// Phase 3.I pins for <see cref="HttpWorkflowClient"/>. Two seams under
/// test: the pure-function <see cref="HttpWorkflowClient.BuildRequestBody"/>
/// (JSON envelope shape) and the <see cref="HttpWorkflowClient.ScheduleByIdAsync"/>
/// HTTP flow exercised via a recording <see cref="DelegatingHandler"/>.
///
/// <para>
/// Failure-mapping pins per the Phase 3.I brief:
/// 401→AuthFailed, 404→DefinitionNotFound, 5xx/transport/timeout→Transient,
/// other 4xx→BadRequest with ProblemDetails detail. Plus a JWT-propagation
/// pin (Authorization header copied from IHttpContextAccessor's HttpContext
/// to the outbound request).
/// </para>
/// </summary>
public sealed class HttpWorkflowClientTests
{
    private static readonly Guid SampleDefinitionId = Guid.Parse("88888888-8888-8888-8888-888888888888");

    // ----------------- BuildRequestBody pins -----------------

    [Fact]
    public void BuildRequestBody_NoInput_EmitsWorkflowDefinitionIdOnly()
    {
        var body = HttpWorkflowClient.BuildRequestBody(SampleDefinitionId, initialInputJson: null);
        body.Should().Be("""{"workflowDefinitionId":"88888888-8888-8888-8888-888888888888"}""");
    }

    [Fact]
    public void BuildRequestBody_WithInitialInput_EmbedsObjectVerbatim()
    {
        var body = HttpWorkflowClient.BuildRequestBody(
            SampleDefinitionId,
            initialInputJson: """{"email":"a@b.co","limit":5}""");
        body.Should().Contain("\"workflowDefinitionId\":\"88888888-8888-8888-8888-888888888888\"");
        body.Should().Contain("\"initialInputJson\":{\"email\":\"a@b.co\",\"limit\":5}");
    }

    // ----------------- HTTP flow pins -----------------

    [Fact]
    public async Task ScheduleByIdAsync_201Created_ReturnsSuccessWithBodyJson()
    {
        var responseBody = """{"id":"77777777-7777-7777-7777-777777777777","status":1}""";
        var (client, _) = BuildClient(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
        });

        var result = await client.ScheduleByIdAsync(SampleDefinitionId, initialInputJson: null);

        result.Success.Should().BeTrue();
        result.ResponseBodyJson.Should().Be(responseBody,
            "qwen's body bytes pass through verbatim into ResponseBodyJson; the LLM sees the canonical Created shape");
        result.ErrorCode.Should().BeNull();
    }

    [Fact]
    public async Task ScheduleByIdAsync_401_MapsToAuthFailed()
    {
        var (client, _) = BuildClient(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await client.ScheduleByIdAsync(SampleDefinitionId, null);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(WorkflowScheduleErrorCode.AuthFailed);
        result.ErrorMessage.Should().Contain("rejected credentials");
    }

    [Fact]
    public async Task ScheduleByIdAsync_404_MapsToDefinitionNotFound_WithIdInMessage()
    {
        var (client, _) = BuildClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await client.ScheduleByIdAsync(SampleDefinitionId, null);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(WorkflowScheduleErrorCode.DefinitionNotFound);
        result.ErrorMessage.Should().Contain(SampleDefinitionId.ToString("D"),
            "404 message echoes the requested id so operators + the LLM know which definition was missing");
    }

    [Fact]
    public async Task ScheduleByIdAsync_5xx_MapsToTransient_WithProblemDetailsDetail()
    {
        var (client, _) = BuildClient(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(
                """{"type":"about:blank","title":"Internal","status":500,"detail":"hangfire enqueue failed"}""",
                Encoding.UTF8,
                "application/problem+json"),
        });

        var result = await client.ScheduleByIdAsync(SampleDefinitionId, null);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(WorkflowScheduleErrorCode.Transient);
        result.ErrorMessage.Should().Contain("hangfire enqueue failed");
    }

    [Fact]
    public async Task ScheduleByIdAsync_OtherFourXx_MapsToBadRequest_WithDetail()
    {
        var (client, _) = BuildClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                """{"detail":"WorkflowDefinitionJson schema invalid"}""",
                Encoding.UTF8,
                "application/problem+json"),
        });

        var result = await client.ScheduleByIdAsync(SampleDefinitionId, null);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(WorkflowScheduleErrorCode.BadRequest);
        result.ErrorMessage.Should().Contain("WorkflowDefinitionJson schema invalid");
    }

    [Fact]
    public async Task ScheduleByIdAsync_TransportError_MapsToTransient()
    {
        var (client, _) = BuildClient(_ => throw new HttpRequestException("connection refused"));

        var result = await client.ScheduleByIdAsync(SampleDefinitionId, null);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(WorkflowScheduleErrorCode.Transient);
        result.ErrorMessage.Should().Contain("transport error");
    }

    [Fact]
    public async Task ScheduleByIdAsync_Timeout_MapsToTransient()
    {
        var (client, _) = BuildClient(_ => throw new TaskCanceledException("timed out"));

        var result = await client.ScheduleByIdAsync(SampleDefinitionId, null);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(WorkflowScheduleErrorCode.Transient);
        result.ErrorMessage.Should().Contain("timed out");
    }

    [Fact]
    public async Task ScheduleByIdAsync_CallerCancellation_RethrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var (client, _) = BuildClient(_ => throw new TaskCanceledException("cancelled"));

        var act = () => client.ScheduleByIdAsync(SampleDefinitionId, null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "caller cancellation must propagate so the executor's OCE filter handles it upstack");
    }

    [Fact]
    public async Task ScheduleByIdAsync_EmptyDefinitionId_ShortCircuitsBeforeAnyHttpCall()
    {
        var (client, handler) = BuildClient(_ => throw new InvalidOperationException("should not reach"));

        var result = await client.ScheduleByIdAsync(Guid.Empty, null);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(WorkflowScheduleErrorCode.BadRequest);
        handler.CapturedRequests.Should().BeEmpty(
            "empty Guid fails the pre-flight check — no HTTP call goes out");
    }

    [Fact]
    public async Task ScheduleByIdAsync_HttpContextWithBearer_PropagatesAuthorizationHeader()
    {
        // Build an HttpContextAccessor with a synthetic HttpContext that
        // carries an inbound Authorization header — pin propagation.
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = "Bearer test-token-abc";
        var accessor = new HttpContextAccessor { HttpContext = ctx };

        HttpRequestMessage? captured = null;
        var (client, _) = BuildClient(
            respond: req => { captured = req; return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}") }; },
            accessor: accessor);

        await client.ScheduleByIdAsync(SampleDefinitionId, null);

        captured.Should().NotBeNull();
        captured!.Headers.Authorization.Should().NotBeNull(
            "the inbound user JWT propagates to the outbound qwen call so workflow's TenantClaimsMiddleware sees the same tenant");
        captured.Headers.Authorization!.Scheme.Should().Be("Bearer");
        captured.Headers.Authorization.Parameter.Should().Be("test-token-abc");
    }

    [Fact]
    public async Task ScheduleByIdAsync_NoHttpContext_OmitsAuthorizationHeader()
    {
        // No HttpContext (background-service / test scenario). The client
        // attaches no Authorization header; qwen will 401 in production
        // but the test handler ignores headers.
        var accessor = new HttpContextAccessor { HttpContext = null };
        HttpRequestMessage? captured = null;
        var (client, _) = BuildClient(
            respond: req => { captured = req; return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}") }; },
            accessor: accessor);

        await client.ScheduleByIdAsync(SampleDefinitionId, null);

        captured.Should().NotBeNull();
        captured!.Headers.Authorization.Should().BeNull(
            "no inbound HttpContext → no header to copy; the client doesn't fabricate auth");
    }

    // ----------------- Helpers -----------------

    private static (HttpWorkflowClient client, RecordingHandler handler) BuildClient(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        IHttpContextAccessor? accessor = null)
    {
        var handler = new RecordingHandler(respond);
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:5118/"),
            Timeout = TimeSpan.FromSeconds(5),
        };
        var effectiveAccessor = accessor ?? new HttpContextAccessor { HttpContext = null };
        var client = new HttpWorkflowClient(http, effectiveAccessor, NullLogger<HttpWorkflowClient>.Instance);
        return (client, handler);
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

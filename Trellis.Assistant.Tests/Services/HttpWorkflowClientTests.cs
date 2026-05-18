using System.Net;
using System.Security.Claims;
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
/// (JSON envelope shape) and the
/// <see cref="HttpWorkflowClient.ScheduleByIdAsync"/> HTTP flow exercised
/// via a recording <see cref="DelegatingHandler"/>.
///
/// <para>
/// Phase 3.I fix-up: the AUTH contract changed. Outbound request
/// carries a minted service token (Authorization: Bearer
/// &lt;internal-token&gt;) plus the user's tenant_id forwarded as
/// <c>X-Trellis-Tenant-Id</c>. The prior verbatim-forwarding shape
/// 401'd in production (user JWT carried aud=trellis-assistant).
/// Test setup wires a <see cref="StubInternalTokenIssuer"/> +
/// a synthetic HttpContext carrying a <c>tenant_id</c> claim.
/// </para>
/// </summary>
public sealed class HttpWorkflowClientTests
{
    private static readonly Guid SampleDefinitionId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private const string SampleTenantId = "44444444-4444-4444-4444-444444444444";
    private const string StubInternalToken = "stub-internal-cc-token";

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
        var (client, _, _) = BuildClient(_ => new HttpResponseMessage(HttpStatusCode.Created)
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
    public async Task ScheduleByIdAsync_201Created_OutboundCarriesServiceTokenAndTenantHeader()
    {
        // Phase 3.I fix-up: the outbound carries the MINTED service
        // token (NOT the verbatim user JWT) + the X-Trellis-Tenant-Id
        // header derived from the inbound user JWT's tenant_id claim.
        var (client, handler, _) = BuildClient(_ =>
            new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}") });

        await client.ScheduleByIdAsync(SampleDefinitionId, null);

        var captured = handler.CapturedRequests.Single();
        captured.Headers.Authorization.Should().NotBeNull();
        captured.Headers.Authorization!.Scheme.Should().Be("Bearer");
        captured.Headers.Authorization.Parameter.Should().Be(StubInternalToken,
            "outbound Authorization carries the MINTED service token (aud=trellis-workflow), NOT the inbound user JWT");

        captured.Headers.TryGetValues(HttpWorkflowClient.TenantIdHeader, out var tenantValues).Should().BeTrue(
            "user tenant identity is forwarded out-of-band as X-Trellis-Tenant-Id");
        tenantValues!.Single().Should().Be(SampleTenantId);
    }

    [Fact]
    public async Task ScheduleByIdAsync_401_MapsToAuthFailed()
    {
        var (client, _, _) = BuildClient(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await client.ScheduleByIdAsync(SampleDefinitionId, null);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(WorkflowScheduleErrorCode.AuthFailed);
        result.ErrorMessage.Should().Contain("rejected credentials");
    }

    [Fact]
    public async Task ScheduleByIdAsync_404_MapsToDefinitionNotFound_WithIdInMessage()
    {
        var (client, _, _) = BuildClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await client.ScheduleByIdAsync(SampleDefinitionId, null);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(WorkflowScheduleErrorCode.DefinitionNotFound);
        result.ErrorMessage.Should().Contain(SampleDefinitionId.ToString("D"),
            "404 message echoes the requested id so operators + the LLM know which definition was missing");
    }

    [Fact]
    public async Task ScheduleByIdAsync_5xx_MapsToTransient_WithProblemDetailsDetail()
    {
        var (client, _, _) = BuildClient(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
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
        var (client, _, _) = BuildClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
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
        var (client, _, _) = BuildClient(_ => throw new HttpRequestException("connection refused"));

        var result = await client.ScheduleByIdAsync(SampleDefinitionId, null);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(WorkflowScheduleErrorCode.Transient);
        result.ErrorMessage.Should().Contain("transport error");
    }

    [Fact]
    public async Task ScheduleByIdAsync_Timeout_MapsToTransient()
    {
        var (client, _, _) = BuildClient(_ => throw new TaskCanceledException("timed out"));

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
        var (client, _, _) = BuildClient(_ => throw new TaskCanceledException("cancelled"));

        var act = () => client.ScheduleByIdAsync(SampleDefinitionId, null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "caller cancellation must propagate so the executor's OCE filter handles it upstack");
    }

    [Fact]
    public async Task ScheduleByIdAsync_EmptyDefinitionId_ShortCircuitsBeforeAnyHttpCall()
    {
        var (client, handler, _) = BuildClient(_ => throw new InvalidOperationException("should not reach"));

        var result = await client.ScheduleByIdAsync(Guid.Empty, null);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(WorkflowScheduleErrorCode.BadRequest);
        handler.CapturedRequests.Should().BeEmpty(
            "empty Guid fails the pre-flight check — no HTTP call goes out");
    }

    [Fact]
    public async Task ScheduleByIdAsync_NoHttpContext_ReturnsAuthFailed_WithoutHttpCall()
    {
        // Phase 3.I fix-up: workflow_schedule requires an inbound JWT
        // for tenant_id forwarding. Without HttpContext, the tool can't
        // identify the tenant — fail fast with AuthFailed, no HTTP call.
        var accessor = new HttpContextAccessor { HttpContext = null };
        var (client, handler, _) = BuildClient(
            respond: _ => throw new InvalidOperationException("should not reach"),
            accessor: accessor);

        var result = await client.ScheduleByIdAsync(SampleDefinitionId, null);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(WorkflowScheduleErrorCode.AuthFailed);
        result.ErrorMessage.Should().Contain("HttpContext",
            "operator-visible message identifies the missing context");
        handler.CapturedRequests.Should().BeEmpty(
            "no tenant_id available → fail at the Assistant boundary, never reach qwen");
    }

    [Fact]
    public async Task ScheduleByIdAsync_HttpContextMissingTenantClaim_ReturnsAuthFailed_WithoutHttpCall()
    {
        // Inbound JWT had no tenant_id claim → can't forward what we
        // don't have. Same fail-fast posture.
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("sub", "user-a"),
            }, "TestAuth")),
        };
        var accessor = new HttpContextAccessor { HttpContext = ctx };
        var (client, handler, _) = BuildClient(
            respond: _ => throw new InvalidOperationException("should not reach"),
            accessor: accessor);

        var result = await client.ScheduleByIdAsync(SampleDefinitionId, null);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(WorkflowScheduleErrorCode.AuthFailed);
        result.ErrorMessage.Should().Contain("tenant_id");
        handler.CapturedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ScheduleByIdAsync_TokenIssuerThrows_ReturnsAuthFailed_WithoutHttpCall()
    {
        // Phase 3.I fix-up: Auth service unreachable / 5xx / rejected
        // credentials all surface from IInternalTokenIssuer as
        // InternalTokenIssuanceException. HttpWorkflowClient catches
        // and returns AuthFailed — operator-actionable, NOT a transient
        // the LLM should retry-loop against.
        var (client, handler, _) = BuildClient(
            respond: _ => throw new InvalidOperationException("should not reach"),
            issuerOverride: new ThrowingInternalTokenIssuer());

        var result = await client.ScheduleByIdAsync(SampleDefinitionId, null);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(WorkflowScheduleErrorCode.AuthFailed);
        result.ErrorMessage.Should().Contain("authenticate to Workflow service");
        handler.CapturedRequests.Should().BeEmpty(
            "token issuance failed → no outbound to qwen");
    }

    // ----------------- Helpers -----------------

    private static (HttpWorkflowClient client, RecordingHandler handler, StubInternalTokenIssuer issuer) BuildClient(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        IHttpContextAccessor? accessor = null,
        IInternalTokenIssuer? issuerOverride = null)
    {
        var handler = new RecordingHandler(respond);
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:5118/"),
            Timeout = TimeSpan.FromSeconds(5),
        };
        var effectiveAccessor = accessor ?? BuildHttpContextAccessorWithTenant(SampleTenantId);
        var stubIssuer = new StubInternalTokenIssuer(StubInternalToken);
        var effectiveIssuer = issuerOverride ?? stubIssuer;
        var client = new HttpWorkflowClient(
            http, effectiveAccessor, effectiveIssuer, NullLogger<HttpWorkflowClient>.Instance);
        return (client, handler, stubIssuer);
    }

    /// <summary>
    /// Build an IHttpContextAccessor with a synthetic User that has a
    /// tenant_id claim. Default for tests so the JWT-driven tenant
    /// path is satisfied unless overridden.
    /// </summary>
    private static IHttpContextAccessor BuildHttpContextAccessorWithTenant(string tenantId)
    {
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("tenant_id", tenantId),
                new Claim("sub", "user-a"),
            }, "TestAuth")),
        };
        return new HttpContextAccessor { HttpContext = ctx };
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

    private sealed class StubInternalTokenIssuer : IInternalTokenIssuer
    {
        private readonly string _token;
        public int CallCount { get; private set; }
        public StubInternalTokenIssuer(string token) { _token = token; }
        public Task<string> GetAccessTokenAsync(string targetResource, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(_token);
        }
    }

    private sealed class ThrowingInternalTokenIssuer : IInternalTokenIssuer
    {
        public Task<string> GetAccessTokenAsync(string targetResource, CancellationToken ct)
            => throw new InternalTokenIssuanceException("auth service unreachable");
    }
}

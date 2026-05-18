using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Trellis.Assistant.AgentExecution;
using Trellis.Assistant.Services;
using Trellis.Core.Services;

namespace Trellis.Assistant.Tests.TestFixtures;

/// <summary>
/// Custom <see cref="WebApplicationFactory{TEntryPoint}"/> for the Phase
/// 1+2 endpoint integration tests. Injects the
/// <see cref="PostgresFixture"/>'s connection string + leaves
/// <c>Assistant:AutoMigrate=true</c> so the host applies the EF
/// migration on first request — matches QA's deploy-time auto-migrate
/// posture.
///
/// LLM swap (Phase 2): production Program.cs registers
/// <see cref="OllamaClient"/> via <c>AddHttpClient&lt;IOllamaClient&gt;().AddTypedClient</c>;
/// this factory REPLACES that registration with
/// <see cref="StubLlmClient"/> so the endpoint tests verify
/// orchestrator/store/lock invariants WITHOUT depending on a reachable
/// Ollama server. The X1-confirmed test split: stub-driven tests verify
/// the orchestrator/store/lock surface; the real-Ollama integration
/// smoke (separate test class, gated on <c>OLLAMA_BASE_URL</c> env var)
/// verifies the real-LLM path.
///
/// Per-test-class fixture pattern: each test class that holds a
/// PostgresFixture gets its own AssistantWebApplicationFactory bound
/// to the same container's connection string. The container is reused
/// across [Fact]s in the class but not across classes — keeps cross-
/// test-class state isolation cheap.
/// </summary>
public sealed class AssistantWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly StubAgentLlmClient _agentLlmStub = new();
    private readonly StubSearchClient _searchClientStub = new();
    private readonly StubWorkflowClient _workflowClientStub = new();
    private readonly StubInternalTokenIssuer _internalTokenIssuerStub = new();

    /// <summary>
    /// Test-controlled stub for the agent-execution LLM. Tests use the
    /// public <see cref="StubAgentLlmClient.EnqueueAssistantText"/> /
    /// <see cref="StubAgentLlmClient.EnqueueToolCall"/> helpers to script
    /// the LLM's response sequence per test, then assert on
    /// <see cref="StubAgentLlmClient.Calls"/>.
    /// </summary>
    public StubAgentLlmClient AgentLlmStub => _agentLlmStub;

    /// <summary>
    /// PR #10 review Blocker 3: test-controlled stub for the Trainer
    /// search HTTP client. Tests set <see cref="StubSearchClient.NextResult"/>
    /// before driving the endpoint; the executor dispatches
    /// SearchDocumentsTool → this stub → canned response. Pins the full
    /// LLM-emits-tool_call → executor-dispatches → ISearchClient-returns
    /// chain that PR #10's original E2E test missed.
    /// </summary>
    public StubSearchClient SearchClientStub => _searchClientStub;

    /// <summary>
    /// Phase 3.I fix-up: test-controlled stub for the workflow schedule
    /// client. Tests set <see cref="StubWorkflowClient.NextResult"/>
    /// before driving the endpoint; the executor dispatches
    /// WorkflowScheduleTool → this stub → canned response. Mirrors the
    /// <see cref="SearchClientStub"/> pattern.
    /// </summary>
    public StubWorkflowClient WorkflowClientStub => _workflowClientStub;

    /// <summary>
    /// Phase 3.I fix-up: stub for the client-credentials token issuer.
    /// Returns a fixed token; the workflow client stub above bypasses
    /// the actual network path so the token is never sent.
    /// </summary>
    public StubInternalTokenIssuer InternalTokenIssuerStub => _internalTokenIssuerStub;

    public AssistantWebApplicationFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = _connectionString,
                // AutoMigrate stays true so the EF migration applies on
                // first request (or first DI scope creation). The
                // PostgresFixture starts a fresh container per test
                // class; auto-migrate is the cheapest way to land the
                // schema before the first endpoint call.
                ["Assistant:AutoMigrate"] = "true",
                // Disable the warm-up service — the stub LLM doesn't
                // need warming, and the production warm-up would fire
                // outbound HTTP at the dummy Ollama:BaseUrl below and
                // pollute test output with retry warnings.
                ["Assistant:WarmupModel"] = "",
                // Phase-1-compatible turn timeout. The stub yields
                // ~200ms; 60s is a 300x margin and matches Phase 1's
                // hardcoded value, which the existing tests were
                // implicitly written against.
                ["Assistant:TurnRequestTimeoutSeconds"] = "60",
                // Dummy Ollama:BaseUrl — IOllamaClient is overridden
                // below to a stub, but the AddHttpClient registration
                // in Program.cs still resolves at startup. A non-null
                // value keeps that resolution clean.
                ["Ollama:BaseUrl"] = "http://test-host-unreachable:11434/",
                // Macro 3 PR 2: dummy Auth:Authority for the JWT
                // bearer registration. Tests don't actually validate
                // bearers — TestAuthenticationHandler synthesizes
                // claims from headers (see ConfigureTestServices below).
                // A non-null Authority keeps Program.cs's
                // AddJwtBearer registration from throwing at startup.
                ["Auth:Authority"] = "http://test-host-unreachable/",
                ["Auth:Audience"] = "trellis-assistant-test",
                ["Auth:RequireHttpsMetadata"] = "false",
                // Phase 3.B: SearchDocumentsTool is registered in the
                // production DI graph and its typed HttpClient resolves
                // at startup. A dummy BaseUrl keeps that resolution
                // clean — endpoint tests don't actually round-trip to
                // Trainer (no integration test crosses that boundary
                // yet; HttpSearchClientTests covers the client in
                // isolation with a stub handler).
                ["Assistant:Trainer:BaseUrl"] = "http://test-host-unreachable:5114/",
                ["Assistant:Trainer:RequestTimeoutSeconds"] = "30",
                // Phase 3.I fix-up: workflow client + internal token
                // issuer config. The stubs below bypass the network so
                // these values just need to keep startup resolution
                // clean. ExposeWorkflowSchedule=true so the LLM sees
                // the tool in the integration tests; production default
                // is false.
                ["Assistant:Workflow:BaseUrl"] = "http://test-host-unreachable:5118/",
                ["Assistant:Workflow:RequestTimeoutSeconds"] = "10",
                ["Assistant:Auth:InternalClient:TokenEndpoint"] = "http://test-host-unreachable:5119/connect/token",
                ["Assistant:Auth:InternalClient:ClientId"] = "trellis-assistant-internal-test",
                ["Assistant:Auth:InternalClient:ClientSecret"] = "test-secret",
                ["Assistant:Auth:InternalClient:RequestTimeoutSeconds"] = "5",
                ["Assistant:Auth:InternalClient:RefreshSkewSeconds"] = "60",
                ["Assistant:Tools:ExposeWorkflowSchedule"] = "true",
                // Phase 3.C: expose EchoTool in test environments so
                // existing AssistantAgentExecutor + ConversationEndpoint
                // tests that exercise the echo dispatch path still see
                // it in the LLM-visible catalogue. Production default is
                // false (echo is a debug aid; LLM shouldn't be tempted
                // to call it in real conversations). SearchDocumentsTool
                // is exposed unconditionally; the test stub
                // ISearchClient is the boundary that prevents real
                // Trainer round-trips.
                ["Assistant:Tools:ExposeEcho"] = "true",
            });
        });

        // Replace the production OllamaClient + OllamaAgentLlmClient
        // registrations with the stubs. ConfigureTestServices runs AFTER
        // the production ConfigureServices, so the last registration
        // wins. The X1 split: stub-driven tests verify orchestrator/
        // store/lock/executor/registry/gate invariants; OLLAMA_BASE_URL-
        // gated tests (RealOllamaSmokeTests) verify the real-LLM paths.
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IOllamaClient>();
            services.AddSingleton<IOllamaClient, StubLlmClient>();

            // Phase 3.A.1: parallel agent-execution LLM client. Same
            // RemoveAll-then-AddSingleton pattern as IOllamaClient.
            services.RemoveAll<IAgentLlmClient>();
            services.AddSingleton<IAgentLlmClient>(_agentLlmStub);

            // PR #10 review Blocker 3: replace the production
            // HttpSearchClient with the test stub so SearchDocumentsTool
            // dispatches resolve here instead of attempting an HTTP
            // round-trip to the dummy Assistant:Trainer:BaseUrl. The
            // Func<ISearchClient> registration also needs replacement —
            // SearchDocumentsTool resolves the factory per-call (captive-
            // dep fix from PR #9 review), and the production registration
            // pulls from the typed-HttpClient ISearchClient binding which
            // we just removed.
            services.RemoveAll<ISearchClient>();
            services.AddSingleton<ISearchClient>(_searchClientStub);
            services.RemoveAll<Func<ISearchClient>>();
            services.AddSingleton<Func<ISearchClient>>(
                sp => () => sp.GetRequiredService<ISearchClient>());

            // Phase 3.I fix-up: replace the production HttpWorkflowClient
            // + IInternalTokenIssuer with stubs so the WorkflowScheduleTool
            // dispatch path resolves in-process. Same shape as the search
            // client replacement above.
            services.RemoveAll<IWorkflowClient>();
            services.AddSingleton<IWorkflowClient>(_workflowClientStub);
            services.RemoveAll<Func<IWorkflowClient>>();
            services.AddSingleton<Func<IWorkflowClient>>(
                sp => () => sp.GetRequiredService<IWorkflowClient>());
            services.RemoveAll<IInternalTokenIssuer>();
            services.AddSingleton<IInternalTokenIssuer>(_internalTokenIssuerStub);

            // Macro 3 PR 2: replace JWT bearer with the test auth
            // scheme that synthesizes claims from headers. Existing
            // tests' header-based fixture pattern continues working;
            // production validates real bearers, tests skip the JWT
            // path entirely. The TestAuth scheme is also the new
            // default scheme — RequireAuthorization on the route
            // groups validates against it.
            services.Configure<AuthenticationOptions>(opts =>
            {
                opts.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                opts.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
            });
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    TestAuthenticationHandler.SchemeName, _ => { });
        });
    }
}

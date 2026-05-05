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

    /// <summary>
    /// Test-controlled stub for the agent-execution LLM. Tests use the
    /// public <see cref="StubAgentLlmClient.EnqueueAssistantText"/> /
    /// <see cref="StubAgentLlmClient.EnqueueToolCall"/> helpers to script
    /// the LLM's response sequence per test, then assert on
    /// <see cref="StubAgentLlmClient.Calls"/>.
    /// </summary>
    public StubAgentLlmClient AgentLlmStub => _agentLlmStub;

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
        });
    }
}

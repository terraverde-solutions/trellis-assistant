using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Trellis.Assistant.Tests.TestFixtures;

/// <summary>
/// Phase 2 real-Ollama integration smoke factory. Spins up the host
/// against a Testcontainers Postgres + a CALLER-PROVIDED Ollama base
/// URL — typically a developer's local Ollama or an
/// OLLAMA_BASE_URL-pointed-at-CI-runner instance.
///
/// Distinct from <see cref="AssistantWebApplicationFactory"/>: this
/// factory does NOT override <c>IOllamaClient</c> with the stub. The
/// production <c>OllamaClient</c> from
/// <see cref="Trellis.Core.Services.OllamaClient"/> wires up against
/// the real Ollama; tests that use this factory exercise the full
/// real-LLM path.
///
/// X1 split: the stub-driven endpoint tests
/// (<see cref="Integration.ConversationEndpointTests"/>) verify
/// orchestrator/store/lock invariants without an Ollama dependency.
/// This factory drives the SEPARATE real-Ollama smoke
/// (<see cref="Integration.RealOllamaSmokeTests"/>) gated on
/// <c>OLLAMA_BASE_URL</c>. Two test surfaces, two failure modes; do
/// not merge them — 5-parallel-against-real-Ollama would spend
/// ~5 minutes serializing on a single GPU.
///
/// Warm-up disabled: the test itself is the cold-load. Disabling
/// warm-up keeps the test self-contained + makes the
/// 240s upper bound below the dominant runtime cost.
/// </summary>
public sealed class RealOllamaWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly string _ollamaBaseUrl;

    public RealOllamaWebApplicationFactory(string connectionString, string ollamaBaseUrl)
    {
        _connectionString = connectionString;
        _ollamaBaseUrl = ollamaBaseUrl;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = _connectionString,
                ["Assistant:AutoMigrate"] = "true",
                // Disable warm-up; the test itself drives the cold-load.
                ["Assistant:WarmupModel"] = "",
                // Phase 2 default — generous enough for cold 70B + token-
                // heavy responses without bricking the test loop on a
                // genuinely-stuck Ollama.
                ["Assistant:TurnRequestTimeoutSeconds"] = "180",
                ["Ollama:BaseUrl"] = _ollamaBaseUrl,
            });
        });

        // No ConfigureTestServices override — production OllamaClient
        // stays. That's the point of this factory.
    }
}

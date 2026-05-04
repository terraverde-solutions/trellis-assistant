using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Trellis.Assistant.Tests.TestFixtures;

/// <summary>
/// Custom <see cref="WebApplicationFactory{TEntryPoint}"/> for the
/// Phase 1 endpoint integration tests. Injects the
/// <see cref="PostgresFixture"/>'s connection string + leaves
/// <c>Assistant:AutoMigrate=true</c> so the host applies the EF
/// migration on first request — matches QA's deploy-time auto-migrate
/// posture.
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
                // Stub LLM doesn't read the model tag; pin a sentinel
                // so a future regression that sneaks a real LLM call
                // onto the test path fails loud.
                ["Assistant:Model"] = "test-stub-model",
            });
        });
    }
}

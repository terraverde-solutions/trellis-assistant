using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Trellis.Assistant.Tests;

// Integration test against the actual ASP.NET Core pipeline. Spins up the
// app in-process via WebApplicationFactory<Program> and asserts that the
// /healthz endpoint declared in Program.cs returns 200 + the canonical
// `{"status":"ok"}` body.
//
// Phase 1 update: Program.cs now requires a Postgres connection string
// at startup (auto-migrate is true by default). /healthz itself doesn't
// touch the DbContext, so the connection string is supplied as a stub +
// AutoMigrate is disabled — the host wires up DI without ever opening a
// connection. A future change that makes /healthz actually probe Postgres
// would need this test to swap to the Testcontainers-backed
// AssistantWebApplicationFactory; today's contract is liveness-only +
// the test stays cheap.
//
// The /healthz contract IS load-bearing for the deploy-script smoke
// wrapper (Deploy-Assistant-Standalone.ps1 in trellis-deploy hits this
// endpoint via curl over the SSH session and exits non-zero if it
// doesn't get 200).
public class HealthCheckTests : IClassFixture<HealthCheckTests.LightweightFactory>
{
    private readonly LightweightFactory _factory;

    public HealthCheckTests(LightweightFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Get_Healthz_Returns200()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/healthz");

        response.IsSuccessStatusCode.Should().BeTrue(
            $"GET /healthz should return 2xx but returned {(int)response.StatusCode} {response.StatusCode}");
        ((int)response.StatusCode).Should().Be(200,
            "the deploy-script smoke wrapper expects exactly 200 for the active(running)+responsive contract");
    }

    [Fact]
    public async Task Get_Healthz_BodyHasStatusOk()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/healthz");
        response.IsSuccessStatusCode.Should().BeTrue();

        var body = await response.Content.ReadFromJsonAsync<HealthBody>();
        body.Should().NotBeNull();
        body!.Status.Should().Be("ok",
            "Phase 0+1's /healthz returns the simplest possible liveness shape; richer health blocks live in Phase 2+");
    }

    private sealed record HealthBody(string Status);

    /// <summary>
    /// /healthz-only factory. Stubs the connection string so DI wires up
    /// without ever opening a Postgres connection (AutoMigrate=false +
    /// /healthz doesn't touch the DbContext). Avoids the Docker
    /// dependency that the Testcontainers-backed
    /// <c>AssistantWebApplicationFactory</c> carries.
    /// </summary>
    public sealed class LightweightFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Dummy connection string — DI registration succeeds;
                    // /healthz never opens a connection so the unreachable
                    // host doesn't matter.
                    ["ConnectionStrings:Postgres"] = "Host=test-host-unreachable;Database=test_only_unused;Username=test;Password=test",
                    // Skip auto-migrate — would block startup trying to
                    // reach the unreachable host.
                    ["Assistant:AutoMigrate"] = "false",
                });
            });
        }
    }
}

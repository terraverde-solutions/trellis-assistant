using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Trellis.Assistant.Tests;

// Integration test against the actual ASP.NET Core pipeline. Spins up the
// app in-process via WebApplicationFactory<Program> and asserts that the
// /healthz endpoint declared in Program.cs returns 200 + the canonical
// `{"status":"ok"}` body.
//
// This is the only behavioural test Phase 0 ships — orchestrator + channel
// adapters + tool dispatch + voice are deferred to Phase 1+ and will get
// their own tests when they land. The /healthz contract IS load-bearing
// for the deploy-script smoke wrapper (Deploy-Assistant-Standalone.ps1
// in trellis-deploy hits exactly this endpoint via curl over the SSH
// session and exits non-zero if it doesn't get 200).
public class HealthCheckTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthCheckTests(WebApplicationFactory<Program> factory)
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

        // Use the shared JSON deserializer so a future schema add (a
        // version field, an uptime field) doesn't false-fail this test
        // — we only assert the canonical `status` field, additive
        // changes are fine.
        var response = await client.GetAsync("/healthz");
        response.IsSuccessStatusCode.Should().BeTrue();

        var body = await response.Content.ReadFromJsonAsync<HealthBody>();
        body.Should().NotBeNull();
        body!.Status.Should().Be("ok",
            "Phase 0's /healthz returns the simplest possible liveness shape; richer health blocks live in Phase 1+");
    }

    private sealed record HealthBody(string Status);
}

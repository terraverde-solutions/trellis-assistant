using System.Text.Json;
using FluentAssertions;

namespace Trellis.Assistant.Tests;

// Pin the configuration-source convention that surfaced as a bootstrap-day
// gotcha during the trainer-qa GB10 deploy on 2026-05-02:
//
//   appsettings.json must NOT pin a top-level "Urls" key. That's a
//   dev-side default that beats ASPNETCORE_URLS in deployable
//   scenarios — port choice belongs in launchSettings.json (Dev) or
//   systemd's Environment= directives (QA / Production).
//
// Phase 0 carries this contract from day one so a future "convenience"
// commit adding `"Urls": "http://localhost:5117"` doesn't shadow the
// trellis-assistant-qa.service unit's --urls + ASPNETCORE_URLS pinning.
public sealed class AppsettingsConventionsTests
{
    [Fact]
    public void Appsettings_DoesNotPin_UrlsKey()
    {
        var appsettingsPath = Path.Combine(
            ResolveRepoRoot(), "Trellis.Assistant", "appsettings.json");
        File.Exists(appsettingsPath).Should().BeTrue(
            $"appsettings.json must be discoverable at {appsettingsPath} for this test to mean anything");

        using var doc = JsonDocument.Parse(File.ReadAllText(appsettingsPath));
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object,
            "appsettings.json must be a JSON object at the root");

        // Case-insensitive check — ASP.NET Core's IConfiguration treats
        // keys case-insensitively, so a "URLS" or "urls" leak would also
        // bind. Catch all spellings.
        var topLevelKeys = doc.RootElement
            .EnumerateObject()
            .Select(p => p.Name)
            .ToList();
        topLevelKeys.Should().NotContain(
            k => string.Equals(k, "Urls", StringComparison.OrdinalIgnoreCase),
            "appsettings.json MUST NOT pin a 'Urls' key. Move dev-port choice to " +
            "Properties/launchSettings.json or the systemd unit's Environment=ASPNETCORE_URLS " +
            "(plus --urls on ExecStart for defense in depth). A committed Urls key here was " +
            "the original trainer-qa bootstrap-day bug — it shadowed the QA unit's " +
            "ASPNETCORE_URLS=http://127.0.0.1:5114 and the trainer bound the wrong port until " +
            "the unit was live-edited to add an --urls CLI flag.");
    }

    private static string ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var solutionMatch = dir.GetFiles("Trellis.Assistant.sln").Length > 0;
            var dirMatch =
                Directory.Exists(Path.Combine(dir.FullName, "Trellis.Assistant"))
                && Directory.Exists(Path.Combine(dir.FullName, "Trellis.Assistant.Tests"));
            if (solutionMatch || dirMatch)
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate the assistant repo root from " + AppContext.BaseDirectory);
    }
}

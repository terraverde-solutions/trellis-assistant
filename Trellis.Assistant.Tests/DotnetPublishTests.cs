using System.Diagnostics;
using FluentAssertions;

namespace Trellis.Assistant.Tests;

// Pin the canonical no-leak contract for `appsettings.user.json`:
// the file MUST NEVER ride into a `dotnet publish` output bundle.
//
// Why this needs a publish-output test instead of an XML-shape test:
//
// The trainer-qa GB10 bootstrap on 2026-05-02 leaked a developer's
// local Postgres connection string onto the deployed host because
// `dotnet publish` copied `appsettings.user.json` into the bundle.
// The first attempt at a fix (trellis-trainer PR #41 original) used
// `<None Update="appsettings.user.json"><CopyToPublishDirectory>Never
// </...></None>`, plus an XML-parse test that asserted the rule was
// present in the csproj. The XML test false-greened because
// `Microsoft.NET.Sdk.Web`'s implicit auto-include for
// `appsettings*.json` classifies the file as `<Content>`, not
// `<None>` — so the `<None Update>` rule targeted an item-type the
// file wasn't in, silently no-op'd, and the publish bundle still
// shipped the file.
//
// The corrected csproj rule uses `<Content Remove>` + `<None Remove>`
// (defense in depth: covers either item-type the SDK may classify the
// file as). This test EMPIRICALLY exercises the rule by:
//   1. Writing a sentinel `appsettings.user.json` to the project source.
//   2. Invoking `dotnet publish` against the real Trellis.Assistant.csproj.
//   3. Asserting the sentinel file is ABSENT from the publish output.
//   4. Sanity-asserting the production `appsettings.json` IS present
//      (so the test catches the over-broad `appsettings*.json` kill).
//
// Phase 0 carries this contract from day one so the customer-side +
// QA-side install paths (Deploy-Assistant-Standalone.ps1) cannot leak
// operator-local configuration into the deployed bundle.
//
// Tagged [Trait("Category", "Integration")] so fast-test runs filter
// it out via `--filter "Category!=Integration"` and CI integration
// runs filter via `--filter "Category=Integration"`. The publish step
// shells out to `dotnet publish` and adds ~2-30 s depending on whether
// dependencies are warm.
//
// This test class is sealed + has only one Fact so xUnit's default
// "tests in the same class run sequentially" guarantee covers our
// in-source-tree file mutation. Other test classes in this project
// don't touch `appsettings.user.json`, so cross-class parallelism is
// safe. The test backs up any pre-existing file in the project
// source and restores it in `finally` — a developer with a real
// local override file won't lose it.
public sealed class DotnetPublishTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void DotnetPublish_DoesNotCopy_AppsettingsUserJson_IntoPublishOutput()
    {
        var repoRoot = ResolveRepoRoot();
        var projectDir = Path.Combine(repoRoot, "Trellis.Assistant");
        var csprojPath = Path.Combine(projectDir, "Trellis.Assistant.csproj");
        File.Exists(csprojPath).Should().BeTrue(
            $"the csproj must be discoverable at {csprojPath} for this test to mean anything");

        var userJsonPath = Path.Combine(projectDir, "appsettings.user.json");
        var backupPath = userJsonPath + $".test-backup-{Guid.NewGuid():N}";
        var publishDir = Path.Combine(
            Path.GetTempPath(),
            $"trellis-assistant-publish-test-{Guid.NewGuid():N}");

        // If the developer has a real local appsettings.user.json,
        // back it up so the test doesn't clobber. The file is
        // gitignored so its contents are operator-local property.
        var hadExistingFile = File.Exists(userJsonPath);
        if (hadExistingFile)
        {
            File.Move(userJsonPath, backupPath);
        }

        try
        {
            // Sentinel content. The marker string must be unique enough
            // that we can distinguish "the file content was preserved"
            // from "the file slot exists but is empty / unrelated."
            const string leakMarker = "TEST_MARKER_MUST_NOT_LEAK_INTO_PUBLISH_OUTPUT";
            const string fileBody = $$"""
                {
                  "Marker": "{{leakMarker}}",
                  "Gateway": {
                    "ApiKey": "fake-local-dev-key-pinning-the-no-leak-contract"
                  }
                }
                """;
            File.WriteAllText(userJsonPath, fileBody);

            // Invoke `dotnet publish`. --no-self-contained matches the
            // QA deploy script (Deploy-Assistant-Standalone.ps1);
            // --nologo trims output to keep test logs readable; -o
            // forces a known scratch path so we don't pollute bin/Release.
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                ArgumentList =
                {
                    "publish",
                    csprojPath,
                    "-c", "Release",
                    "-o", publishDir,
                    "--no-self-contained",
                    "--nologo",
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = repoRoot,
            };
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            process.ExitCode.Should().Be(0,
                "dotnet publish must succeed for this test to verify the no-leak contract. " +
                $"STDOUT:\n{stdout}\nSTDERR:\n{stderr}");

            // *** Load-bearing assertion ***: the sentinel file is
            // ABSENT from the publish output. If a future change
            // re-enables the SDK's auto-include for this file (or
            // someone "simplifies" the csproj by dropping the Remove
            // rules), this assertion fires loudly with a clear
            // message that names the 2026-05-02 incident.
            var leakedPath = Path.Combine(publishDir, "appsettings.user.json");
            File.Exists(leakedPath).Should().BeFalse(
                "appsettings.user.json must NOT ride into the `dotnet publish` output. " +
                "On 2026-05-02 the trainer-qa GB10 bootstrap leaked a developer's local " +
                "Postgres connection string via this exact path — operator-local dev " +
                "secrets shadowed /etc/<unit>.env values on the deployed host. The csproj " +
                "rule (<Content Remove='appsettings.user.json' /> + <None Remove='...' />) " +
                "is what stops Microsoft.NET.Sdk.Web's implicit <Content Include='appsettings*.json'> " +
                "auto-copy. If THIS assertion fires, that rule was either dropped or " +
                "regressed (e.g. someone reverted to the broken <None Update> form). " +
                $"Leaked file path: {leakedPath}");

            // Sanity: the production appsettings.json IS in the output.
            // A regression that over-broadly killed *all* appsettings*.json
            // (e.g. <Content Remove='appsettings*.json' />) would shut
            // off the production config too — this assertion catches
            // that class of mistake before it deploys.
            File.Exists(Path.Combine(publishDir, "appsettings.json")).Should().BeTrue(
                "the production appsettings.json must STILL be in the publish output. " +
                "A too-broad exclude rule (e.g. matching `appsettings*.json` instead of " +
                "specifically `appsettings.user.json`) would shut off the production config; " +
                "the deployed Assistant would lose its Logging / AllowedHosts defaults.");
        }
        finally
        {
            // Always clean up the publish output dir.
            if (Directory.Exists(publishDir))
            {
                Directory.Delete(publishDir, recursive: true);
            }
            // Always remove the sentinel.
            if (File.Exists(userJsonPath))
            {
                File.Delete(userJsonPath);
            }
            // Restore the developer's pre-existing file if there was one.
            if (hadExistingFile && File.Exists(backupPath))
            {
                File.Move(backupPath, userJsonPath);
            }
        }
    }

    // Walk up from the test binary's directory until we hit a directory
    // containing both Trellis.Assistant and Trellis.Assistant.Tests subdirectories
    // (or the .sln). The test binary lives at
    // .../Trellis.Assistant.Tests/bin/Debug/net10.0/ at runtime, so a fixed
    // relative-path constant would break the moment xUnit's working
    // directory shifted.
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

using Testcontainers.PostgreSql;

namespace Trellis.Assistant.Tests.TestFixtures;

/// <summary>
/// Testcontainers-backed Postgres 16 fixture. Spins up an ephemeral
/// container, exposes its connection string for the
/// <see cref="AssistantWebApplicationFactory"/> + the EF migration
/// smoke test, and tears down on disposal.
///
/// Image: postgres:16 (no pgvector; Assistant doesn't embed). Standard
/// Testcontainers default port mapping. Database name + role default to
/// the Postgres image's defaults so the test factory's connection string
/// is straightforward.
///
/// Skipped cleanly on machines without Docker via the
/// <see cref="IsDockerAvailable"/> probe + Xunit.SkippableFact —
/// matches the trellis-trainer test convention.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // PR #11 review Blocker 1: PostgreSqlBuilder.Build() probes Docker
    // synchronously during .Validate(). On Docker-down hosts this throws
    // DockerUnavailableException; xUnit catches the class-fixture
    // constructor exception and marks every gated test as Failed (not
    // Skipped) — the SkippableFact + Skip.IfNot(IsAvailable) gate never
    // runs because the fixture couldn't be constructed.
    //
    // Defer both the build + the start into InitializeAsync, after the
    // IsDockerAvailable() probe. Field is nullable; ConnectionString
    // throws clearly if accessed before InitializeAsync ran successfully
    // (which means the gate was bypassed — operator error, not silent
    // Docker-down).
    private PostgreSqlContainer? _container;

    /// <summary>
    /// Connection string for the started container. Throws if the
    /// container hasn't been started yet via <see cref="InitializeAsync"/>
    /// (i.e., Docker was down OR the caller bypassed the
    /// <see cref="IsAvailable"/> gate).
    /// </summary>
    public string ConnectionString => _container?.GetConnectionString()
        ?? throw new InvalidOperationException(
            "PostgresFixture not initialized — Docker was unavailable at InitializeAsync, " +
            "or the caller bypassed the IsAvailable skip gate. Wrap consumer tests in " +
            "[SkippableFact] + Skip.IfNot(_pg.IsAvailable, \"...\").");

    public bool IsAvailable { get; private set; }

    public async Task InitializeAsync()
    {
        if (!IsDockerAvailable())
        {
            // Tests that depend on this fixture should use Skip.IfNot
            // and gate on IsAvailable. Fixture initialisation is skipped
            // entirely so we don't waste 30s waiting for a container
            // that can't start AND we don't crash the class-fixture ctor.
            IsAvailable = false;
            return;
        }
        try
        {
#pragma warning disable CS0618 // Obsolete parameterless ctor — kept while
            // we mirror trellis-trainer's TestDatabaseFixture exactly. The
            // image-as-constructor-arg variant produced password-auth failures
            // mid-startup in the AssistantWebApplicationFactory's auto-migrate
            // path on this build (the new ctor's interaction with WithUsername/
            // WithPassword overrides differs subtly from the parameterless +
            // WithImage chain). Once trainer's fixture migrates to the new
            // signature, this one follows.
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:16")
                .WithDatabase("trellis_assistant_test")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
#pragma warning restore CS0618
            await _container.StartAsync();
            IsAvailable = true;
        }
        catch
        {
            // PR #11 v2 review polish: don't rethrow. A failure in
            // .Build() / .StartAsync() AFTER IsDockerAvailable() passed
            // is rare but possible (image pull failure, port exhaustion,
            // OOM, intermittent daemon hiccup). Rethrowing reintroduces
            // the original PR #11 Blocker 1 symptom on a different path
            // — xUnit catches the class-fixture init failure + marks
            // every consumer test as Failed (not Skipped). Dispose the
            // partially-built container if any, leave IsAvailable=false,
            // let the SkippableFact gate skip cleanly.
            IsAvailable = false;
            if (_container is not null)
            {
                try { await _container.DisposeAsync().ConfigureAwait(false); }
                catch { /* best-effort cleanup; do not mask the original failure path */ }
                _container = null;
            }
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>
    /// Cheap probe for Docker availability. Avoids a full container start
    /// when Docker isn't running — the container start would hang ~30s
    /// before failing.
    ///
    /// <para>
    /// 10-second timeout on <c>docker info</c>: the metadata call is
    /// usually sub-second, but when many test classes are concurrently
    /// spinning + tearing down containers, the daemon can briefly serialize
    /// requests. The original Phase 1/2 budget of 2s was sufficient for
    /// the smaller test surface but proved non-deterministic under Phase
    /// 3.A.1's 83-test load (the third concurrent test class's probe
    /// would intermittently timeout while two prior containers were still
    /// reporting cleanup state). 10s is the comfortable ceiling — still
    /// fast-fail when Docker truly isn't running.
    /// </para>
    /// </summary>
    public static bool IsDockerAvailable()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "info",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return false;
            p.WaitForExit(10000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

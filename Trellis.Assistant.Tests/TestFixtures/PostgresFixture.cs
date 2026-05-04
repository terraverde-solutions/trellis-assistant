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
#pragma warning disable CS0618 // Obsolete parameterless ctor — kept while
    // we mirror trellis-trainer's TestDatabaseFixture exactly. The
    // image-as-constructor-arg variant produced password-auth failures
    // mid-startup in the AssistantWebApplicationFactory's auto-migrate
    // path on this build (the new ctor's interaction with WithUsername/
    // WithPassword overrides differs subtly from the parameterless +
    // WithImage chain). Once trainer's fixture migrates to the new
    // signature, this one follows.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16")
        .WithDatabase("trellis_assistant_test")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();
#pragma warning restore CS0618

    /// <summary>
    /// Connection string for the started container. Throws if the
    /// container hasn't been started yet via <see cref="InitializeAsync"/>.
    /// </summary>
    public string ConnectionString => _container.GetConnectionString();

    public bool IsAvailable { get; private set; }

    public async Task InitializeAsync()
    {
        if (!IsDockerAvailable())
        {
            // Tests that depend on this fixture should use Skip.IfNot
            // and gate on IsAvailable. Fixture initialisation is skipped
            // entirely so we don't waste 30s waiting for a container
            // that can't start.
            IsAvailable = false;
            return;
        }
        try
        {
            await _container.StartAsync();
            IsAvailable = true;
        }
        catch
        {
            IsAvailable = false;
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        if (IsAvailable)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>
    /// Cheap probe for Docker availability. Avoids a full container start
    /// when Docker isn't running — the container start would hang ~30s
    /// before failing.
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
            p.WaitForExit(2000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

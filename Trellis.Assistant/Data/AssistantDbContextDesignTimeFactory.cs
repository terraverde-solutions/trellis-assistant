using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Trellis.Assistant.Data;

/// <summary>
/// Design-time factory used by `dotnet ef migrations add` to construct
/// an <see cref="AssistantDbContext"/> without running the full app DI
/// chain. The connection string here is a placeholder — `migrations add`
/// doesn't connect, just inspects the model + emits SQL. `database
/// update` does connect, in which case override via the standard
/// EF tooling (e.g. <c>--connection</c>). At runtime, the real
/// connection string flows in via DI from <c>appsettings.json</c> /
/// <c>ConnectionStrings:Postgres</c>.
///
/// Same pattern as trellis-trainer's design-time factory.
/// </summary>
public sealed class AssistantDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AssistantDbContext>
{
    public AssistantDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AssistantDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=trellis_assistant_design_time;Username=postgres;Password=postgres")
            .Options;
        return new AssistantDbContext(options);
    }
}

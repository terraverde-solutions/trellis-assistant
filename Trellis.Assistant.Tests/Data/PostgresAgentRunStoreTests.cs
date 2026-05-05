using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Trellis.Assistant.Data;
using Trellis.Assistant.Tests.TestFixtures;
using Trellis.Core.Models;
using Xunit;

namespace Trellis.Assistant.Tests.Data;

/// <summary>
/// Direct CRUD + cross-tenant isolation tests for
/// <see cref="PostgresAgentRunStore"/>. Covers the surface that
/// <see cref="AssistantAgentExecutorTests"/> exercises only indirectly,
/// pinning the tenant chokepoint contract without going through the
/// executor.
/// </summary>
public sealed class PostgresAgentRunStoreTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid OrgA = Guid.Parse(TestTenants.TenantA);
    private static readonly Guid OrgB = Guid.Parse(TestTenants.TenantB);
    private readonly PostgresFixture _pg;
    private AssistantDbContext? _db;

    public PostgresAgentRunStoreTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    public async Task InitializeAsync()
    {
        if (_pg.IsAvailable)
        {
            var options = new DbContextOptionsBuilder<AssistantDbContext>()
                .UseNpgsql(_pg.ConnectionString)
                .Options;
            _db = new AssistantDbContext(options);
            await _db.Database.MigrateAsync();
        }
    }

    public async Task DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task CreateRunAsync_RoundTrips_RunWithMatchingFields()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var store = NewStore();
        var input = NewRun(OrgA, plan: "test plan", status: AgentRunStatus.Planning);

        var created = await store.CreateRunAsync(input);

        created.Id.Should().NotBe(Guid.Empty);
        created.OrgId.Should().Be(OrgA);
        created.Plan.Should().Be("test plan");
        created.Status.Should().Be(AgentRunStatus.Planning);
        created.Steps.Should().BeEmpty("freshly-created runs have no steps yet");
    }

    [SkippableFact]
    public async Task CreateRunAsync_ZeroOrgId_Throws()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var store = NewStore();
        var input = NewRun(Guid.Empty, plan: "bad", status: AgentRunStatus.Planning);

        var act = () => store.CreateRunAsync(input);
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*OrgId*non-empty*",
                "the store enforces the same Guid.Empty contract as Trellis.Core's AgentRunRequest.Create");
    }

    [SkippableFact]
    public async Task GetRunAsync_OwnedByOtherOrg_ReturnsNull_DoesNotLeakExistence()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        // OrgA creates a run; OrgB queries it; should get null (NOT
        // distinguish "doesn't exist" from "not visible").
        var store = NewStore();
        var run = await store.CreateRunAsync(NewRun(OrgA, plan: "OrgA's run", status: AgentRunStatus.Planning));

        var crossTenantRead = await store.GetRunAsync(OrgB, run.Id);
        crossTenantRead.Should().BeNull(
            "cross-tenant read of an existing run must return null — caller cannot distinguish 'doesn't exist' from 'not visible' to prevent existence-probing leaks");

        var sameTenantRead = await store.GetRunAsync(OrgA, run.Id);
        sameTenantRead.Should().NotBeNull("same-tenant read of the same row succeeds");
    }

    [SkippableFact]
    public async Task GetRunAsync_ZeroOrgId_Throws()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var store = NewStore();
        var act = () => store.GetRunAsync(Guid.Empty, Guid.NewGuid());
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [SkippableFact]
    public async Task CompleteRunAsync_UpdatesTerminalState_ReturnsUpdatedRun()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var store = NewStore();
        var run = await store.CreateRunAsync(NewRun(OrgA, plan: "test", status: AgentRunStatus.Planning));

        var completedAt = new DateTime(2026, 5, 5, 12, 30, 0, DateTimeKind.Utc);
        var completed = await store.CompleteRunAsync(
            OrgA, run.Id,
            status: AgentRunStatus.Succeeded,
            completedAt: completedAt,
            tokensUsed: 1234L,
            errorMessage: null);

        completed.Status.Should().Be(AgentRunStatus.Succeeded);
        completed.CompletedAt.Should().Be(completedAt);
        completed.TokensUsed.Should().Be(1234);
    }

    [SkippableFact]
    public async Task CompleteRunAsync_CrossTenant_ThrowsInvalidOperation()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var store = NewStore();
        var run = await store.CreateRunAsync(NewRun(OrgA, plan: "OrgA only", status: AgentRunStatus.Planning));

        // OrgB attempts to complete OrgA's run — UPDATE matches 0 rows;
        // store throws InvalidOperationException so the caller can map
        // to 404/403/etc.
        var act = () => store.CompleteRunAsync(
            OrgB, run.Id,
            status: AgentRunStatus.Succeeded,
            completedAt: DateTime.UtcNow,
            tokensUsed: 0,
            errorMessage: null);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*",
                "cross-tenant CompleteRunAsync must surface as InvalidOperationException so callers can map to 404 + the chokepoint contract holds");
    }

    [SkippableFact]
    public async Task AppendStepAsync_PersistsStep_VisibleInGetSteps()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var store = NewStore();
        var run = await store.CreateRunAsync(NewRun(OrgA, plan: "test", status: AgentRunStatus.Running));

        var step = await store.AppendStepAsync(OrgA, NewStep(run.Id, stepIndex: 0, toolName: "echo"));
        step.AgentRunId.Should().Be(run.Id);
        step.StepIndex.Should().Be(0);
        step.ToolName.Should().Be("echo");

        var allSteps = await store.GetStepsAsync(OrgA, run.Id);
        allSteps.Should().HaveCount(1);
        allSteps[0].Id.Should().Be(step.Id);
    }

    [SkippableFact]
    public async Task AppendStepAsync_CrossTenant_ThrowsInvalidOperation()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var store = NewStore();
        var run = await store.CreateRunAsync(NewRun(OrgA, plan: "OrgA's", status: AgentRunStatus.Running));

        var step = NewStep(run.Id, stepIndex: 0, toolName: "echo");
        var act = () => store.AppendStepAsync(OrgB, step);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*",
                "cross-tenant AppendStepAsync must fail loud rather than silently leak a step into another org's run");
    }

    [SkippableFact]
    public async Task AppendStepAsync_DuplicateStepIndex_ThrowsOnUniqueConstraint()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var store = NewStore();
        var run = await store.CreateRunAsync(NewRun(OrgA, plan: "test", status: AgentRunStatus.Running));

        await store.AppendStepAsync(OrgA, NewStep(run.Id, stepIndex: 5, toolName: "echo"));
        var act = () => store.AppendStepAsync(OrgA, NewStep(run.Id, stepIndex: 5, toolName: "echo"));
        await act.Should().ThrowAsync<DbUpdateException>(
            "the (agent_run_id, step_index) unique constraint must fire on duplicate index — pinned for the single-writer-per-run invariant");
    }

    [SkippableFact]
    public async Task GetStepsAsync_CrossTenant_ReturnsEmptyList_DoesNotLeak()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var store = NewStore();
        var run = await store.CreateRunAsync(NewRun(OrgA, plan: "OrgA's", status: AgentRunStatus.Running));
        await store.AppendStepAsync(OrgA, NewStep(run.Id, stepIndex: 0, toolName: "echo"));

        var crossTenantSteps = await store.GetStepsAsync(OrgB, run.Id);
        crossTenantSteps.Should().BeEmpty(
            "cross-tenant GetStepsAsync returns empty rather than leaking step content");

        var sameTenantSteps = await store.GetStepsAsync(OrgA, run.Id);
        sameTenantSteps.Should().HaveCount(1, "same-tenant read still works");
    }

    [SkippableFact]
    public async Task GetStepsAsync_ReturnsStepsInIndexOrder()
    {
        Skip.IfNot(_pg.IsAvailable, "Docker not available; Testcontainers integration test skipped.");

        var store = NewStore();
        var run = await store.CreateRunAsync(NewRun(OrgA, plan: "test", status: AgentRunStatus.Running));

        // Insert out of natural order to verify ORDER BY step_index.
        await store.AppendStepAsync(OrgA, NewStep(run.Id, stepIndex: 2, toolName: "echo"));
        await store.AppendStepAsync(OrgA, NewStep(run.Id, stepIndex: 0, toolName: "echo"));
        await store.AppendStepAsync(OrgA, NewStep(run.Id, stepIndex: 1, toolName: "echo"));

        var steps = await store.GetStepsAsync(OrgA, run.Id);
        steps.Select(s => s.StepIndex).Should().Equal(0, 1, 2);
    }

    private PostgresAgentRunStore NewStore() => new(_db!);

    private static AgentRun NewRun(Guid orgId, string plan, AgentRunStatus status) => new()
    {
        Id = Guid.Empty, // store assigns
        OrgId = orgId,
        AssistantTurnId = null,
        Plan = plan,
        Status = status,
        StartedAt = DateTime.UtcNow,
        CompletedAt = null,
        ArchivedAt = null,
        Steps = Array.Empty<AgentStep>(),
        TokensUsed = 0,
    };

    private static AgentStep NewStep(Guid runId, int stepIndex, string toolName) => new()
    {
        Id = Guid.Empty, // store assigns
        AgentRunId = runId,
        StepIndex = stepIndex,
        ToolName = toolName,
        ToolInputJson = "{}",
        ToolOutputJson = "{\"output\":\"ok\"}",
        Status = AgentStepStatus.Succeeded,
        StartedAt = DateTime.UtcNow,
        CompletedAt = DateTime.UtcNow,
        ErrorMessage = null,
        DurationMs = 5,
    };
}

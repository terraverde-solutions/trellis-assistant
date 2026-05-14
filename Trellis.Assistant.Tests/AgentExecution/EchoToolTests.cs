using FluentAssertions;
using Trellis.Assistant.AgentExecution;
using Trellis.Assistant.Tests.TestFixtures;
using Trellis.Core.Models;
using Xunit;

namespace Trellis.Assistant.Tests.AgentExecution;

/// <summary>
/// Pure-logic unit tests for <see cref="EchoTool"/>. The stub tool's
/// happy path + error-shape contracts feed directly into Phase 3.B's
/// SearchDocumentsTool — same input/output contract.
/// </summary>
public sealed class EchoToolTests
{
    private static readonly Guid TestRunId = Guid.Parse("aaaaaaaa-1111-0000-0000-000000000001");
    private static readonly Guid TestOrgId = Guid.Parse("00000000-0000-0000-0000-00000000000a");

    [Fact]
    public void Descriptor_HasNonEmptyNameAndDescription_AndCategoryInspect()
    {
        var tool = new EchoTool();
        tool.Descriptor.Name.Should().Be("echo");
        tool.Descriptor.Description.Should().NotBeNullOrWhiteSpace();
        tool.Descriptor.ParameterSchema.Should().NotBeNullOrWhiteSpace();
        tool.Descriptor.Category.Should().Be(AgentToolCategory.Inspect,
            "echo is read-only inspection of its own input — not search, not act");
    }

    [Fact]
    public async Task RunAsync_ValidArgs_ReturnsSuccessWithEchoedOutput()
    {
        var tool = new EchoTool();
        var output = await tool.RunAsync(NewInput("""{"text":"hello"}"""));
        output.Success.Should().BeTrue();
        output.ErrorMessage.Should().BeNull();
        output.ResultJson.Should().NotBeNull();
        output.ResultJson!.Should().Contain("\"output\":\"hello\"");
    }

    [Fact]
    public async Task RunAsync_MissingTextProperty_ReturnsFailure()
    {
        var tool = new EchoTool();
        var output = await tool.RunAsync(NewInput("""{}"""));
        output.Success.Should().BeFalse();
        output.ErrorMessage.Should().Contain("text",
            "the failure message must name the missing required field so the planner can correct on retry");
    }

    [Fact]
    public async Task RunAsync_EmptyTextProperty_ReturnsFailure()
    {
        var tool = new EchoTool();
        var output = await tool.RunAsync(NewInput("""{"text":""}"""));
        output.Success.Should().BeFalse();
        output.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task RunAsync_MalformedJsonArgs_ReturnsFailure_DoesNotThrow()
    {
        // Per Core's contract: tools must NOT throw to signal failure;
        // return Success=false instead. Pin the structured-failure path.
        var tool = new EchoTool();
        var output = await tool.RunAsync(NewInput("not json at all"));
        output.Success.Should().BeFalse();
        output.ErrorMessage.Should().Contain("parse",
            "malformed JSON must surface as a structured failure with a parse-related diagnostic");
    }

    [Fact]
    public async Task RunAsync_CancellationRequested_Throws()
    {
        // Cancellation IS expected to throw (OperationCanceledException) —
        // the Core contract distinguishes "tool failed" (Success=false)
        // from "caller cancelled" (CT trip). Tools must respect the CT.
        var tool = new EchoTool();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = () => tool.RunAsync(NewInput("""{"text":"hi"}"""), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static AgentToolInput NewInput(string parametersJson) => new()
    {
        AgentRunId = TestRunId,
        StepIndex = 0,
        ParametersJson = parametersJson,
        OrgId = TestOrgId,
        UserId = TestTenants.UserA,  // Phase 3.D widening; EchoTool ignores user scope but the field is required.
    };
}

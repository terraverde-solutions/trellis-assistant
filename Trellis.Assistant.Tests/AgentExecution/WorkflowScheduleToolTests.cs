using FluentAssertions;
using Trellis.Assistant.AgentExecution;
using Trellis.Assistant.Services;
using Trellis.Core.Models;
using Trellis.Core.Services;
using Xunit;

namespace Trellis.Assistant.Tests.AgentExecution;

/// <summary>
/// Phase 3.I pins for <see cref="WorkflowScheduleTool"/>. Pure-unit
/// pattern matching <see cref="ChatRecentToolTests"/>: stub the
/// <see cref="IWorkflowClient"/> via a hand-rolled fake; no DB, no
/// executor — just the tool's RunAsync behavior + the structured
/// LLM-visible failure envelope.
///
/// <para>
/// Six pin tests cover the contract enumerated in the Phase 3.I brief:
/// happy path → run id surfaced; schema-bypass missing-arg → invalid_args;
/// 404 → definition_not_found echoed; transient → transient envelope;
/// schema validates via JsonSchemaNetValidator; descriptor shape.
/// </para>
/// </summary>
public sealed class WorkflowScheduleToolTests
{
    private static readonly Guid SampleDefinitionId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void Descriptor_HasExpectedShape()
    {
        var tool = NewTool(out _);
        tool.Descriptor.Name.Should().Be(WorkflowScheduleTool.ToolName);
        tool.Descriptor.Category.Should().Be(AgentToolCategory.Act,
            "workflow_schedule is side-effecting — planner-LLM steering depends on the Act category");
        tool.Descriptor.Description.Should().Contain("workflow");
        tool.Descriptor.ParameterSchema.Should().Contain("workflow_definition_id");
    }

    [Fact]
    public void Descriptor_ParameterSchema_IsValidJsonSchema()
    {
        var tool = NewTool(out _);
        var validator = new JsonSchemaNetValidator();
        var act = () => validator.EnsureValidSchema(tool.Descriptor.ParameterSchema);
        act.Should().NotThrow(
            "WorkflowScheduleTool's ParameterSchema must parse — startup ToolRegistry validation would crash the host otherwise");
    }

    [Fact]
    public async Task RunAsync_HappyPath_ReturnsRunIdFromQwen()
    {
        // Phase 3.I pin 1: stub returns 201 Created body; the tool
        // surfaces it verbatim into AgentToolOutput.ResultJson. The LLM
        // sees the {id, status} shape it can use for follow-up.
        var tool = NewTool(out var stub);
        stub.Result = new WorkflowScheduleResult
        {
            Success = true,
            ResponseBodyJson = """{"id":"77777777-7777-7777-7777-777777777777","status":1}""",
        };

        var output = await tool.RunAsync(new AgentToolInput
        {
            AgentRunId = Guid.NewGuid(),
            StepIndex = 0,
            ParametersJson = "{\"workflow_definition_id\":\"" + SampleDefinitionId.ToString("D")
                + "\",\"initial_input_json\":{\"email\":\"a@b.co\"}}",
            OrgId = Guid.Parse(TestFixtures.TestTenants.TenantA),
            UserId = TestFixtures.TestTenants.UserA,
        });

        output.Success.Should().BeTrue();
        output.ErrorMessage.Should().BeNull();
        output.ResultJson.Should().Contain("77777777-7777-7777-7777-777777777777",
            "qwen's 201 body passes through verbatim — the LLM reads the id directly");

        // The client was invoked with the parsed definition id + the
        // initial-input JSON extracted from the LLM args.
        stub.LastDefinitionId.Should().Be(SampleDefinitionId);
        stub.LastInitialInputJson.Should().Be("""{"email":"a@b.co"}""",
            "initial_input_json passes through to the client as raw JSON — no Assistant-side reshape");
    }

    [Fact]
    public async Task RunAsync_SchemaBypass_MissingWorkflowDefinitionId_ReturnsStructuredError_WithoutDispatch()
    {
        // Phase 3.I pin 2: with schema validation upstack the executor
        // catches this. At the tool layer (covering schema-bypass /
        // future schema drift), the tool short-circuits with a structured
        // error envelope BEFORE any IWorkflowClient call.
        var tool = NewTool(out var stub);

        var output = await tool.RunAsync(new AgentToolInput
        {
            AgentRunId = Guid.NewGuid(),
            StepIndex = 0,
            ParametersJson = """{"initial_input_json":{"a":1}}""",
            OrgId = Guid.Parse(TestFixtures.TestTenants.TenantA),
            UserId = TestFixtures.TestTenants.UserA,
        });

        output.Success.Should().BeFalse();
        output.ErrorMessage.Should().Contain("workflow_definition_id",
            "missing-required-arg failure surfaces the field name");
        stub.CallCount.Should().Be(0,
            "missing definition id short-circuits BEFORE the HTTP call — no outbound HTTP");
    }

    [Fact]
    public async Task RunAsync_QwenReturns404_LlmGetsDefinitionNotFoundEnvelope()
    {
        // Phase 3.I pin 3: 404 surfaces as a structured envelope the
        // LLM can read. The envelope echoes the workflow_definition_id
        // back so the LLM can ask the user about a different one.
        var tool = NewTool(out var stub);
        stub.Result = new WorkflowScheduleResult
        {
            Success = false,
            ErrorCode = WorkflowScheduleErrorCode.DefinitionNotFound,
            ErrorMessage = $"Workflow definition '{SampleDefinitionId:D}' not found.",
        };

        var output = await tool.RunAsync(new AgentToolInput
        {
            AgentRunId = Guid.NewGuid(),
            StepIndex = 0,
            ParametersJson = "{\"workflow_definition_id\":\"" + SampleDefinitionId.ToString("D") + "\"}",
            OrgId = Guid.Parse(TestFixtures.TestTenants.TenantA),
            UserId = TestFixtures.TestTenants.UserA,
        });

        output.Success.Should().BeFalse();
        output.ResultJson.Should().Contain("\"error\":\"definition_not_found\"",
            "structured-error envelope uses the snake_case error key so the LLM can pattern-match");
        output.ResultJson.Should().Contain(SampleDefinitionId.ToString("D"),
            "envelope echoes the requested id back to the LLM");
        output.ErrorMessage.Should().Contain("not found");
    }

    [Fact]
    public async Task RunAsync_QwenReturnsTransient_LlmGetsTransientEnvelope()
    {
        // Phase 3.I pin 4: 5xx / timeout / transport all flow to
        // WorkflowScheduleErrorCode.Transient at the client; the tool
        // surfaces error=transient so the LLM can retry-with-backoff
        // or surface to the user.
        var tool = NewTool(out var stub);
        stub.Result = new WorkflowScheduleResult
        {
            Success = false,
            ErrorCode = WorkflowScheduleErrorCode.Transient,
            ErrorMessage = "workflow_schedule: timed out after 10s",
        };

        var output = await tool.RunAsync(new AgentToolInput
        {
            AgentRunId = Guid.NewGuid(),
            StepIndex = 0,
            ParametersJson = "{\"workflow_definition_id\":\"" + SampleDefinitionId.ToString("D") + "\"}",
            OrgId = Guid.Parse(TestFixtures.TestTenants.TenantA),
            UserId = TestFixtures.TestTenants.UserA,
        });

        output.Success.Should().BeFalse();
        output.ResultJson.Should().Contain("\"error\":\"transient\"");
        output.ErrorMessage.Should().Contain("timed out");
    }

    [Fact]
    public async Task RunAsync_QwenReturns401_LlmGetsAuthFailedEnvelope()
    {
        // Phase 3.I auth_failed path: structured envelope so the LLM
        // knows NOT to retry. Operator-actionable (credentials issue);
        // tagging this distinctly from transient lets retry-loops
        // discriminate.
        var tool = NewTool(out var stub);
        stub.Result = new WorkflowScheduleResult
        {
            Success = false,
            ErrorCode = WorkflowScheduleErrorCode.AuthFailed,
            ErrorMessage = "Workflow service rejected credentials.",
        };

        var output = await tool.RunAsync(new AgentToolInput
        {
            AgentRunId = Guid.NewGuid(),
            StepIndex = 0,
            ParametersJson = "{\"workflow_definition_id\":\"" + SampleDefinitionId.ToString("D") + "\"}",
            OrgId = Guid.Parse(TestFixtures.TestTenants.TenantA),
            UserId = TestFixtures.TestTenants.UserA,
        });

        output.Success.Should().BeFalse();
        output.ResultJson.Should().Contain("\"error\":\"auth_failed\"",
            "auth_failed is distinct from transient so the LLM knows not to retry");
    }

    [Fact]
    public void SerializeInitialInputIfPresent_ObjectPresent_ReturnsRawJson()
    {
        var raw = WorkflowScheduleTool.SerializeInitialInputIfPresent(
            """{"workflow_definition_id":"x","initial_input_json":{"a":1,"b":[2,3]}}""");
        raw.Should().Be("""{"a":1,"b":[2,3]}""",
            "initial_input_json passes to the client as raw JSON — no Assistant-side reshape");
    }

    [Fact]
    public void SerializeInitialInputIfPresent_NoObject_ReturnsNull()
    {
        WorkflowScheduleTool.SerializeInitialInputIfPresent("""{"workflow_definition_id":"x"}""")
            .Should().BeNull();
    }

    [Fact]
    public void SerializeInitialInputIfPresent_CamelCaseKey_AlsoExtracts()
    {
        // LLMs drift between snake_case + camelCase even with snake_case
        // schemas. Defensive parse accepts either; the JSON Schema is
        // the authoritative shape.
        var raw = WorkflowScheduleTool.SerializeInitialInputIfPresent(
            """{"workflowDefinitionId":"x","initialInputJson":{"k":1}}""");
        raw.Should().Be("""{"k":1}""");
    }

    // ---------------- helpers ----------------

    private static WorkflowScheduleTool NewTool(out StubWorkflowClient stub)
    {
        var captured = new StubWorkflowClient();
        var tool = new WorkflowScheduleTool(() => captured);
        stub = captured;
        return tool;
    }

    private sealed class StubWorkflowClient : IWorkflowClient
    {
        public int CallCount { get; private set; }
        public Guid LastDefinitionId { get; private set; }
        public string? LastInitialInputJson { get; private set; }
        public WorkflowScheduleResult Result { get; set; } = new()
        {
            Success = true,
            ResponseBodyJson = """{"id":"00000000-0000-0000-0000-000000000000","status":0}""",
        };

        public Task<WorkflowScheduleResult> ScheduleByIdAsync(
            Guid workflowDefinitionId,
            string? initialInputJson,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastDefinitionId = workflowDefinitionId;
            LastInitialInputJson = initialInputJson;
            return Task.FromResult(Result);
        }
    }
}

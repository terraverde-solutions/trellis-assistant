using System.Text.Json;
using Trellis.Assistant.Services;
using Trellis.Core.Models;
using Trellis.Core.Services;

namespace Trellis.Assistant.AgentExecution;

/// <summary>
/// Phase 3.I's third production tool. Lets the LLM schedule a workflow
/// run via trellis-workflow's <c>POST /api/workflows/runs</c>
/// schedule-by-id branch (qwen Phase 4.A). Only the by-id branch is
/// exposed — the inline-JSON branch takes a full
/// <c>WorkflowDefinitionJson</c> blob that an LLM has no business
/// emitting (hallucination + injection risk; per Phase 3.I brief).
///
/// <para>
/// Wire shape: LLM-emitted arguments JSON deserializes to
/// <see cref="ToolArgs"/>, dispatches via <see cref="IWorkflowClient"/>,
/// returns qwen's 201 Created body verbatim into
/// <see cref="AgentToolOutput.ResultJson"/>. Failure cases surface as
/// structured error envelopes keyed on
/// <see cref="WorkflowScheduleErrorCode"/>:
/// <list type="bullet">
/// <item>401 → <c>{error: "auth_failed"}</c> — LLM should NOT retry.</item>
/// <item>404 → <c>{error: "definition_not_found", workflow_definition_id}</c>
/// — LLM can ask the user for a different id.</item>
/// <item>5xx / transport / timeout → <c>{error: "transient"}</c> — LLM
/// can retry-with-backoff or surface to user.</item>
/// <item>Other 4xx → <c>{error: "bad_request", detail}</c> — LLM reads
/// the detail + decides whether to retry with corrected args.</item>
/// </list>
/// </para>
///
/// <para>
/// Category: <see cref="AgentToolCategory.Act"/> — side-effecting tool
/// (kicks off a workflow run). The planner LLM is steered (via Core's
/// AgentToolCategory docstring) to prefer Search/Inspect tools when
/// investigating, Act when committing — which keeps the LLM from
/// chaining schedule calls speculatively.
/// </para>
///
/// <para>
/// Exposure gating: <see cref="ToolCatalogueOptions.ExposeWorkflowSchedule"/>
/// boolean (Phase 3.I; mirrors EchoTool's <c>ExposeEcho</c> precedent).
/// Defaults <c>false</c> in production — opt-in until the tool gets
/// real-world exercise. <see cref="DefaultToolExposurePolicy"/>
/// special-cases workflow_schedule against the flag (matches how Echo
/// is gated; the per-tenant PerTool map is left available for layered
/// gating once the flag is flipped on).
/// </para>
///
/// <para>
/// Schema validation: the executor validates
/// <see cref="AgentToolInput.ParametersJson"/> against
/// <see cref="AgentToolDescriptor.ParameterSchema"/> BEFORE dispatch
/// (Phase 0 contract; Phase 3.B turn-on). By the time RunAsync fires,
/// args parse + match. The defensive parse below covers schema/library
/// drift.
/// </para>
/// </summary>
public sealed class WorkflowScheduleTool : IAgentTool
{
    public const string ToolName = "workflow_schedule";

    /// <summary>
    /// JSON Schema (Draft 2020-12) for the tool's argument surface. Only
    /// the schedule-by-id wire shape — initialInputJson is an
    /// optional JSON object the LLM can fill with the new run's inputs.
    /// </summary>
    public const string ParameterSchemaJson = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "workflow_definition_id": {
          "type": "string",
          "format": "uuid",
          "description": "GUID of the workflow definition to schedule. The definition must already exist in the workflow service for the current tenant."
        },
        "initial_input_json": {
          "type": "object",
          "additionalProperties": true,
          "description": "Optional input object passed to the workflow run. Shape depends on the workflow definition's declared inputs."
        }
      },
      "required": ["workflow_definition_id"]
    }
    """;

    public AgentToolDescriptor Descriptor { get; } = new()
    {
        Name = ToolName,
        Description =
            "Schedule a workflow run by its definition id. Use this when the user explicitly asks to " +
            "start a workflow (e.g. 'kick off the onboarding flow', 'run the weekly report'). The workflow " +
            "definition must exist in the workflow service for the current tenant. Returns the new run's " +
            "id and status; this tool fires the run (side effect — prefer to confirm with the user before " +
            "invoking). Do not retry on 'auth_failed'; can retry on 'transient'; ask the user for a different " +
            "id on 'definition_not_found'.",
        ParameterSchema = ParameterSchemaJson,
        Category = AgentToolCategory.Act,
    };

    private static readonly JsonSerializerOptions ArgsJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions ErrorEnvelopeJsonOpts = new()
    {
        WriteIndented = false,
    };

    private readonly Func<IWorkflowClient> _clientFactory;

    /// <summary>
    /// Take a factory rather than the client directly: <see cref="IWorkflowClient"/>
    /// is registered as a typed-HttpClient client, effectively transient
    /// per IHttpClientFactory's contract. Capturing the client directly
    /// in a singleton tool would pin the first transient instance + its
    /// handler for the host's lifetime, bypassing IHttpClientFactory's
    /// 2-minute handler rotation. Same pattern as
    /// <see cref="SearchDocumentsTool"/>.
    /// </summary>
    public WorkflowScheduleTool(Func<IWorkflowClient> clientFactory)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    }

    public async Task<AgentToolOutput> RunAsync(
        AgentToolInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        ToolArgs args;
        try
        {
            args = JsonSerializer.Deserialize<ToolArgs>(input.ParametersJson, ArgsJsonOpts)
                ?? throw new JsonException("arguments deserialized to null");
        }
        catch (JsonException ex)
        {
            return new AgentToolOutput
            {
                Success = false,
                ResultJson = null,
                ErrorMessage = $"{ToolName}: failed to parse arguments JSON: {ex.Message}",
            };
        }

        if (args.WorkflowDefinitionId is null || args.WorkflowDefinitionId == Guid.Empty)
        {
            return new AgentToolOutput
            {
                Success = false,
                ResultJson = null,
                ErrorMessage = $"{ToolName}: required argument 'workflow_definition_id' was missing or empty.",
            };
        }

        // initial_input_json is preserved as raw JSON if the LLM emitted
        // a nested object — the schema validator already verified it's an
        // object literal. Re-serializing through JsonElement keeps the
        // shape verbatim without forcing a typed model that would lock
        // the workflow's input shape at the Assistant layer.
        var initialInputJson = SerializeInitialInputIfPresent(input.ParametersJson);

        var client = _clientFactory();
        var result = await client
            .ScheduleByIdAsync(args.WorkflowDefinitionId.Value, initialInputJson, cancellationToken)
            .ConfigureAwait(false);

        if (result.Success)
        {
            return new AgentToolOutput
            {
                Success = true,
                ResultJson = result.ResponseBodyJson ?? "{}",
                ErrorMessage = null,
            };
        }

        // Build the LLM-visible structured-error envelope per Phase 3.I
        // brief. ResultJson carries the structured shape (LLM-readable);
        // ErrorMessage carries the human-readable description (operator
        // log + AgentStep.ErrorMessage). The executor surfaces both.
        var envelope = BuildErrorEnvelope(result, args.WorkflowDefinitionId.Value);
        return new AgentToolOutput
        {
            Success = false,
            ResultJson = envelope,
            ErrorMessage = result.ErrorMessage ?? $"{ToolName}: workflow service failure.",
        };
    }

    private static string BuildErrorEnvelope(
        WorkflowScheduleResult result, Guid workflowDefinitionId)
    {
        var errorKey = result.ErrorCode switch
        {
            WorkflowScheduleErrorCode.AuthFailed => "auth_failed",
            WorkflowScheduleErrorCode.DefinitionNotFound => "definition_not_found",
            WorkflowScheduleErrorCode.BadRequest => "bad_request",
            WorkflowScheduleErrorCode.Transient => "transient",
            null => "transient",
            _ => "transient",
        };
        return JsonSerializer.Serialize(new
        {
            error = errorKey,
            message = result.ErrorMessage,
            workflow_definition_id = workflowDefinitionId.ToString("D"),
        }, ErrorEnvelopeJsonOpts);
    }

    /// <summary>
    /// Extract <c>initial_input_json</c> from the LLM-emitted argument
    /// JSON as a raw JSON string. Visible-for-testing pure function.
    /// Returns null when the property is absent, JsonValueKind.Null, or
    /// not an object. The schema validator should have already rejected
    /// non-object shapes by the time RunAsync fires; this defense covers
    /// the schema-bypass code path (off-line tool invocations / future
    /// schema drift).
    /// </summary>
    public static string? SerializeInitialInputIfPresent(string parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(parametersJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            if (!doc.RootElement.TryGetProperty("initial_input_json", out var inputElement)
                && !doc.RootElement.TryGetProperty("initialInputJson", out inputElement))
            {
                return null;
            }
            if (inputElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            return inputElement.GetRawText();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Deserialized LLM-emitted argument shape. snake_case to match the
    /// JSON Schema; <see cref="JsonSerializerOptions.PropertyNameCaseInsensitive"/>
    /// makes camelCase + snake_case forms both parse against PascalCase
    /// .NET properties.
    /// </summary>
    public sealed record ToolArgs
    {
        [System.Text.Json.Serialization.JsonPropertyName("workflow_definition_id")]
        public Guid? WorkflowDefinitionId { get; init; }
    }
}

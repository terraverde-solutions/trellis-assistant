namespace Trellis.Assistant.AgentExecution;

/// <summary>
/// JSON Schema validation seam — Phase 3.B turn-on of the
/// schema-validation-before-dispatch contract that Core's
/// <see cref="Trellis.Core.Services.IAgentTool"/> docstring promises
/// ("the executor has already schema-validated
/// <c>ParametersJson</c> against
/// <c>Descriptor.ParameterSchema</c>, so implementations can assume the
/// JSON parses + matches"). Phase 3.A.1 deferred this surface;
/// <see cref="SearchDocumentsTool"/>'s non-trivial multi-property schema
/// is the consumer that justifies turning it on.
///
/// <para>
/// Two responsibilities, deliberately separate methods:
/// <list type="bullet">
/// <item><see cref="EnsureValidSchema"/> — startup-time check that a
/// tool's <c>ParameterSchema</c> string is itself a parseable JSON
/// Schema (Draft 2020-12). Called by <see cref="ToolRegistry"/>'s ctor
/// for each registered tool; throws on malformed schemas so the host
/// crashes at startup rather than letting a misconfigured tool surface
/// as a 500 on first dispatch.</item>
/// <item><see cref="Validate"/> — runtime check that the LLM-emitted
/// arguments JSON satisfies the schema. Called by
/// <see cref="AssistantAgentExecutor"/> before each tool dispatch;
/// invalid args surface as <c>AgentStepStatus.Failed</c> with a
/// structured error message the next LLM iteration sees in history,
/// letting the model retry with corrected args (decide-and-document #4:
/// REJECT semantics).</item>
/// </list>
/// </para>
///
/// <para>
/// Implementations should cache parsed schemas keyed by the schema-text
/// string — schemas are bounded (one per registered tool, ~10 tools in
/// v0) + immutable for the host's lifetime, so an unbounded
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>
/// is cheap + safe.
/// </para>
/// </summary>
public interface IJsonSchemaValidator
{
    /// <summary>
    /// Parse + cache a schema. Throws
    /// <see cref="InvalidJsonSchemaException"/> when the input is not a
    /// valid JSON Schema document.
    /// </summary>
    void EnsureValidSchema(string schemaJson);

    /// <summary>
    /// Validate an instance JSON string against a schema JSON string.
    /// Returns the validation outcome; never throws for invalid
    /// instances (those flow through the result type). Throws
    /// <see cref="InvalidJsonSchemaException"/> if the schema itself is
    /// malformed — but in the normal flow, the schema has already been
    /// validated at startup via <see cref="EnsureValidSchema"/>.
    /// </summary>
    JsonSchemaValidationResult Validate(string schemaJson, string instanceJson);
}

/// <summary>
/// Outcome of <see cref="IJsonSchemaValidator.Validate"/>. On
/// <see cref="IsValid"/>=<c>true</c>, <see cref="FirstError"/> is
/// <c>null</c>. On <see cref="IsValid"/>=<c>false</c>,
/// <see cref="FirstError"/> carries a short normalized
/// <c>"&lt;instance-path&gt;: &lt;message&gt;"</c> string suitable for
/// embedding in the LLM-visible error envelope. We surface just the
/// first error (not the full list) to keep the message short — schema
/// libraries often emit verbose per-keyword error trees, and the LLM
/// only needs one anchor to fix its next call.
/// </summary>
public sealed record JsonSchemaValidationResult
{
    public required bool IsValid { get; init; }
    public string? FirstError { get; init; }
}

/// <summary>
/// Thrown by <see cref="IJsonSchemaValidator.EnsureValidSchema"/> when a
/// tool registers a <c>ParameterSchema</c> string that isn't a parseable
/// JSON Schema. Caught by <see cref="ToolRegistry"/>'s ctor + rethrown
/// as an <see cref="InvalidOperationException"/> with tool-identifying
/// context, so the host's startup log identifies the offending tool.
/// </summary>
public sealed class InvalidJsonSchemaException : Exception
{
    public InvalidJsonSchemaException(string message) : base(message) { }
    public InvalidJsonSchemaException(string message, Exception inner) : base(message, inner) { }
}

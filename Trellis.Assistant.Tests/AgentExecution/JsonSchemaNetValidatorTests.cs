using FluentAssertions;
using Trellis.Assistant.AgentExecution;
using Xunit;

namespace Trellis.Assistant.Tests.AgentExecution;

/// <summary>
/// Pins for <see cref="JsonSchemaNetValidator"/> — Phase 3.B turn-on of
/// JSON Schema validation for tool args. Covers both responsibilities:
/// startup descriptor validation (<see cref="JsonSchemaNetValidator.EnsureValidSchema"/>)
/// + runtime instance validation
/// (<see cref="JsonSchemaNetValidator.Validate"/>).
///
/// <para>
/// Pure unit tests — no DB, no HTTP. Validates the JsonSchema.Net wrap +
/// error normalization the executor / registry depend on.
/// </para>
/// </summary>
public sealed class JsonSchemaNetValidatorTests
{
    private const string TrivialSchema = """{"type":"object"}""";

    private const string SearchSchema = """
    {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "query": {"type": "string", "minLength": 1},
        "top_k": {"type": "integer", "minimum": 1, "maximum": 50},
        "mode": {"type": "string", "enum": ["vector", "lexical", "hybrid"]}
      },
      "required": ["query"]
    }
    """;

    [Fact]
    public void EnsureValidSchema_ValidSchema_DoesNotThrow()
    {
        var v = new JsonSchemaNetValidator();
        var act = () => v.EnsureValidSchema(SearchSchema);
        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureValidSchema_MalformedJson_ThrowsInvalidJsonSchemaException()
    {
        var v = new JsonSchemaNetValidator();
        var act = () => v.EnsureValidSchema("{this is not json");
        act.Should().Throw<InvalidJsonSchemaException>()
            .WithMessage("*not a parseable JSON Schema*");
    }

    [Fact]
    public void EnsureValidSchema_EmptyString_ThrowsArgumentException()
    {
        var v = new JsonSchemaNetValidator();
        var act = () => v.EnsureValidSchema("");
        // ArgumentException.ThrowIfNullOrWhiteSpace path; surfaces before
        // the parse attempt.
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Validate_HappyPath_AllRequiredFieldsPresent_IsValidTrue()
    {
        var v = new JsonSchemaNetValidator();
        var result = v.Validate(SearchSchema, """{"query":"refund policy","top_k":10,"mode":"hybrid"}""");
        result.IsValid.Should().BeTrue();
        result.FirstError.Should().BeNull();
    }

    [Fact]
    public void Validate_MissingRequiredQuery_IsValidFalse_WithStructuredError()
    {
        var v = new JsonSchemaNetValidator();
        var result = v.Validate(SearchSchema, """{"top_k":5}""");
        result.IsValid.Should().BeFalse();
        result.FirstError.Should().NotBeNullOrEmpty(
            "every validation failure carries an LLM-actionable anchor string");
    }

    [Fact]
    public void Validate_BadEnumValue_IsValidFalse()
    {
        var v = new JsonSchemaNetValidator();
        var result = v.Validate(SearchSchema, """{"query":"x","mode":"bogus"}""");
        result.IsValid.Should().BeFalse();
        result.FirstError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Validate_BadTypeForField_IsValidFalse()
    {
        var v = new JsonSchemaNetValidator();
        var result = v.Validate(SearchSchema, """{"query":"x","top_k":"not-an-int"}""");
        result.IsValid.Should().BeFalse();
        result.FirstError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Validate_UnparseableInstanceJson_IsValidFalse_WithSyntheticRootError()
    {
        var v = new JsonSchemaNetValidator();
        var result = v.Validate(TrivialSchema, "{not json");
        result.IsValid.Should().BeFalse();
        result.FirstError.Should().StartWith("/: invalid JSON",
            "unparseable instance gets a synthetic root-anchored error so the LLM sees the failure category");
    }

    [Fact]
    public void Validate_CachesParsedSchema_SecondCall_FastPath()
    {
        // No timing assertion (test would be flaky); just exercise the
        // cache path twice + assert both succeed. The point is to pin
        // that EnsureValidSchema + Validate share the cache and the
        // second call doesn't re-parse + fail under load.
        var v = new JsonSchemaNetValidator();
        v.EnsureValidSchema(SearchSchema);
        v.Validate(SearchSchema, """{"query":"first"}""").IsValid.Should().BeTrue();
        v.Validate(SearchSchema, """{"query":"second"}""").IsValid.Should().BeTrue();
    }
}

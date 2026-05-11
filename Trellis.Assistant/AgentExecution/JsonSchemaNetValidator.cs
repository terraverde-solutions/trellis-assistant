using System.Collections.Concurrent;
using System.Text.Json;
using Json.Schema;

namespace Trellis.Assistant.AgentExecution;

/// <summary>
/// JsonSchema.Net-backed <see cref="IJsonSchemaValidator"/>. Wraps
/// <see cref="JsonSchema.FromText(string,JsonSerializerOptions?)"/> +
/// <see cref="JsonSchema.Evaluate(JsonNode?,EvaluationOptions?)"/> with
/// a parsed-schema cache + normalized error reporting.
///
/// <para>
/// JsonSchema.Net is the canonical .NET implementation of JSON Schema
/// Draft 2020-12 — same draft that Anthropic's tool_use schema and
/// OpenAI's function-calling schema target, which is why we picked it
/// for Phase 3.B (matches the schemas the LLM is already trained to
/// emit args against).
/// </para>
///
/// <para>
/// Cache discipline: schemas are bounded by the tool registry size + are
/// immutable strings (per <see cref="Core.Models.AgentToolDescriptor.ParameterSchema"/>'s
/// init-only contract); an unbounded
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by schema text
/// is safe — v0 has &lt;10 tools, none of which mutate their schema.
/// </para>
///
/// <para>
/// Error normalization: JsonSchema.Net's <see cref="EvaluationResults"/>
/// is a tree of per-keyword evaluations + per-error messages, which can
/// produce verbose multi-error reports for a single bad instance. We
/// flatten to <c>"&lt;instance-path&gt;: &lt;first-message&gt;"</c> —
/// the LLM only needs one anchor to fix its next call.
/// </para>
/// </summary>
public sealed class JsonSchemaNetValidator : IJsonSchemaValidator
{
    private readonly ConcurrentDictionary<string, JsonSchema> _schemaCache = new(StringComparer.Ordinal);

    public void EnsureValidSchema(string schemaJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaJson);
        _ = GetOrParseSchema(schemaJson);
    }

    public JsonSchemaValidationResult Validate(string schemaJson, string instanceJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaJson);

        var schema = GetOrParseSchema(schemaJson);

        // JsonSchema.Net v9's Evaluate takes JsonElement (not JsonNode).
        // JsonDocument owns the buffer; the RootElement is only valid
        // while the document is alive, so we evaluate inside the using
        // scope and materialize the result before disposing.
        JsonDocument doc;
        try
        {
            // Empty/whitespace instance is itself a validation target —
            // synthesize an "{}" document so the schema's required-field
            // checks fire correctly. The LLM never emits "" args; this is
            // defensive against a future canonicalizer change.
            doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(instanceJson) ? "{}" : instanceJson);
        }
        catch (JsonException ex)
        {
            // Unparseable instance JSON is itself a validation failure —
            // surface a synthetic root-anchored error so the LLM sees
            // "the args you sent aren't even valid JSON".
            return new JsonSchemaValidationResult
            {
                IsValid = false,
                FirstError = $"/: invalid JSON ({ex.Message})",
            };
        }

        using (doc)
        {
            var results = schema.Evaluate(doc.RootElement, new EvaluationOptions
            {
                OutputFormat = OutputFormat.List,
            });

            if (results.IsValid)
            {
                return new JsonSchemaValidationResult { IsValid = true };
            }

            return new JsonSchemaValidationResult
            {
                IsValid = false,
                FirstError = ExtractFirstError(results),
            };
        }
    }

    private JsonSchema GetOrParseSchema(string schemaJson)
    {
        return _schemaCache.GetOrAdd(schemaJson, static text =>
        {
            try
            {
                return JsonSchema.FromText(text);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or FormatException)
            {
                throw new InvalidJsonSchemaException(
                    $"schema text is not a parseable JSON Schema Draft 2020-12 document: {ex.Message}",
                    ex);
            }
        });
    }

    /// <summary>
    /// Pull the first concrete error message out of an
    /// <see cref="EvaluationResults"/> tree (OutputFormat=List), normalized
    /// to <c>"&lt;instance-path&gt;: &lt;message&gt;"</c>. The tree's
    /// <see cref="EvaluationResults.Details"/> carries one entry per
    /// failed keyword evaluation; the first entry with a populated
    /// <see cref="EvaluationResults.Errors"/> dict is our best
    /// LLM-actionable anchor. Falls back to a generic message if the
    /// tree shape isn't what we expect (shouldn't happen with
    /// OutputFormat=List, but a contract-violating library version
    /// shouldn't crash the agent loop).
    /// </summary>
    private static string ExtractFirstError(EvaluationResults results)
    {
        // JsonSchema.Net v9 annotates Details as nullable on the
        // declared property, but the runtime always returns a (possibly
        // empty) List — null-forgiving the dereference rather than
        // adding a redundant guard.
        foreach (var detail in results.Details!)
        {
            if (detail.Errors is { Count: > 0 } errors)
            {
                var first = errors.First();
                var path = detail.InstanceLocation.ToString();
                var message = first.Value ?? first.Key;
                return string.IsNullOrEmpty(path)
                    ? $"/: {message}"
                    : $"{path}: {message}";
            }
        }
        // Root-level errors (rare with OutputFormat=List but possible).
        if (results.Errors is { Count: > 0 } rootErrors)
        {
            var first = rootErrors.First();
            return $"/: {first.Value ?? first.Key}";
        }
        return "/: schema validation failed (no specific error message available)";
    }
}

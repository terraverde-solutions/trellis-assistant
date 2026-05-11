namespace Trellis.Assistant.Services;

/// <summary>
/// Loopback-trust client for trellis-trainer's <c>GET /api/search</c>
/// endpoint. Used by <see cref="AgentExecution.SearchDocumentsTool"/> to
/// dispatch agent-driven semantic search against the customer's indexed
/// corpus.
///
/// <para>
/// Pass-through (a) semantics per Phase 3.B brief: Trainer returns a bare
/// JSON array (no envelope, no <c>took_ms</c>, no <c>corpus_status</c>);
/// the client returns the raw body string verbatim on 2xx so the tool
/// can stuff it into <see cref="Core.Models.AgentToolOutput.ResultJson"/>
/// without re-serializing. Trainer-canonical field names (PascalCase per
/// the C# record contract — <c>DocumentId</c>, <c>DocumentTitle</c>,
/// etc.) flow through to the LLM.
/// </para>
///
/// <para>
/// Non-2xx maps to <see cref="SearchClientResult.Success"/>=<c>false</c>
/// with <see cref="SearchClientResult.ErrorMessage"/> populated from
/// Trainer's <c>ProblemDetails.Detail</c> on 4xx or a transport
/// description on 5xx/network. The tool surfaces those verbatim into
/// the LLM-visible error envelope.
/// </para>
/// </summary>
public interface ISearchClient
{
    Task<SearchClientResult> SearchAsync(
        SearchQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Discriminated-union result. On <see cref="Success"/>=<c>true</c>,
/// <see cref="ResponseBodyJson"/> is the bare JSON array from Trainer.
/// On <see cref="Success"/>=<c>false</c>, <see cref="ErrorMessage"/>
/// carries a short human-readable failure description (4xx detail,
/// transport error, timeout); <see cref="ResponseBodyJson"/> may be
/// <c>null</c>.
/// </summary>
public sealed record SearchClientResult
{
    public required bool Success { get; init; }
    public string? ResponseBodyJson { get; init; }
    public string? ErrorMessage { get; init; }
}

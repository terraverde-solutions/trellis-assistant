namespace Trellis.Assistant.Services;

/// <summary>
/// Input to <see cref="ISearchClient.SearchAsync"/>. Maps 1:1 to Trainer's
/// <c>GET /api/search</c> query-parameter surface. Built by
/// <see cref="AgentExecution.SearchDocumentsTool"/> from the LLM's
/// tool-call arguments.
///
/// <para>
/// Pass-through field semantics: omitted values (null / empty arrays)
/// are dropped from the outgoing URL so Trainer's defaults apply
/// (mode=vector, k=5, no since/document/content-type filters). The
/// <c>source=augmentation</c> tag is baked into the client, NOT carried
/// here — every Assistant-side search is audit-tagged as agent-driven.
/// </para>
/// </summary>
public sealed record SearchQuery
{
    /// <summary>The search query string. Required; non-empty.</summary>
    public required string Q { get; init; }

    /// <summary>
    /// Top-K results. <c>null</c> → Trainer default (5). Trainer's max is
    /// 50; values past that are rejected server-side as 400.
    /// </summary>
    public int? K { get; init; }

    /// <summary>
    /// Retrieval mode. <c>null</c> → Trainer default (<c>vector</c>).
    /// Hybrid is generally higher-quality for English natural-language
    /// queries (RRF over vector + lexical); lexical is the right pick
    /// when the caller wants exact-term match.
    /// </summary>
    public SearchMode? Mode { get; init; }

    /// <summary>
    /// ISO 8601 timestamp filter. <c>null</c> → no filter. When supplied,
    /// Trainer restricts to chunks whose parent document has
    /// <c>UpdatedAt &gt;= since</c>.
    /// </summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>
    /// Filter by document GUIDs. Empty/null → no filter. Each value
    /// becomes a repeated <c>documentId=</c> query parameter.
    /// </summary>
    public IReadOnlyList<Guid>? DocumentIds { get; init; }

    /// <summary>
    /// Filter by MIME types (e.g. <c>application/pdf</c>,
    /// <c>text/markdown</c>). Empty/null → no filter. Each value becomes
    /// a repeated <c>contentType=</c> query parameter; Trainer rejects
    /// unregistered MIME values as 400.
    /// </summary>
    public IReadOnlyList<string>? ContentTypes { get; init; }
}

/// <summary>
/// Trainer's three retrieval modes. Serialized to the wire as lowercase
/// (<c>vector</c>, <c>lexical</c>, <c>hybrid</c>) — matches Trainer's
/// <c>?mode=</c> query-string vocabulary.
/// </summary>
public enum SearchMode
{
    Vector,
    Lexical,
    Hybrid,
}

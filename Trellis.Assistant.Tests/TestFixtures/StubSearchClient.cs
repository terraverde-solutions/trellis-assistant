using Trellis.Assistant.Services;

namespace Trellis.Assistant.Tests.TestFixtures;

/// <summary>
/// Test double for <see cref="ISearchClient"/>. Returns a per-test
/// <see cref="NextResult"/> on every <see cref="SearchAsync"/> call and
/// records the last <see cref="SearchQuery"/> for assertions.
///
/// <para>
/// Added in PR #10 review Blocker 3 fix-up: the original PR's E2E
/// integration test claimed to exercise the LLM-emits-tool_call →
/// executor-dispatches → ISearchClient chain but never actually wired
/// a search-client stub into the factory — a real dispatch would have
/// tried to HTTP-round-trip to <c>http://test-host-unreachable:5114/</c>.
/// This stub closes the gap; <see cref="AssistantWebApplicationFactory"/>
/// registers it via <see cref="Microsoft.Extensions.DependencyInjection.IServiceCollection"/>
/// in place of the production <c>HttpSearchClient</c>, and the
/// <c>Func&lt;ISearchClient&gt;</c> factory wrapper resolves through DI
/// each per-call (captive-dep fix from PR #9 review).
/// </para>
/// </summary>
public sealed class StubSearchClient : ISearchClient
{
    /// <summary>
    /// What every <see cref="SearchAsync"/> call returns. Default is a
    /// success with an empty results array — most tests override per-call
    /// before dispatching.
    /// </summary>
    public SearchClientResult NextResult { get; set; } = new()
    {
        Success = true,
        ResponseBodyJson = "[]",
    };

    /// <summary>
    /// The most recent <see cref="SearchQuery"/> the stub received.
    /// Tests assert that the executor passed through the LLM-emitted
    /// query verbatim (boundary check for the
    /// LLM-args → MapToSearchQuery → ISearchClient hand-off).
    /// </summary>
    public SearchQuery? LastQuery { get; private set; }

    public Task<SearchClientResult> SearchAsync(
        SearchQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastQuery = query;
        return Task.FromResult(NextResult);
    }
}

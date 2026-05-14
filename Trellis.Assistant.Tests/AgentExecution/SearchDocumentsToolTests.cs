using FluentAssertions;
using Trellis.Assistant.AgentExecution;
using Trellis.Assistant.Services;
using Trellis.Core.Models;
using Xunit;

namespace Trellis.Assistant.Tests.AgentExecution;

/// <summary>
/// Pins for <see cref="SearchDocumentsTool"/> — Phase 3.B's first real
/// tool. Covers descriptor shape, <see cref="SearchDocumentsTool.MapToSearchQuery"/>
/// boundary mapping, and <see cref="SearchDocumentsTool.RunAsync"/> happy +
/// failure paths against a stub <see cref="ISearchClient"/>.
///
/// <para>
/// Pure unit tests — no DB, no HTTP. The stub client lets us pin tool
/// behavior in isolation; the actual Trainer round-trip is covered by
/// <see cref="Services.HttpSearchClientTests"/> separately.
/// </para>
/// </summary>
public sealed class SearchDocumentsToolTests
{
    [Fact]
    public void Descriptor_HasExpectedShape()
    {
        var tool = new SearchDocumentsTool(() => new StubSearchClient());
        tool.Descriptor.Name.Should().Be("search_documents");
        tool.Descriptor.Category.Should().Be(AgentToolCategory.Search);
        tool.Descriptor.Description.Should().Contain("indexed document corpus");
        tool.Descriptor.ParameterSchema.Should().Contain(@"""required"":");
        tool.Descriptor.ParameterSchema.Should().Contain("query");
    }

    [Fact]
    public void Descriptor_ParameterSchema_IsValidJsonSchema()
    {
        var tool = new SearchDocumentsTool(() => new StubSearchClient());
        var validator = new JsonSchemaNetValidator();
        var act = () => validator.EnsureValidSchema(tool.Descriptor.ParameterSchema);
        act.Should().NotThrow(
            "descriptor's ParameterSchema must parse — startup ToolRegistry validation pins this and the registry would crash the host otherwise");
    }

    [Fact]
    public void MapToSearchQuery_QueryOnly_PropagatesQOnly()
    {
        var q = SearchDocumentsTool.MapToSearchQuery(new SearchDocumentsTool.ToolArgs { Query = "refunds" });
        q.Q.Should().Be("refunds");
        q.K.Should().BeNull();
        q.Mode.Should().BeNull();
        q.Since.Should().BeNull();
        q.DocumentIds.Should().BeNull();
        q.ContentTypes.Should().BeNull();
    }

    [Fact]
    public void MapToSearchQuery_AllFieldsSet_TranslatesEverything()
    {
        var ids = new[] { Guid.NewGuid() };
        var types = new[] { "application/pdf" };
        var args = new SearchDocumentsTool.ToolArgs
        {
            Query = "refunds",
            TopK = 10,
            Mode = "hybrid",
            Since = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Filter = new SearchDocumentsTool.ToolFilter { DocumentIds = ids, ContentTypes = types },
        };
        var q = SearchDocumentsTool.MapToSearchQuery(args);
        q.Q.Should().Be("refunds");
        q.K.Should().Be(10);
        q.Mode.Should().Be(SearchMode.Hybrid);
        q.Since.Should().Be(args.Since);
        q.DocumentIds.Should().BeEquivalentTo(ids);
        q.ContentTypes.Should().BeEquivalentTo(types);
    }

    [Theory]
    [InlineData("vector", SearchMode.Vector)]
    [InlineData("lexical", SearchMode.Lexical)]
    [InlineData("hybrid", SearchMode.Hybrid)]
    public void MapToSearchQuery_MapsAllThreeWireModes(string wire, SearchMode expected)
    {
        var q = SearchDocumentsTool.MapToSearchQuery(new SearchDocumentsTool.ToolArgs
        {
            Query = "x",
            Mode = wire,
        });
        q.Mode.Should().Be(expected);
    }

    [Fact]
    public async Task RunAsync_HappyPath_ReturnsSuccessWithRawBodyAsResultJson()
    {
        const string body = """[{"DocumentId":"abc","Score":0.9}]""";
        var stub = new StubSearchClient
        {
            Result = new SearchClientResult { Success = true, ResponseBodyJson = body },
        };
        var tool = new SearchDocumentsTool(() => stub);
        var output = await tool.RunAsync(NewInput("""{"query":"refunds"}"""));
        output.Success.Should().BeTrue();
        output.ResultJson.Should().Be(body,
            "pass-through (a): the tool returns Trainer's body bytes verbatim as ResultJson");
        output.ErrorMessage.Should().BeNull();
        stub.LastQuery.Should().NotBeNull();
        stub.LastQuery!.Q.Should().Be("refunds");
    }

    [Fact]
    public async Task RunAsync_EmptyArrayBody_ReturnsSuccessWithEmptyArray()
    {
        var stub = new StubSearchClient
        {
            Result = new SearchClientResult { Success = true, ResponseBodyJson = "[]" },
        };
        var tool = new SearchDocumentsTool(() => stub);
        var output = await tool.RunAsync(NewInput("""{"query":"unmatched"}"""));
        output.Success.Should().BeTrue();
        output.ResultJson.Should().Be("[]",
            "decide-and-document #3: empty corpus surfaces as Success=true with empty array; LLM interprets 'no documents matched'");
    }

    [Fact]
    public async Task RunAsync_ClientFailure_MapsToToolFailureWithClientErrorMessage()
    {
        var stub = new StubSearchClient
        {
            Result = new SearchClientResult { Success = false, ErrorMessage = "search: trainer returned 400 — q must be non-empty" },
        };
        var tool = new SearchDocumentsTool(() => stub);
        var output = await tool.RunAsync(NewInput("""{"query":"refunds"}"""));
        output.Success.Should().BeFalse();
        output.ResultJson.Should().BeNull();
        output.ErrorMessage.Should().Be("search: trainer returned 400 — q must be non-empty");
    }

    [Fact]
    public async Task RunAsync_UnparseableArgsJson_ReturnsStructuredFailure()
    {
        // Schema validation happens upstack in the executor; this test
        // pins the tool's defensive parse failure handling in case it's
        // ever called outside the executor (or with a schema-mismatched
        // payload due to library/schema drift).
        var tool = new SearchDocumentsTool(() => new StubSearchClient());
        var output = await tool.RunAsync(NewInput("{this is not json"));
        output.Success.Should().BeFalse();
        output.ErrorMessage.Should().Contain("failed to parse arguments JSON");
    }

    [Fact]
    public async Task RunAsync_MissingQuery_ReturnsStructuredFailure()
    {
        // Schema validation upstack would reject this; the tool's
        // defensive check is belt-and-suspenders.
        var tool = new SearchDocumentsTool(() => new StubSearchClient());
        var output = await tool.RunAsync(NewInput("""{}"""));
        output.Success.Should().BeFalse();
        output.ErrorMessage.Should().Contain("'query' was missing or empty");
    }

    [Fact]
    public async Task RunAsync_CancellationRequested_Throws()
    {
        var tool = new SearchDocumentsTool(() => new StubSearchClient());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = () => tool.RunAsync(NewInput("""{"query":"x"}"""), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static AgentToolInput NewInput(string parametersJson) => new()
    {
        AgentRunId = Guid.NewGuid(),
        StepIndex = 0,
        ParametersJson = parametersJson,
        OrgId = Guid.NewGuid(),
        UserId = "test-user",  // Phase 3.D widening; SearchDocumentsTool ignores user scope (loopback-trust Trainer is single-tenant) but the field is required.
    };

    private sealed class StubSearchClient : ISearchClient
    {
        public SearchClientResult Result { get; set; } = new()
        {
            Success = true,
            ResponseBodyJson = "[]",
        };

        public SearchQuery? LastQuery { get; private set; }

        public Task<SearchClientResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return Task.FromResult(Result);
        }
    }
}

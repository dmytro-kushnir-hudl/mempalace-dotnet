using Mempalace.IntegrationTests.Harness;

namespace Mempalace.IntegrationTests.Tests;

/// <summary>Group 3 — Semantic search.</summary>
[Collection("MCP")]
public sealed class T13_T20_SearchTests(EmbedderFixture embedder) : IAsyncLifetime, IDisposable
{
    private readonly PalaceFactory _factory = new(embedder.Embedder);
    private McpToolContext _chromaCtx = null!;
    private McpToolContext _sqliteCtx = null!;

    public async ValueTask InitializeAsync()
    {
        (_sqliteCtx, _) = _factory.CreateContext();
        (_chromaCtx, _) = _factory.CreateContext(VectorBackend.Chroma);
        await Seed.ApplyAsync(_sqliteCtx);
        await Seed.ApplyAsync(_chromaCtx);
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T13_Search_JwtQuery_ReturnsRelevantResult(VectorBackend backend)
    {
        var ctx = backend == VectorBackend.Sqlite ? _sqliteCtx : _chromaCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_search", new { query = "JWT authentication tokens" }));

        var results = s.Result(2)["results"]!.AsArray();
        Assert.NotEmpty(results);
        var top = results[0]!;
        Assert.True(top["similarity"]!.GetValue<double>() > 0);
        var text = top["text"]!.GetValue<string>().ToLowerInvariant();
        Assert.True(text.Contains("jwt") || text.Contains("auth") || text.Contains("token"));
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T14_Search_PostgresQuery_SortedBySimilarity(VectorBackend backend)
    {
        var ctx = backend == VectorBackend.Sqlite ? _sqliteCtx : _chromaCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_search", new { query = "PostgreSQL migration database" }));

        var results = s.Result(2)["results"]!.AsArray();
        Assert.NotEmpty(results);

        // Results sorted descending by similarity
        var sims = results.Select(r => r!["similarity"]!.GetValue<double>()).ToList();
        for (var i = 1; i < sims.Count; i++)
            Assert.True(sims[i - 1] >= sims[i], "Results not sorted descending");

        var topText = results[0]!["text"]!.GetValue<string>().ToLowerInvariant();
        Assert.True(topText.Contains("postgresql") || topText.Contains("migration") || topText.Contains("mysql"));
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T15_Search_WingFilter_OnlyFrontendResults(VectorBackend backend)
    {
        var ctx = backend == VectorBackend.Sqlite ? _sqliteCtx : _chromaCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_search", new { query = "auth", wing = "frontend" }));

        var results = s.Result(2)["results"]!.AsArray();
        Assert.NotEmpty(results);
        foreach (var r in results)
            Assert.Equal("frontend", r!["wing"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T16_Search_RoomFilter_OnlyDatabaseResults(VectorBackend backend)
    {
        var ctx = backend == VectorBackend.Sqlite ? _sqliteCtx : _chromaCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_search", new { query = "auth", room = "database" }));

        var results = s.Result(2)["results"]!.AsArray();
        foreach (var r in results)
            Assert.Equal("database", r!["room"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T17_Search_WingAndRoomFilter_OnlyBackendAuth(VectorBackend backend)
    {
        var ctx = backend == VectorBackend.Sqlite ? _sqliteCtx : _chromaCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_search", new { query = "decisions", wing = "backend", room = "auth" }));

        var results = s.Result(2)["results"]!.AsArray();
        foreach (var r in results)
        {
            Assert.Equal("backend", r!["wing"]!.GetValue<string>());
            Assert.Equal("auth", r!["room"]!.GetValue<string>());
        }
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T18_Search_Limit2_AtMost2Results(VectorBackend backend)
    {
        var ctx = backend == VectorBackend.Sqlite ? _sqliteCtx : _chromaCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_search", new { query = "auth", limit = 2 }));

        var results = s.Result(2)["results"]!.AsArray();
        Assert.True(results.Count <= 2);
    }

    [Fact]
    public async Task T19_Search_EmptyPalace_ReturnsEmpty()
    {
        var (ctx, _) = _factory.CreateContext();
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_search", new { query = "anything" }));

        var results = s.Result(2)["results"]!.AsArray();
        Assert.Empty(results);
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T20_Search_NonexistentWing_ReturnsEmpty(VectorBackend backend)
    {
        var ctx = backend == VectorBackend.Sqlite ? _sqliteCtx : _chromaCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_search", new { query = "anything", wing = "does_not_exist" }));

        Assert.False(s.ToolError(2));
        var results = s.Result(2)["results"]!.AsArray();
        Assert.Empty(results);
    }
}
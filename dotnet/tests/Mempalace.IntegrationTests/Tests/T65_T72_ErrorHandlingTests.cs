using Mempalace.IntegrationTests.Harness;

namespace Mempalace.IntegrationTests.Tests;

/// <summary>Group 10 — Error handling and edge cases.</summary>
[Collection("MCP")]
public sealed class T65_T72_ErrorHandlingTests(EmbedderFixture embedder) : IDisposable
{
    private readonly PalaceFactory _factory = new(embedder.Embedder);

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T65_Search_MissingQuery_ReturnsError(VectorBackend backend)
    {
        var (ctx, _) = _factory.CreateContext(backend);
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_search", new { }));

        Assert.True(s.ToolError(2));
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T66_DeleteNonexistent_ReturnsFalseNotCrash(VectorBackend backend)
    {
        var (ctx, _) = _factory.CreateContext(backend);
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_delete_drawer",
                new { drawer_id = "drawer_nonexistent_id_xyz" }));

        Assert.False(s.Result(2)["success"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T67_TraverseGraph_EmptyPalace_NoCrash(VectorBackend backend)
    {
        var (ctx, _) = _factory.CreateContext(backend);
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_traverse_graph",
                new { start_room = "auth", max_hops = 1 }));

        // Either error field or empty nodes — no crash (no exception thrown)
        var r = s.Result(2);
        Assert.True(r["error"] is not null ||
                    (r["nodes"] is { } n && n.AsArray().Count == 0));
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T68_Search_Limit0_ReturnsEmpty(VectorBackend backend)
    {
        var (ctx, _) = _factory.CreateContext(backend);
        await McpHarness.SessionAsync(ctx, // seed one drawer
            McpHarness.Call(2, "mempalace_add_drawer",
                new { wing = "test", room = "r", content = "limit zero test content" }));

        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_search",
                new { query = "limit zero test", limit = 0 }));

        Assert.Empty(s.Result(2)["Results"]!.AsArray());
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T69_KgQuery_UnknownEntity_ReturnsEmptyTriples(VectorBackend backend)
    {
        var (ctx, _) = _factory.CreateContext(backend);
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_kg_query",
                new { entity = "nobody_xyz_unknown" }));

        Assert.False(s.ToolError(2));
        Assert.Empty(s.Result(2)["triples"]!.AsArray());
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T70_AddDrawer_VeryLongContent_Succeeds(VectorBackend backend)
    {
        var (ctx, _) = _factory.CreateContext(backend);
        var longContent = new string('a', 10_000);
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_add_drawer",
                new { wing = "test", room = "long", content = longContent }));

        Assert.True(s.Result(2)["success"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T71_AddThenImmediatelySearch_FindsNewDrawer(VectorBackend backend)
    {
        var (ctx, _) = _factory.CreateContext(backend);
        var unique = $"uniqueXyzzy_{Guid.NewGuid():N}";
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_add_drawer",
                new { wing = "test", room = "r", content = $"Unique content marker: {unique}" }),
            McpHarness.Call(3, "mempalace_search",
                new { query = unique }));

        var results = s.Result(3)["Results"]!.AsArray();
        Assert.Contains(results, r => r!["Text"]!.GetValue<string>().Contains(unique));
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T72_TwoSessions_SharedPalace_SecondSeesFirst(VectorBackend backend)
    {
        var (ctx, palacePath) = _factory.CreateContext(backend);

        // Session A: add drawer
        var unique = $"sharedXyzzy_{Guid.NewGuid():N}";
        await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_add_drawer",
                new { wing = "test", room = "shared", content = $"Shared content: {unique}" }));

        // Session B: fresh context pointing to same palace
        var ctxB = new McpToolContext(palacePath, Constants.DefaultCollectionName,
            Path.Combine(palacePath, "kg.db"), embedder.Embedder, backend);
        var sB = await McpHarness.SessionAsync(ctxB,
            McpHarness.Call(2, "mempalace_search", new { query = unique }));

        var results = sB.Result(2)["Results"]!.AsArray();
        Assert.Contains(results, r => r!["Text"]!.GetValue<string>().Contains(unique));
    }

    public void Dispose() => _factory.Dispose();
}

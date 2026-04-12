using Mempalace.IntegrationTests.Harness;

namespace Mempalace.IntegrationTests.Tests;

/// <summary>Group 6 — Graph traversal.</summary>
[Collection("MCP")]
public sealed class T31_T38_GraphTests(EmbedderFixture embedder) : IAsyncLifetime, IDisposable
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
    public async Task T31_GraphStats_CorrectCounts(VectorBackend backend)
    {
        var ctx = backend == VectorBackend.Sqlite ? _sqliteCtx : _chromaCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_graph_stats", new { }));

        var r = s.Result(2);
        Assert.True(r["totalRooms"]!.GetValue<int>() >= 3);
        Assert.Equal(1, r["tunnelRooms"]!.GetValue<int>());
        Assert.Equal(2, r["roomsPerWing"]!["backend"]!.GetValue<int>());
        Assert.Equal(2, r["roomsPerWing"]!["frontend"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T32_FindTunnels_NoFilter_AuthBridgesWings(VectorBackend backend)
    {
        var ctx = backend == VectorBackend.Sqlite ? _sqliteCtx : _chromaCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_find_tunnels", new { }));

        var tunnels = s.Result(2)["tunnels"]!.AsArray();
        Assert.NotEmpty(tunnels);
        var authTunnel = tunnels.FirstOrDefault(t => t!["Room"]?.GetValue<string>() == "auth");
        Assert.NotNull(authTunnel);
        var wings = authTunnel!["Wings"]!.AsArray().Select(w => w!.GetValue<string>()).ToList();
        Assert.Contains("backend", wings);
        Assert.Contains("frontend", wings);
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T33_FindTunnels_WingAFilter_AllContainBackend(VectorBackend backend)
    {
        var ctx = backend == VectorBackend.Sqlite ? _sqliteCtx : _chromaCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_find_tunnels", new { wing_a = "backend" }));

        var tunnels = s.Result(2)["tunnels"]!.AsArray();
        foreach (var t in tunnels)
        {
            var wings = t!["Wings"]!.AsArray().Select(w => w!.GetValue<string>()).ToList();
            Assert.Contains("backend", wings);
        }
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T34_FindTunnels_BothWings_OnlyAuth(VectorBackend backend)
    {
        var ctx = backend == VectorBackend.Sqlite ? _sqliteCtx : _chromaCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_find_tunnels",
                new { wing_a = "backend", wing_b = "frontend" }));

        var tunnels = s.Result(2)["tunnels"]!.AsArray();
        Assert.Single(tunnels);
        Assert.Equal("auth", tunnels[0]!["Room"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T35_FindTunnels_NonexistentWing_Empty(VectorBackend backend)
    {
        var ctx = backend == VectorBackend.Sqlite ? _sqliteCtx : _chromaCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_find_tunnels",
                new { wing_a = "backend", wing_b = "nonexistent" }));

        Assert.Empty(s.Result(2)["tunnels"]!.AsArray());
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T36_TraverseGraph_AuthRoom_Hop0ThenHop1(VectorBackend backend)
    {
        var ctx = backend == VectorBackend.Sqlite ? _sqliteCtx : _chromaCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_traverse_graph",
                new { start_room = "auth", max_hops = 1 }));

        var nodes = s.Result(2).AsArray();
        Assert.NotEmpty(nodes);
        var first = nodes[0]!;
        Assert.Equal("auth", first["room"]!.GetValue<string>());
        Assert.Equal(0, first["hop"]!.GetValue<int>());
        Assert.True(nodes.Count > 1, "Expected hop-1 nodes");
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T37_TraverseGraph_TypoStartRoom_SuggestsAuth(VectorBackend backend)
    {
        var ctx = backend == VectorBackend.Sqlite ? _sqliteCtx : _chromaCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_traverse_graph",
                new { start_room = "autth", max_hops = 1 }));

        var r = s.Result(2);
        Assert.NotNull(r["error"]);
        var suggestions = r["suggestions"]!.AsArray()
            .Select(x => x!.GetValue<string>()).ToList();
        Assert.Contains("auth", suggestions);
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T38_TraverseGraph_MaxHops0_ExactlyOneNode(VectorBackend backend)
    {
        var ctx = backend == VectorBackend.Sqlite ? _sqliteCtx : _chromaCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_traverse_graph",
                new { start_room = "auth", max_hops = 0 }));

        Assert.Single(s.Result(2).AsArray());
    }
}
using Mempalace.IntegrationTests.Harness;

namespace Mempalace.IntegrationTests.Tests;

/// <summary>Group 6 — Graph traversal.</summary>
[Collection("MCP")]
public sealed class T31_T38_GraphTests(EmbedderFixture embedder) : IAsyncLifetime, IDisposable
{
    private readonly PalaceFactory _factory = new(embedder.Embedder);
    private McpToolContext _sqliteCtx = null!;

    public async ValueTask InitializeAsync()
    {
        (_sqliteCtx, _) = _factory.CreateContext();
        await Seed.ApplyAsync(_sqliteCtx);
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    [Fact]
    public async Task T31_GraphStats_CorrectCounts()
    {
        var ctx = _sqliteCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_graph_stats", new { }));

        var r = s.Result(2);
        Assert.True(r["totalRooms"]!.GetValue<int>() >= 3);
        Assert.Equal(1, r["tunnelRooms"]!.GetValue<int>());
        Assert.Equal(2, r["roomsPerWing"]!["backend"]!.GetValue<int>());
        Assert.Equal(2, r["roomsPerWing"]!["frontend"]!.GetValue<int>());
    }

    [Fact]
    public async Task T32_FindTunnels_NoFilter_AuthBridgesWings()
    {
        var ctx = _sqliteCtx;
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

    [Fact]
    public async Task T33_FindTunnels_WingAFilter_AllContainBackend()
    {
        var ctx = _sqliteCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_find_tunnels", new { wing_a = "backend" }));

        var tunnels = s.Result(2)["tunnels"]!.AsArray();
        foreach (var t in tunnels)
        {
            var wings = t!["Wings"]!.AsArray().Select(w => w!.GetValue<string>()).ToList();
            Assert.Contains("backend", wings);
        }
    }

    [Fact]
    public async Task T34_FindTunnels_BothWings_OnlyAuth()
    {
        var ctx = _sqliteCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_find_tunnels",
                new { wing_a = "backend", wing_b = "frontend" }));

        var tunnels = s.Result(2)["tunnels"]!.AsArray();
        Assert.Single(tunnels);
        Assert.Equal("auth", tunnels[0]!["Room"]!.GetValue<string>());
    }

    [Fact]
    public async Task T35_FindTunnels_NonexistentWing_Empty()
    {
        var ctx = _sqliteCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_find_tunnels",
                new { wing_a = "backend", wing_b = "nonexistent" }));

        Assert.Empty(s.Result(2)["tunnels"]!.AsArray());
    }

    [Fact]
    public async Task T36_TraverseGraph_AuthRoom_Hop0ThenHop1()
    {
        var ctx = _sqliteCtx;
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

    [Fact]
    public async Task T37_TraverseGraph_TypoStartRoom_SuggestsAuth()
    {
        var ctx = _sqliteCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_traverse_graph",
                new { start_room = "autth", max_hops = 1 }));

        var r = s.Result(2);
        Assert.NotNull(r["error"]);
        var suggestions = r["suggestions"]!.AsArray()
            .Select(x => x!.GetValue<string>()).ToList();
        Assert.Contains("auth", suggestions);
    }

    [Fact]
    public async Task T38_TraverseGraph_MaxHops0_ExactlyOneNode()
    {
        var ctx = _sqliteCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_traverse_graph",
                new { start_room = "auth", max_hops = 0 }));

        Assert.Single(s.Result(2).AsArray());
    }
}
using Mempalace.IntegrationTests.Harness;

namespace Mempalace.IntegrationTests.Tests;

/// <summary>Group 2 — Status and exploration.</summary>
[Collection("MCP")]
public sealed class T06_T12_StatusTests(EmbedderFixture embedder) : IAsyncLifetime, IDisposable
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

    [Fact]
    public async Task T06_Status_EmptyPalace_HasRequiredKeys()
    {
        var (ctx, _) = _factory.CreateContext();
        var session = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_status", new { }));

        var r = session.Result(2);
        Assert.NotNull(r["total_drawers"]);
        Assert.NotNull(r["wings"]);
        Assert.NotNull(r["palace_path"]);
        Assert.NotNull(r["protocol"]);
        Assert.NotNull(r["aaak_dialect"]);
        Assert.Equal(0, r["total_drawers"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T07_Status_AfterSeeding_Shows6Drawers(VectorBackend backend)
    {
        var ctx = backend == VectorBackend.Sqlite ? _sqliteCtx : _chromaCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_status", new { }));

        var r = s.Result(2);
        Assert.Equal(6, r["total_drawers"]!.GetValue<int>());
        Assert.NotNull(r["wings"]!["backend"]);
        Assert.NotNull(r["wings"]!["frontend"]);
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T08_ListWings_BackendAndFrontend(VectorBackend backend)
    {
        var ctx = backend == VectorBackend.Sqlite ? _sqliteCtx : _chromaCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_list_wings", new { }));

        var wings = s.Result(2)["wings"]!;
        Assert.NotNull(wings["backend"]);
        Assert.NotNull(wings["frontend"]);
        Assert.True(wings["backend"]!.GetValue<int>() >= wings["frontend"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T09_ListRooms_NoFilter_ShowsAllRooms(VectorBackend backend)
    {
        var ctx = backend == VectorBackend.Sqlite ? _sqliteCtx : _chromaCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_list_rooms", new { }));

        var rooms = s.Result(2)["rooms"]!;
        Assert.NotNull(rooms["auth"]);
        Assert.NotNull(rooms["database"]);
        Assert.NotNull(rooms["components"]);
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T10_ListRooms_BackendWing_ExcludesComponents(VectorBackend backend)
    {
        var ctx = backend == VectorBackend.Sqlite ? _sqliteCtx : _chromaCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_list_rooms", new { wing = "backend" }));

        var rooms = s.Result(2)["rooms"]!;
        Assert.NotNull(rooms["auth"]);
        Assert.NotNull(rooms["database"]);
        Assert.Null(rooms["components"]);
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T11_GetTaxonomy_CorrectCounts(VectorBackend backend)
    {
        var ctx = backend == VectorBackend.Sqlite ? _sqliteCtx : _chromaCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_get_taxonomy", new { }));

        var tax = s.Result(2)["taxonomy"]!;
        Assert.Equal(3, tax["backend"]!["auth"]!.GetValue<int>());
        Assert.Equal(1, tax["backend"]!["database"]!.GetValue<int>());
        Assert.Equal(1, tax["frontend"]!["auth"]!.GetValue<int>());
        Assert.Equal(1, tax["frontend"]!["components"]!.GetValue<int>());
    }

    [Fact]
    public async Task T12_GetAaakSpec_HasSpecAndProtocol()
    {
        var s = await McpHarness.SessionAsync(_sqliteCtx,
            McpHarness.Call(2, "mempalace_get_aaak_spec", new { }));

        var r = s.Result(2);
        var spec = r["spec"]!.GetValue<string>();
        Assert.False(string.IsNullOrEmpty(spec));
        Assert.Contains("AAAK", spec);
        Assert.False(string.IsNullOrEmpty(r["protocol"]!.GetValue<string>()));
    }
}
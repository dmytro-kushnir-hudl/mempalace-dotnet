using Mempalace.IntegrationTests.Harness;

namespace Mempalace.IntegrationTests.Tests;

/// <summary>Groups 4 + 5 — Deduplication and drawer write/delete.</summary>
[Collection("MCP")]
public sealed class T21_T30_DrawerTests(EmbedderFixture embedder) : IAsyncLifetime, IDisposable
{
    private const string ExactContent =
        "We decided to use JWT tokens with RS256 signing because it allows stateless verification across microservices. The secret is stored in Vault.";

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

    // ── Deduplication ──────────────────────────────────────────────────────────

    [Fact]
    public async Task T21_CheckDuplicate_ExactMatch_IsTrue()
    {
        var ctx = _sqliteCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_check_duplicate",
                new { content = ExactContent, threshold = 0.95 }));

        var r = s.Result(2);
        Assert.True(r["is_duplicate"]!.GetValue<bool>());
        Assert.NotEmpty(r["matches"]!.AsArray());
    }

    [Fact]
    public async Task T22_CheckDuplicate_LowThreshold_SimilarContentMatches()
    {
        var ctx = _sqliteCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_check_duplicate",
                new { content = "JWT token auth", threshold = 0.5 }));

        Assert.True(s.Result(2)["is_duplicate"]!.GetValue<bool>());
    }

    [Fact]
    public async Task T23_CheckDuplicate_UnrelatedContent_NotDuplicate()
    {
        var ctx = _sqliteCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_check_duplicate",
                new { content = "completely unrelated topic about baking bread", threshold = 0.99 }));

        Assert.False(s.Result(2)["is_duplicate"]!.GetValue<bool>());
    }

    // ── Write / Delete ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    public async Task T24_AddDrawer_ReturnsIdWithPrefix(VectorBackend backend)
    {
        var (ctx, _) = _factory.CreateContext(backend);
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_add_drawer",
                new { wing = "test", room = "regression", content = "Regression test drawer added by T24" }));

        var r = s.Result(2);
        Assert.True(r["success"]!.GetValue<bool>());
        Assert.StartsWith("drawer_test_regression_", r["drawer_id"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    public async Task T25_AddedDrawer_IsSearchable(VectorBackend backend)
    {
        var (ctx, _) = _factory.CreateContext(backend);
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_add_drawer",
                new { wing = "test", room = "regression", content = "Regression test drawer added by T24" }),
            McpHarness.Call(3, "mempalace_search",
                new { query = "regression test drawer" }));

        var testId = s.Result(2)["drawer_id"]!.GetValue<string>();
        var results = s.Result(3)["results"]!.AsArray();
        Assert.Contains(results, r => r!["text"]!.GetValue<string>().Contains("Regression test drawer"));
        _ = testId; // used implicitly via search content
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    public async Task T26_AddDrawer_Idempotent_SameIdReturned(VectorBackend backend)
    {
        var (ctx, _) = _factory.CreateContext(backend);
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_add_drawer",
                new { wing = "test", room = "regression", content = "idempotent content" }),
            McpHarness.Call(3, "mempalace_add_drawer",
                new { wing = "test", room = "regression", content = "idempotent content" }));

        Assert.Equal(
            s.Result(2)["drawer_id"]!.GetValue<string>(),
            s.Result(3)["drawer_id"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    public async Task T27_AddDrawer_EmptyWing_ReturnsError(VectorBackend backend)
    {
        var (ctx, _) = _factory.CreateContext(backend);
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_add_drawer",
                new { wing = "", room = "r", content = "x" }));

        var r = s.Result(2);
        var success = r["success"]?.GetValue<bool>();
        Assert.True(success == false || r["error"] is not null);
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    public async Task T28_T29_DeleteDrawer_ThenNotSearchable(VectorBackend backend)
    {
        var (ctx, _) = _factory.CreateContext(backend);
        var s1 = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_add_drawer",
                new { wing = "test", room = "del", content = "unique content for deletion test xyzzy" }));

        var drawerId = s1.Result(2)["drawer_id"]!.GetValue<string>();

        var s2 = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_delete_drawer", new { drawer_id = drawerId }),
            McpHarness.Call(3, "mempalace_search", new { query = "unique content xyzzy" }));

        Assert.True(s2.Result(2)["success"]!.GetValue<bool>());

        var results = s2.Result(3)["results"]!.AsArray();
        Assert.DoesNotContain(results, r => r!["text"]!.GetValue<string>().Contains("xyzzy"));
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    public async Task T30_DeleteDrawer_NotFound_ReturnsError(VectorBackend backend)
    {
        var (ctx, _) = _factory.CreateContext(backend);
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_delete_drawer",
                new { drawer_id = "drawer_nonexistent_id_xyz" }));

        var r = s.Result(2);
        Assert.False(r["success"]!.GetValue<bool>());
        Assert.NotNull(r["error"]);
    }
}
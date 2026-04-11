using Mempalace.IntegrationTests.Harness;

namespace Mempalace.IntegrationTests.Tests;

/// <summary>Group 8 — Diary.</summary>
[Collection("MCP")]
public sealed class T49_T52_DiaryTests(EmbedderFixture embedder) : IDisposable
{
    private readonly PalaceFactory _factory = new(embedder.Embedder);

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T49_DiaryWrite_ReturnsEntryIdWithPrefix(VectorBackend backend)
    {
        var (ctx, _) = _factory.CreateContext(backend);
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_diary_write",
                new { agent_name = "test-agent", entry = "0:???|regression_test|\"ran all 22 tools today\"|determ|DECISION", topic = "regression" }));

        var r = s.Result(2);
        Assert.True(r["success"]!.GetValue<bool>());
        Assert.StartsWith("diary_wing_test-agent_", r["entry_id"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T50_DiaryRead_ReturnsEntryWithTopic(VectorBackend backend)
    {
        var (ctx, _) = _factory.CreateContext(backend);
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_diary_write",
                new { agent_name = "test-agent", entry = "test entry", topic = "regression" }),
            McpHarness.Call(3, "mempalace_diary_read",
                new { agent_name = "test-agent", last_n = 5 }));

        var entries = s.Result(3)["entries"]!.AsArray();
        Assert.NotEmpty(entries);
        Assert.Contains(entries, e => e!["topic"]!.GetValue<string>() == "regression");

        // Ordered by timestamp descending
        var timestamps = entries.Select(e => e!["timestamp"]!.GetValue<string>()).ToList();
        for (int i = 1; i < timestamps.Count; i++)
            Assert.True(string.Compare(timestamps[i - 1], timestamps[i], StringComparison.Ordinal) >= 0);
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T51_DiaryRead_UnknownAgent_ReturnsEmpty(VectorBackend backend)
    {
        var (ctx, _) = _factory.CreateContext(backend);
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_diary_read",
                new { agent_name = "nonexistent-agent-xyz" }));

        Assert.False(s.ToolError(2));
        Assert.Empty(s.Result(2)["entries"]!.AsArray());
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T52_DiaryRead_LastN_RespectsLimit(VectorBackend backend)
    {
        var (ctx, _) = _factory.CreateContext(backend);

        // Write 4 entries, read back 2
        var lines = new List<string>();
        for (int i = 0; i < 4; i++)
            lines.Add(McpHarness.Call(i + 2, "mempalace_diary_write",
                new { agent_name = "test-agent", entry = $"entry {i}", topic = "test" }));
        lines.Add(McpHarness.Call(6, "mempalace_diary_read",
            new { agent_name = "test-agent", last_n = 2 }));

        var s = await McpHarness.SessionAsync(ctx, [.. lines]);
        Assert.Equal(2, s.Result(6)["entries"]!.AsArray().Count);
    }

    public void Dispose() => _factory.Dispose();
}

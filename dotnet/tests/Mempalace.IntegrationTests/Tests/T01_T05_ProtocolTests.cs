using Mempalace.IntegrationTests.Harness;

namespace Mempalace.IntegrationTests.Tests;

/// <summary>Group 1 — Protocol and introspection (backend-independent, Sqlite only).</summary>
[Collection("MCP")]
public sealed class T01_T05_ProtocolTests(EmbedderFixture embedder) : IDisposable
{
    private readonly PalaceFactory _factory = new(embedder.Embedder);

    public void Dispose()
    {
        _factory.Dispose();
    }

    [Fact]
    public async Task T01_Initialize_Handshake()
    {
        var (ctx, _) = _factory.CreateContext();
        var session = await McpHarness.RunAsync(ctx, [McpHarness.Initialize()]);

        var result = session.Response(1)["result"]!;
        Assert.Equal("2025-11-25", result["protocolVersion"]!.GetValue<string>());
        Assert.Equal("mempalace", result["serverInfo"]!["name"]!.GetValue<string>());
        Assert.NotNull(result["capabilities"]!["tools"]);
    }

    [Fact]
    public async Task T02_ToolsList_Returns22Tools_NoUnicodeEscapes()
    {
        var (ctx, _) = _factory.CreateContext();
        var session = await McpHarness.RunAsync(ctx, [
            McpHarness.Initialize(), McpHarness.Initialized(),
            McpHarness.ToolsList(2)
        ]);

        var tools = session.Response(2)["result"]!["tools"]!.AsArray();
        Assert.Equal(22, tools.Count);

        var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToHashSet();
        string[] expected =
        [
            "mempalace_status", "mempalace_list_wings", "mempalace_list_rooms",
            "mempalace_get_taxonomy", "mempalace_get_aaak_spec", "mempalace_search",
            "mempalace_check_duplicate", "mempalace_traverse_graph", "mempalace_find_tunnels",
            "mempalace_graph_stats", "mempalace_add_drawer", "mempalace_delete_drawer",
            "mempalace_kg_query", "mempalace_kg_add", "mempalace_kg_invalidate",
            "mempalace_kg_timeline", "mempalace_kg_stats", "mempalace_diary_write",
            "mempalace_diary_read", "mempalace_extract_memories", "mempalace_detect_entities",
            "mempalace_compress"
        ];
        foreach (var name in expected)
            Assert.Contains(name, names);

        // No \u0022 encoding regression
        var raw = session.Response(2).ToJsonString();
        Assert.DoesNotContain(@"\u0022", raw);
    }

    [Fact]
    public async Task T03_Ping_ReturnsEmptyResult()
    {
        var (ctx, _) = _factory.CreateContext();
        var session = await McpHarness.RunAsync(ctx, [McpHarness.Ping(1)]);

        var result = session.Response(1)["result"]!;
        Assert.Equal("{}", result.ToJsonString());
    }

    [Fact]
    public async Task T04_UnknownMethod_ReturnsMethodNotFound()
    {
        var (ctx, _) = _factory.CreateContext();
        var session = await McpHarness.RunAsync(ctx, [McpHarness.UnknownMethod(1)]);

        var error = session.Response(1)["error"]!;
        Assert.Equal(-32601, error["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task T05_MalformedJson_ReturnsParseError()
    {
        var (ctx, _) = _factory.CreateContext();
        var session = await McpHarness.RunAsync(ctx, [McpHarness.MalformedJson()]);

        var parseError = session.NullIdResponses
            .FirstOrDefault(n => n["error"]?["code"]?.GetValue<int>() == -32700);
        Assert.NotNull(parseError);
    }
}
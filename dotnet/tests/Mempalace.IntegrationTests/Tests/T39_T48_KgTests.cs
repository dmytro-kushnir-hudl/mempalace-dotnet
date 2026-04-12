using Mempalace.IntegrationTests.Harness;

namespace Mempalace.IntegrationTests.Tests;

/// <summary>Group 7 — Knowledge graph.</summary>
[Collection("MCP")]
public sealed class T39_T48_KgTests(EmbedderFixture embedder) : IAsyncLifetime, IDisposable
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
    public async Task T39_KgStats_AfterSeeding()
    {
        var ctx = _sqliteCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_kg_stats", new { }));

        var r = s.Result(2);
        Assert.True(r["entities"]!.GetValue<int>() >= 5);
        Assert.True(r["triples"]!.GetValue<int>() >= 5);
        Assert.True(r["currentFacts"]!.GetValue<int>() < r["triples"]!.GetValue<int>());
        var types = r["relationshipTypes"]!.AsArray()
            .Select(x => x!.GetValue<string>()).ToHashSet();
        Assert.Contains("owns", types);
        Assert.Contains("depends_on", types);
        Assert.Contains("uses", types);
    }

    [Fact]
    public async Task T40_KgQuery_RileyOutgoing_OwnsAuthService()
    {
        var ctx = _sqliteCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_kg_query",
                new { entity = "Riley", direction = "outgoing" }));

        var triples = s.Result(2)["triples"]!.AsArray();
        Assert.Contains(triples, t =>
            t!["predicate"]!.GetValue<string>() == "owns" &&
            t!["object"]!.GetValue<string>() == "auth-service");
    }

    [Fact]
    public async Task T41_KgQuery_AuthServiceIncoming_RileyOwns()
    {
        var ctx = _sqliteCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_kg_query",
                new { entity = "auth-service", direction = "incoming" }));

        var triples = s.Result(2)["triples"]!.AsArray();
        Assert.Contains(triples, t =>
            t!["subject"]!.GetValue<string>() == "riley" &&
            t!["predicate"]!.GetValue<string>() == "owns");
    }

    [Fact]
    public async Task T42_KgQuery_AuthServiceBoth_OutgoingAndIncoming()
    {
        var ctx = _sqliteCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_kg_query",
                new { entity = "auth-service", direction = "both" }));

        var triples = s.Result(2)["triples"]!.AsArray();
        var hasOutgoing = triples.Any(t => t!["subject"]!.GetValue<string>() == "auth-service");
        var hasIncoming = triples.Any(t => t!["object"]!.GetValue<string>() == "auth-service");
        Assert.True(hasOutgoing && hasIncoming);
    }

    [Fact]
    public async Task T43_KgQuery_TemporalBeforeMigration_MysqlPresent()
    {
        var ctx = _sqliteCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_kg_query",
                new { entity = "auth-service", as_of = "2023-06-01" }));

        var triples = s.Result(2)["triples"]!.AsArray();
        Assert.Contains(triples, t =>
            t!["predicate"]!.GetValue<string>() == "uses" &&
            t!["object"]!.GetValue<string>() == "mysql");
    }

    [Fact]
    public async Task T44_KgQuery_TemporalAfterMigration_MysqlAbsentJwtPresent()
    {
        var ctx = _sqliteCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_kg_query",
                new { entity = "auth-service", as_of = "2024-06-01" }));

        var triples = s.Result(2)["triples"]!.AsArray();
        Assert.DoesNotContain(triples, t =>
            t!["predicate"]!.GetValue<string>() == "uses" &&
            t!["object"]!.GetValue<string>() == "mysql");
        Assert.Contains(triples, t =>
            t!["predicate"]!.GetValue<string>() == "uses" &&
            t!["object"]!.GetValue<string>() == "jwt");
    }

    [Fact]
    public async Task T45_KgInvalidate_RileyOwns_NotReturnedAfterEndDate()
    {
        var ctx = _sqliteCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_kg_invalidate",
                new { subject = "Riley", predicate = "owns", @object = "auth-service", ended = "2024-12-01" }),
            McpHarness.Call(3, "mempalace_kg_query",
                new { entity = "Riley", direction = "outgoing", as_of = "2025-01-01" }));

        Assert.True(s.Result(2)["success"]!.GetValue<bool>());
        var triples = s.Result(3)["triples"]!.AsArray();
        Assert.DoesNotContain(triples, t =>
            t!["predicate"]!.GetValue<string>() == "owns" &&
            t!["object"]!.GetValue<string>() == "auth-service");
    }

    [Fact]
    public async Task T46_KgTimeline_NoFilter_NonEmpty()
    {
        var ctx = _sqliteCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_kg_timeline", new { }));

        Assert.NotEmpty(s.Result(2)["timeline"]!.AsArray());
    }

    [Fact]
    public async Task T47_KgTimeline_EntityFilter_OnlySamTriples()
    {
        var ctx = _sqliteCtx;
        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_kg_timeline", new { entity = "Sam" }));

        var timeline = s.Result(2)["timeline"]!.AsArray();
        Assert.NotEmpty(timeline);
        foreach (var t in timeline)
        {
            var subj = t!["subject"]!.GetValue<string>();
            var obj = t!["object"]!.GetValue<string>();
            Assert.True(subj == "sam" || obj == "sam");
        }
    }

    [Fact]
    public async Task T48_KgAdd_Duplicate_TriplesCountUnchanged()
    {
        var ctx = _sqliteCtx;
        var before = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_kg_stats", new { }));
        var countBefore = before.Result(2)["triples"]!.GetValue<int>();

        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_kg_add",
                new { subject = "Riley", predicate = "owns", @object = "auth-service", confidence = 1.0 }),
            McpHarness.Call(3, "mempalace_kg_stats", new { }));

        Assert.True(s.Result(2)["success"]!.GetValue<bool>());
        Assert.Equal(countBefore, s.Result(3)["triples"]!.GetValue<int>());
    }
}
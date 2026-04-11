using Mempalace.IntegrationTests.Harness;

namespace Mempalace.IntegrationTests.Tests;

/// <summary>Group 7 — Knowledge graph.</summary>
[Collection("MCP")]
public sealed class T39_T48_KgTests(EmbedderFixture embedder) : IAsyncLifetime, IDisposable
{
    private readonly PalaceFactory _factory = new(embedder.Embedder);
    private McpToolContext _sqliteCtx = null!;
    private McpToolContext _chromaCtx = null!;

    public async ValueTask InitializeAsync()
    {
        (_sqliteCtx, _) = _factory.CreateContext(VectorBackend.Sqlite);
        (_chromaCtx, _) = _factory.CreateContext(VectorBackend.Chroma);
        await Seed.ApplyAsync(_sqliteCtx);
        await Seed.ApplyAsync(_chromaCtx);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void Dispose() => _factory.Dispose();

    // Stats keys: PascalCase (Entities, Triples, CurrentFacts, RelationshipTypes)
    // Triple fields: PascalCase (Subject, Predicate, Object) with lowercase values

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T39_KgStats_AfterSeeding(VectorBackend backend)
    {
        var ctx = backend == VectorBackend.Sqlite ? _sqliteCtx : _chromaCtx;
        var s   = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_kg_stats", new { }));

        var r = s.Result(2);
        Assert.True(r["Entities"]!.GetValue<int>() >= 5);
        Assert.True(r["Triples"]!.GetValue<int>() >= 5);
        Assert.True(r["CurrentFacts"]!.GetValue<int>() < r["Triples"]!.GetValue<int>());
        var types = r["RelationshipTypes"]!.AsArray()
            .Select(x => x!.GetValue<string>()).ToHashSet();
        Assert.Contains("owns",       types);
        Assert.Contains("depends_on", types);
        Assert.Contains("uses",       types);
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T40_KgQuery_RileyOutgoing_OwnsAuthService(VectorBackend backend)
    {
        var ctx = backend == VectorBackend.Sqlite ? _sqliteCtx : _chromaCtx;
        var s   = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_kg_query",
                new { entity = "Riley", direction = "outgoing" }));

        var triples = s.Result(2)["triples"]!.AsArray();
        Assert.Contains(triples, t =>
            t!["Predicate"]!.GetValue<string>() == "owns" &&
            t!["Object"]!.GetValue<string>()    == "auth-service");
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T41_KgQuery_AuthServiceIncoming_RileyOwns(VectorBackend backend)
    {
        var ctx = backend == VectorBackend.Sqlite ? _sqliteCtx : _chromaCtx;
        var s   = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_kg_query",
                new { entity = "auth-service", direction = "incoming" }));

        var triples = s.Result(2)["triples"]!.AsArray();
        Assert.Contains(triples, t =>
            t!["Subject"]!.GetValue<string>()   == "riley" &&
            t!["Predicate"]!.GetValue<string>() == "owns");
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T42_KgQuery_AuthServiceBoth_OutgoingAndIncoming(VectorBackend backend)
    {
        var ctx = backend == VectorBackend.Sqlite ? _sqliteCtx : _chromaCtx;
        var s   = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_kg_query",
                new { entity = "auth-service", direction = "both" }));

        var triples = s.Result(2)["triples"]!.AsArray();
        bool hasOutgoing = triples.Any(t => t!["Subject"]!.GetValue<string>() == "auth-service");
        bool hasIncoming = triples.Any(t => t!["Object"]!.GetValue<string>()  == "auth-service");
        Assert.True(hasOutgoing && hasIncoming);
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T43_KgQuery_TemporalBeforeMigration_MysqlPresent(VectorBackend backend)
    {
        var ctx = backend == VectorBackend.Sqlite ? _sqliteCtx : _chromaCtx;
        var s   = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_kg_query",
                new { entity = "auth-service", as_of = "2023-06-01" }));

        var triples = s.Result(2)["triples"]!.AsArray();
        Assert.Contains(triples, t =>
            t!["Predicate"]!.GetValue<string>() == "uses" &&
            t!["Object"]!.GetValue<string>()    == "mysql");
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T44_KgQuery_TemporalAfterMigration_MysqlAbsentJwtPresent(VectorBackend backend)
    {
        var ctx = backend == VectorBackend.Sqlite ? _sqliteCtx : _chromaCtx;
        var s   = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_kg_query",
                new { entity = "auth-service", as_of = "2024-06-01" }));

        var triples = s.Result(2)["triples"]!.AsArray();
        Assert.DoesNotContain(triples, t =>
            t!["Predicate"]!.GetValue<string>() == "uses" &&
            t!["Object"]!.GetValue<string>()    == "mysql");
        Assert.Contains(triples, t =>
            t!["Predicate"]!.GetValue<string>() == "uses" &&
            t!["Object"]!.GetValue<string>()    == "jwt");
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T45_KgInvalidate_RileyOwns_NotReturnedAfterEndDate(VectorBackend backend)
    {
        var ctx = backend == VectorBackend.Sqlite ? _sqliteCtx : _chromaCtx;
        var s   = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_kg_invalidate",
                new { subject = "Riley", predicate = "owns", @object = "auth-service", ended = "2024-12-01" }),
            McpHarness.Call(3, "mempalace_kg_query",
                new { entity = "Riley", direction = "outgoing", as_of = "2025-01-01" }));

        Assert.True(s.Result(2)["success"]!.GetValue<bool>());
        var triples = s.Result(3)["triples"]!.AsArray();
        Assert.DoesNotContain(triples, t =>
            t!["Predicate"]!.GetValue<string>() == "owns" &&
            t!["Object"]!.GetValue<string>()    == "auth-service");
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T46_KgTimeline_NoFilter_NonEmpty(VectorBackend backend)
    {
        var ctx = backend == VectorBackend.Sqlite ? _sqliteCtx : _chromaCtx;
        var s   = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_kg_timeline", new { }));

        Assert.NotEmpty(s.Result(2)["timeline"]!.AsArray());
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T47_KgTimeline_EntityFilter_OnlySamTriples(VectorBackend backend)
    {
        var ctx = backend == VectorBackend.Sqlite ? _sqliteCtx : _chromaCtx;
        var s   = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_kg_timeline", new { entity = "Sam" }));

        var timeline = s.Result(2)["timeline"]!.AsArray();
        Assert.NotEmpty(timeline);
        foreach (var t in timeline)
        {
            var subj = t!["Subject"]!.GetValue<string>();
            var obj  = t!["Object"]!.GetValue<string>();
            Assert.True(subj == "sam" || obj == "sam");
        }
    }

    [Theory]
    [InlineData(VectorBackend.Sqlite)]
    [InlineData(VectorBackend.Chroma)]
    public async Task T48_KgAdd_Duplicate_TriplesCountUnchanged(VectorBackend backend)
    {
        var ctx = backend == VectorBackend.Sqlite ? _sqliteCtx : _chromaCtx;
        var before = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_kg_stats", new { }));
        var countBefore = before.Result(2)["Triples"]!.GetValue<int>();

        var s = await McpHarness.SessionAsync(ctx,
            McpHarness.Call(2, "mempalace_kg_add",
                new { subject = "Riley", predicate = "owns", @object = "auth-service", confidence = 1.0 }),
            McpHarness.Call(3, "mempalace_kg_stats", new { }));

        Assert.True(s.Result(2)["success"]!.GetValue<bool>());
        Assert.Equal(countBefore, s.Result(3)["Triples"]!.GetValue<int>());
    }
}

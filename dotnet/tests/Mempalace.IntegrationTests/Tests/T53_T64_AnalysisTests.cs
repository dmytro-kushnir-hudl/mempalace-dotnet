using Mempalace.IntegrationTests.Harness;

namespace Mempalace.IntegrationTests.Tests;

/// <summary>Group 9 — Pure analysis tools (backend-independent, Sqlite only).</summary>
[Collection("MCP")]
public sealed class T53_T64_AnalysisTests(EmbedderFixture embedder) : IDisposable
{
    private readonly PalaceFactory _factory = new(embedder.Embedder);

    private Task<McpSession> Run(string tool, object args)
    {
        var (ctx, _) = _factory.CreateContext();
        return McpHarness.SessionAsync(ctx, McpHarness.Call(2, tool, args));
    }

    [Fact]
    public async Task T53_ExtractMemories_Decision()
    {
        var s = await Run("mempalace_extract_memories", new
        {
            text           = "We decided to use PostgreSQL instead of MySQL because of better JSON support.",
            min_confidence = 0.1,
        });
        var r = s.Result(2);
        Assert.True(r["total"]!.GetValue<int>() >= 1);
        Assert.True(r["by_type"]!["decision"]?.GetValue<int>() >= 1);
    }

    [Fact]
    public async Task T54_ExtractMemories_Milestone()
    {
        var s = await Run("mempalace_extract_memories", new
        {
            text = "Milestone: deployed auth v2 to production. The team hit this key goal in June.",
        });
        Assert.True(s.Result(2)["by_type"]!["milestone"]?.GetValue<int>() >= 1);
    }

    [Fact]
    public async Task T55_ExtractMemories_Problem()
    {
        var s = await Run("mempalace_extract_memories", new
        {
            text = "Critical bug: auth middleware does not validate token expiry. This causes session hijacking.",
        });
        Assert.True(s.Result(2)["by_type"]!["problem"]?.GetValue<int>() >= 1);
    }

    [Fact]
    public async Task T56_ExtractMemories_Preference()
    {
        var s = await Run("mempalace_extract_memories", new
        {
            text = "I prefer functional components over class components. The team always uses hooks now.",
        });
        Assert.True(s.Result(2)["by_type"]!["preference"]?.GetValue<int>() >= 1);
    }

    [Fact]
    public async Task T57_ExtractMemories_EmptyText_NoCrash()
    {
        var s = await Run("mempalace_extract_memories", new { text = "" });
        var r = s.Result(2);
        // Either total==0 or an error with a message — no crash
        Assert.True(r["total"]?.GetValue<int>() == 0 || r["error"] is not null);
    }

    [Fact]
    public async Task T58_ExtractMemories_HighThreshold_ZeroOrFew()
    {
        var s = await Run("mempalace_extract_memories", new
        {
            text = "We decided something.", min_confidence = 0.99,
        });
        Assert.True(s.Result(2)["total"]!.GetValue<int>() == 0);
    }

    [Fact]
    public async Task T59_DetectEntities_People_RileyOrSam()
    {
        var s = await Run("mempalace_detect_entities", new
        {
            text = "Riley said the project is going well. Riley told Sam about the progress. Sam laughed. Riley and Sam both agreed.",
        });
        var r = s.Result(2);
        var people    = r["people"]!.AsArray().Select(e => e!["Name"]!.GetValue<string>()).ToHashSet();
        var uncertain = r["uncertain"]!.AsArray().Select(e => e!["Name"]!.GetValue<string>()).ToHashSet();
        Assert.True(people.Contains("Riley") || uncertain.Contains("Riley") ||
                    people.Contains("Sam")   || uncertain.Contains("Sam"));
    }

    [Fact]
    public async Task T60_DetectEntities_Projects_MemPalace()
    {
        var s = await Run("mempalace_detect_entities", new
        {
            text = "We are building MemPalace. We deployed MemPalace v2. The MemPalace pipeline is stable.",
        });
        var projects = s.Result(2)["projects"]!.AsArray()
            .Select(e => e!["Name"]!.GetValue<string>()).ToHashSet();
        Assert.Contains("MemPalace", projects);
    }

    [Fact]
    public async Task T61_DetectEntities_BelowFrequencyThreshold_NoRiley()
    {
        var s = await Run("mempalace_detect_entities", new
        {
            text = "Riley went to the store.",
        });
        var r         = s.Result(2);
        var people    = r["people"]!.AsArray().Select(e => e!["Name"]!.GetValue<string>()).ToHashSet();
        var uncertain = r["uncertain"]!.AsArray().Select(e => e!["Name"]!.GetValue<string>()).ToHashSet();
        Assert.False(people.Contains("Riley") || uncertain.Contains("Riley"));
    }

    [Fact]
    public async Task T62_Compress_Basic_CorrectOutput()
    {
        var (ctx, _) = _factory.CreateContext();
        var session = await McpHarness.SessionAsync(ctx, McpHarness.Call(2, "mempalace_compress", new
        {
            text = "We decided to migrate from MySQL to PostgreSQL because JSON support is far superior. This is a core architectural decision.",
        }));
        var r          = session.Result(2);
        var compressed = r["compressed"]!.GetValue<string>();

        Assert.False(string.IsNullOrEmpty(compressed));
        Assert.True(r["size_ratio"]!.GetValue<double>() > 1.0);
        Assert.True(compressed.Contains("DECISION") || compressed.Contains("CORE") ||
                    compressed.Contains("determ"), $"No expected flags in: {compressed}");

        // Encoding regression: the raw MCP content text must not contain \u0022
        // (server uses UnsafeRelaxedJsonEscaping — double quotes appear as \" not \u0022)
        var rawText = session.Response(2)["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.DoesNotContain(@"\u0022", rawText);
    }

    [Fact]
    public async Task T63_Compress_KeyQuote_ContainsQuotedPhrase()
    {
        var s = await Run("mempalace_compress", new
        {
            text = "We decided to migrate from MySQL to PostgreSQL because JSON support is far superior. This is a core architectural decision.",
        });
        var compressed = s.Result(2)["compressed"]!.GetValue<string>();
        Assert.Contains('"', compressed);
    }

    [Fact]
    public async Task T64_Compress_ShortText_NoCrash()
    {
        var s = await Run("mempalace_compress", new { text = "ok" });
        Assert.False(string.IsNullOrEmpty(s.Result(2)["compressed"]?.GetValue<string>()));
    }

    public void Dispose() => _factory.Dispose();
}

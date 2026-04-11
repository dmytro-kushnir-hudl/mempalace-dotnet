namespace Mempalace.Tests;

public sealed class DialectTests
{
    // ── Compress — basic smoke ────────────────────────────────────────────────

    [Fact]
    public void Compress_EmptyText_ReturnsOutput()
    {
        // Should not throw
        var d = new Dialect();
        var result = d.Compress("Hello world, this is a test.");
        Assert.NotEmpty(result);
    }

    [Fact]
    public void Compress_OutputContainsPipeSeparators()
    {
        var d      = new Dialect();
        var result = d.Compress("We decided to use React because of the ecosystem.");
        // Content line should have pipe separators
        Assert.Contains("|", result);
    }

    // ── Entity encoding ───────────────────────────────────────────────────────

    [Fact]
    public void EncodeEntity_KnownMapping_ReturnsCode()
    {
        var d = new Dialect(entities: new Dictionary<string, string> { ["Alice"] = "ALC" });
        Assert.Equal("ALC", d.EncodeEntity("Alice"));
    }

    [Fact]
    public void EncodeEntity_CaseInsensitive_ReturnsCode()
    {
        var d = new Dialect(entities: new Dictionary<string, string> { ["alice"] = "ALC" });
        Assert.Equal("ALC", d.EncodeEntity("Alice"));
    }

    [Fact]
    public void EncodeEntity_UnknownName_AutoCodes()
    {
        var d    = new Dialect();
        var code = d.EncodeEntity("Zachary");
        Assert.Equal("ZAC", code);
    }

    [Fact]
    public void EncodeEntity_SkipName_ReturnsNull()
    {
        var d = new Dialect(skipNames: ["Gandalf"]);
        Assert.Null(d.EncodeEntity("Gandalf"));
    }

    [Fact]
    public void EncodeEntity_SubstringMatch_ReturnsCode()
    {
        var d = new Dialect(entities: new Dictionary<string, string> { ["Alice"] = "ALC" });
        // "Alice Smith" contains "Alice"
        Assert.Equal("ALC", d.EncodeEntity("Alice Smith"));
    }

    // ── Emotion encoding ──────────────────────────────────────────────────────

    [Fact]
    public void EncodeEmotions_KnownEmotions_ReturnsCompactCodes()
    {
        var d      = new Dialect();
        var result = d.EncodeEmotions(["joy", "fear", "love"]);
        Assert.Contains("joy", result);
        Assert.Contains("fear", result);
        Assert.Contains("love", result);
    }

    [Fact]
    public void EncodeEmotions_MoreThanThree_CappedAtThree()
    {
        var d      = new Dialect();
        var result = d.EncodeEmotions(["joy", "fear", "love", "hope", "grief"]);
        var codes  = result.Split('+');
        Assert.True(codes.Length <= 3);
    }

    [Fact]
    public void EncodeEmotions_Deduplicates()
    {
        var d      = new Dialect();
        var result = d.EncodeEmotions(["joy", "joyful"]);
        // Both map to "joy" — should appear once
        var codes = result.Split('+');
        Assert.Equal(1, codes.Count(c => c == "joy"));
    }

    // ── Compress with metadata ────────────────────────────────────────────────

    [Fact]
    public void Compress_WithMetadata_IncludesHeader()
    {
        var d = new Dialect();
        var result = d.Compress("The architecture uses microservices.",
            new Dictionary<string, string>
            {
                ["wing"]        = "myproject",
                ["room"]        = "technical",
                ["source_file"] = "/path/to/arch.md",
                ["date"]        = "2026-04-11",
            });

        Assert.Contains("myproject", result);
        Assert.Contains("technical", result);
        Assert.Contains("arch", result);  // stem of source_file
    }

    [Fact]
    public void Compress_WithoutMetadata_NoHeader()
    {
        var d = new Dialect();
        var result = d.Compress("We decided to use PostgreSQL.");
        // Without metadata, should start with the content line (0:...)
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("0:", lines[0]);
    }

    // ── DECISION flag detection ───────────────────────────────────────────────

    [Fact]
    public void Compress_DecisionText_ContainsDECISIONFlag()
    {
        var d = new Dialect();
        var result = d.Compress(
            "We decided to switch from REST to GraphQL because of flexibility.");
        Assert.Contains("DECISION", result);
    }

    // ── TECHNICAL flag detection ──────────────────────────────────────────────

    [Fact]
    public void Compress_TechnicalText_ContainsTECHNICALFlag()
    {
        var d = new Dialect();
        var result = d.Compress(
            "The API architecture uses a serverless infrastructure with deploy pipelines.");
        Assert.Contains("TECHNICAL", result);
    }

    // ── Emotion detection ─────────────────────────────────────────────────────

    [Fact]
    public void Compress_EmotionalText_ContainsEmotionCode()
    {
        var d = new Dialect();
        var result = d.Compress("I love this project. I feel excited about the launch.");
        // Should detect love/excite emotions
        Assert.True(result.Contains("love") || result.Contains("excite"),
            $"Expected emotion code in: {result}");
    }

    // ── Topic extraction ──────────────────────────────────────────────────────

    [Fact]
    public void Compress_TechnicalTerms_ExtractedAsTopics()
    {
        var d = new Dialect();
        var result = d.Compress(
            "ChromaDB ChromaDB ChromaDB stores embeddings for semantic search.");
        Assert.Contains("chromadb", result.ToLowerInvariant());
    }

    // ── Key sentence ──────────────────────────────────────────────────────────

    [Fact]
    public void Compress_WithKeyInsight_QuoteIncluded()
    {
        var d = new Dialect();
        var result = d.Compress(
            "After weeks of investigation, the key insight was that batching reduces latency. " +
            "This discovery changed everything about our approach.");
        // Key sentence with "key" keyword should be quoted
        Assert.Contains("\"", result);
    }

    // ── Decode ────────────────────────────────────────────────────────────────

    [Fact]
    public void Decode_ValidAaak_ParsesHeader()
    {
        var d = new Dialect();
        const string aaak = """
            myproject|technical|2026-04-11|arch
            0:ALC+BOB|chromadb_semantic|"key insight was batching"|determ|TECHNICAL
            """;

        var decoded = d.Decode(aaak);
        Assert.Equal("myproject", decoded.Header.GetValueOrDefault("file", ""));
        Assert.NotEmpty(decoded.Zettels);
    }

    [Fact]
    public void Decode_WithArc_ParsesArc()
    {
        var d     = new Dialect();
        var aaak  = "ARC:fear->hope->joy\n0:ALC|memory|\"test\"|fear";
        var result = d.Decode(aaak);
        Assert.Equal("fear->hope->joy", result.Arc);
    }

    [Fact]
    public void Decode_WithTunnel_ParsesTunnel()
    {
        var d     = new Dialect();
        var aaak  = "T:001<->002|shared_concept\n0:ALC|memory";
        var result = d.Decode(aaak);
        Assert.Contains("T:001<->002|shared_concept", result.Tunnels);
    }

    [Fact]
    public void Decode_EmptyInput_ReturnsEmptyDecoded()
    {
        var d      = new Dialect();
        var result = d.Decode("");
        Assert.Empty(result.Arc);
        Assert.Empty(result.Zettels);
        Assert.Empty(result.Tunnels);
    }

    // ── Token counting ────────────────────────────────────────────────────────

    [Fact]
    public void CountTokens_EmptyString_ReturnsOne()
    {
        Assert.Equal(1, Dialect.CountTokens(""));
    }

    [Fact]
    public void CountTokens_SingleWord_ReturnsAtLeastOne()
    {
        Assert.True(Dialect.CountTokens("hello") >= 1);
    }

    [Fact]
    public void CountTokens_LongerText_MoreThanShorter()
    {
        var short_ = Dialect.CountTokens("hello world");
        var long_  = Dialect.CountTokens("hello world foo bar baz qux quux corge grault");
        Assert.True(long_ > short_);
    }

    // ── Compression stats ─────────────────────────────────────────────────────

    [Fact]
    public void CompressionStats_CompressedShorterThanOriginal()
    {
        var d    = new Dialect();
        var text = string.Join(' ', Enumerable.Repeat(
            "We decided to use ChromaDB because it has semantic search capabilities.", 10));
        var compressed = d.Compress(text);
        var stats = d.GetCompressionStats(text, compressed);

        Assert.True(stats.SizeRatio > 1.0, $"Expected compression ratio > 1, got {stats.SizeRatio}");
        Assert.Equal(text.Length, stats.OriginalChars);
        Assert.Equal(compressed.Length, stats.SummaryChars);
    }

    // ── Round-trip: compress then decode ─────────────────────────────────────

    [Fact]
    public void CompressThenDecode_ProducesValidStructure()
    {
        var d          = new Dialect();
        var compressed = d.Compress("We decided to use microservices because of scalability.",
            new Dictionary<string, string> { ["wing"] = "arch", ["room"] = "decisions" });
        var decoded = d.Decode(compressed);

        // After decode, either header or zettels should be populated
        Assert.True(decoded.Header.Count > 0 || decoded.Zettels.Count > 0,
            $"Decode produced nothing from: {compressed}");
    }
}

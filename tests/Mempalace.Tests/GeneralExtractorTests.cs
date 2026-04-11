namespace Mempalace.Tests;

public sealed class GeneralExtractorTests
{
    // ── ExtractMemories — basic smoke ─────────────────────────────────────────

    [Fact]
    public void ExtractMemories_EmptyText_ReturnsEmpty()
    {
        var result = GeneralExtractor.ExtractMemories("");
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractMemories_TinyText_ReturnsEmpty()
    {
        var result = GeneralExtractor.ExtractMemories("hi");
        Assert.Empty(result);
    }

    // ── Decisions ─────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractMemories_DecisionText_ClassifiesDecision()
    {
        const string text = """
            We decided to use GraphQL instead of REST because it gives us more flexibility.
            The trade-off is a steeper learning curve but the benefits outweigh the costs.
            We went with this approach after evaluating the alternatives.
            """;

        var memories = GeneralExtractor.ExtractMemories(text, minConfidence: 0.1);
        Assert.Contains(memories, m => m.MemoryType == MemoryType.Decision);
    }

    [Theory]
    [InlineData("let's use TypeScript instead of JavaScript for this project")]
    [InlineData("we decided to go with PostgreSQL because it handles our load better")]
    [InlineData("the reason we chose microservices is scalability")]
    public void ExtractMemories_DecisionPhrases_Detected(string text)
    {
        var memories = GeneralExtractor.ExtractMemories(text, minConfidence: 0.1);
        Assert.Contains(memories, m => m.MemoryType == MemoryType.Decision);
    }

    // ── Preferences ───────────────────────────────────────────────────────────

    [Fact]
    public void ExtractMemories_PreferenceText_ClassifiesPreference()
    {
        const string text = """
            I prefer snake_case for Python variables. Always use type hints.
            Never use mocks in integration tests — we got burned last quarter.
            My rule is: test with real databases only.
            """;

        var memories = GeneralExtractor.ExtractMemories(text, minConfidence: 0.1);
        Assert.Contains(memories, m => m.MemoryType == MemoryType.Preference);
    }

    // ── Milestones ────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractMemories_MilestoneText_ClassifiesMilestone()
    {
        const string text = """
            Finally got it working after three days of debugging!
            The embedding pipeline is now 3x faster than before.
            Shipped the first version of the search feature today.
            It works — the latency dropped by 40%.
            """;

        var memories = GeneralExtractor.ExtractMemories(text, minConfidence: 0.1);
        Assert.Contains(memories, m => m.MemoryType == MemoryType.Milestone);
    }

    [Theory]
    [InlineData("it works! finally figured out the root cause")]
    [InlineData("breakthrough: implemented semantic deduplication, 2x reduction in storage")]
    [InlineData("shipped v1.0 today, first time we've hit the deadline")]
    public void ExtractMemories_MilestonePhrases_Detected(string text)
    {
        var memories = GeneralExtractor.ExtractMemories(text, minConfidence: 0.1);
        Assert.Contains(memories, m => m.MemoryType == MemoryType.Milestone);
    }

    // ── Problems ──────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractMemories_ProblemText_ClassifiesProblem()
    {
        const string text = """
            There's a bug in the auth middleware — it's crashing on refresh tokens.
            The root cause is a race condition in the token store.
            The issue keeps failing in production but not locally.
            """;

        var memories = GeneralExtractor.ExtractMemories(text, minConfidence: 0.1);
        Assert.Contains(memories, m => m.MemoryType == MemoryType.Problem);
    }

    // ── Emotional ─────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractMemories_EmotionalText_ClassifiesEmotional()
    {
        const string text = """
            I'm really proud of what we built together. It feels amazing.
            I love how the team came together under pressure.
            I was worried we wouldn't make it but now I feel grateful.
            """;

        var memories = GeneralExtractor.ExtractMemories(text, minConfidence: 0.1);
        Assert.Contains(memories, m => m.MemoryType == MemoryType.Emotional);
    }

    // ── Disambiguation ────────────────────────────────────────────────────────

    [Fact]
    public void ExtractMemories_ResolvedProblem_ClassifiesAsMilestone()
    {
        // Problem + resolution signals → should be reclassified as Milestone
        const string text = """
            The bug in the database connection pool was driving us crazy.
            Finally figured it out — it was a missing dispose call.
            Fixed it and now it works perfectly.
            """;

        var memories = GeneralExtractor.ExtractMemories(text, minConfidence: 0.1);
        // Should NOT be classified as Problem (resolved)
        Assert.Contains(memories, m => m.MemoryType == MemoryType.Milestone);
    }

    // ── Confidence filtering ──────────────────────────────────────────────────

    [Fact]
    public void ExtractMemories_HighMinConfidence_FiltersWeak()
    {
        const string text = "we use python. it works. there was a bug.";
        var looseResults = GeneralExtractor.ExtractMemories(text, minConfidence: 0.0);
        var strictResults = GeneralExtractor.ExtractMemories(text, minConfidence: 0.9);
        Assert.True(looseResults.Count >= strictResults.Count);
    }

    // ── Chunk indices ─────────────────────────────────────────────────────────

    [Fact]
    public void ExtractMemories_MultipleSegments_ChunkIndicesSequential()
    {
        const string text = """
            We decided to use React because of the component ecosystem.

            Finally got the build pipeline working after the CI changes.

            There's a bug in the webpack config causing intermittent failures.
            """;

        var memories = GeneralExtractor.ExtractMemories(text, minConfidence: 0.0);
        for (int i = 0; i < memories.Count; i++)
            Assert.Equal(i, memories[i].ChunkIndex);
    }

    // ── Speaker-turn splitting ────────────────────────────────────────────────

    [Fact]
    public void ExtractMemories_DialogueFormat_HandledCorrectly()
    {
        const string text = """
            > We should use Redis for the session store
            That makes sense. Redis is fast and we already have it in our stack.
            > I prefer Redis over Memcached because it has persistence
            Good point. Let's use Redis.
            > Great, decided!
            """;

        var memories = GeneralExtractor.ExtractMemories(text, minConfidence: 0.0);
        Assert.NotEmpty(memories);
    }

    // ── Code line filtering ───────────────────────────────────────────────────

    [Fact]
    public void ExtractMemories_CodeBlock_NotClassifiedAsMemory()
    {
        const string text = """
            ```python
            def process(x):
                return x * 2
            ```
            """;

        // Code-only content should produce zero or low-confidence memories
        var memories = GeneralExtractor.ExtractMemories(text, minConfidence: 0.5);
        // If any memories extracted, they shouldn't be from the code block itself
        foreach (var m in memories)
            Assert.DoesNotContain("def process", m.Content);
    }

    // ── Content preservation ──────────────────────────────────────────────────

    [Fact]
    public void ExtractMemories_ContentPreserved_VerbatimInOutput()
    {
        const string text =
            "We decided to adopt trunk-based development because feature branches were too long-lived.";

        var memories = GeneralExtractor.ExtractMemories(text, minConfidence: 0.0);
        if (memories.Count > 0)
            Assert.Contains("trunk-based development", memories[0].Content);
    }
}

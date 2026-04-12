namespace Mempalace.Tests;

public sealed class KnowledgeGraphTests : IDisposable
{
    private readonly string _dbPath;
    private readonly KnowledgeGraph _kg;

    public KnowledgeGraphTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"kg_test_{Guid.NewGuid():N}.sqlite3");
        _kg = new KnowledgeGraph(_dbPath);
    }

    public void Dispose()
    {
        _kg.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    // ── AddEntity ─────────────────────────────────────────────────────────────

    [Fact]
    public void AddEntity_NewEntity_ReturnsNormalisedId()
    {
        var id = _kg.AddEntity("Alice Smith");
        Assert.Equal("alice_smith", id);
    }

    [Fact]
    public void AddEntity_Duplicate_DoesNotThrow()
    {
        _kg.AddEntity("Bob");
        _kg.AddEntity("Bob"); // upsert — must not throw
        Assert.Equal(1, _kg.Stats().Entities);
    }

    [Fact]
    public void AddEntity_WithProperties_Stored()
    {
        _kg.AddEntity("Carol", "person", new Dictionary<string, object?> { ["age"] = 30 });
        Assert.Equal(1, _kg.Stats().Entities);
    }

    // ── AddTriple ─────────────────────────────────────────────────────────────

    [Fact]
    public void AddTriple_NewTriple_ReturnsId()
    {
        var id = _kg.AddTriple("Max", "child_of", "Alice");
        Assert.StartsWith("t_", id);
    }

    [Fact]
    public void AddTriple_AutoCreatesEntities()
    {
        _kg.AddTriple("Max", "loves", "chess");
        Assert.Equal(2, _kg.Stats().Entities);
    }

    [Fact]
    public void AddTriple_WithTemporalBounds_Stored()
    {
        _kg.AddTriple("Alice", "works_at", "ACME", "2020-01-01");
        var triples = _kg.QueryEntity("Alice");
        Assert.Single(triples);
        Assert.Equal("2020-01-01", triples[0].ValidFrom);
        Assert.Null(triples[0].ValidTo);
    }

    [Fact]
    public void AddTriple_SameTriple_IsIdempotent()
    {
        _kg.AddTriple("A", "rel", "B");
        _kg.AddTriple("A", "rel", "B");
        Assert.Equal(1, _kg.Stats().Triples);
    }

    // ── Invalidate ────────────────────────────────────────────────────────────

    [Fact]
    public void Invalidate_SetsValidTo()
    {
        _kg.AddTriple("Max", "has_issue", "injury", "2026-01-01");
        _kg.Invalidate("Max", "has_issue", "injury", "2026-03-01");

        var triples = _kg.QueryEntity("Max");
        Assert.Single(triples);
        Assert.Equal("2026-03-01", triples[0].ValidTo);
        Assert.False(triples[0].Current);
    }

    // ── QueryEntity ───────────────────────────────────────────────────────────

    [Fact]
    public void QueryEntity_Outgoing_ReturnsTriplesWhereSubject()
    {
        _kg.AddTriple("Alice", "parent_of", "Max");
        _kg.AddTriple("Bob", "friend_of", "Alice");

        var results = _kg.QueryEntity("Alice", direction: "outgoing");
        Assert.Single(results);
        Assert.Equal("outgoing", results[0].Direction);
        Assert.Equal("alice", results[0].Subject);
    }

    [Fact]
    public void QueryEntity_Incoming_ReturnsTriplesWhereObject()
    {
        _kg.AddTriple("Alice", "parent_of", "Max");
        var results = _kg.QueryEntity("Max", direction: "incoming");
        Assert.Single(results);
        Assert.Equal("incoming", results[0].Direction);
    }

    [Fact]
    public void QueryEntity_Both_ReturnsBothDirections()
    {
        _kg.AddTriple("Alice", "parent_of", "Max");
        _kg.AddTriple("Bob", "friend_of", "Alice");

        var results = _kg.QueryEntity("Alice", direction: "both");
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void QueryEntity_AsOf_FiltersTemporally()
    {
        _kg.AddTriple("Alice", "works_at", "ACME", "2020-01-01");
        _kg.AddTriple("Alice", "works_at", "NewCo", "2024-01-01");

        var in2021 = _kg.QueryEntity("Alice", "2021-06-01");
        var in2025 = _kg.QueryEntity("Alice", "2025-01-01");

        // Only the ACME triple is valid in 2021
        Assert.Single(in2021);
        Assert.Equal(NormalisedPredicate("works_at"), in2021[0].Predicate);

        // Both valid in 2025
        Assert.Equal(2, in2025.Count);
    }

    // ── QueryRelationship ─────────────────────────────────────────────────────

    [Fact]
    public void QueryRelationship_ReturnsAllMatchingPredicates()
    {
        _kg.AddTriple("A", "loves", "chess");
        _kg.AddTriple("B", "loves", "music");
        _kg.AddTriple("C", "hates", "traffic");

        var loves = _kg.QueryRelationship("loves");
        Assert.Equal(2, loves.Count);
    }

    // ── Timeline ──────────────────────────────────────────────────────────────

    [Fact]
    public void Timeline_All_ReturnsAllTriples()
    {
        _kg.AddTriple("A", "rel1", "B");
        _kg.AddTriple("C", "rel2", "D");
        Assert.Equal(2, _kg.Timeline().Count);
    }

    [Fact]
    public void Timeline_EntityFilter_ReturnsOnlyRelatedFacts()
    {
        _kg.AddTriple("Alice", "parent_of", "Max");
        _kg.AddTriple("Bob", "lives_in", "Paris");

        var alice = _kg.Timeline("Alice");
        Assert.Single(alice);
    }

    // ── Stats ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Stats_EmptyGraph_AllZero()
    {
        var s = _kg.Stats();
        Assert.Equal(0, s.Entities);
        Assert.Equal(0, s.Triples);
    }

    [Fact]
    public void Stats_AfterAdditions_ReflectsCorrectCounts()
    {
        _kg.AddTriple("A", "rel", "B");
        _kg.AddTriple("C", "rel", "D", validTo: "2020-01-01");

        var s = _kg.Stats();
        Assert.Equal(4, s.Entities);
        Assert.Equal(2, s.Triples);
        Assert.Equal(1, s.CurrentFacts);
        Assert.Equal(1, s.ExpiredFacts);
        Assert.Contains("rel", s.RelationshipTypes);
    }

    // ── NormaliseName ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Alice Smith", "alice_smith")]
    [InlineData("child_of", "child_of")]
    [InlineData("O'Brien", "obrien")]
    [InlineData("  spaced  ", "spaced")]
    public void NormaliseName_MatchesPythonBehaviour(string input, string expected)
    {
        Assert.Equal(expected, KnowledgeGraph.NormaliseName(input));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string NormalisedPredicate(string p)
    {
        return KnowledgeGraph.NormaliseName(p);
    }
}
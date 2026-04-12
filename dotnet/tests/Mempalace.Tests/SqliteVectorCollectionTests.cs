using Mempalace.Storage;
using Microsoft.Extensions.AI;

namespace Mempalace.Tests;

// ---------------------------------------------------------------------------
// StubEmbeddingGenerator — deterministic unit embeddings for tests.
// Each document gets a unique unit vector via index-based seeding.
// ---------------------------------------------------------------------------

internal sealed class StubEmbeddingGenerator
    : IEmbeddingGenerator<ReadOnlyMemory<char>, Embedding<float>>
{
    private const int Dim = 384;
    private readonly Dictionary<string, float[]> _cache = new();
    private int _next;

    public EmbeddingGeneratorMetadata Metadata => new("stub", null, null, Dim);

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<ReadOnlyMemory<char>> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = new List<Embedding<float>>();
        foreach (var v in values)
        {
            var key = v.ToString();
            if (!_cache.TryGetValue(key, out var emb))
            {
                emb = MakeEmb(_next++);
                _cache[key] = emb;
            }

            list.Add(new Embedding<float>(emb));
        }

        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(list));
    }

    object? IEmbeddingGenerator.GetService(Type serviceType, object? key)
    {
        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
    }

    // Put the entire signal in dimension [index % Dim] so
    // identical-index documents get cosine similarity = 1.
    private static float[] MakeEmb(int idx)
    {
        var v = new float[Dim];
        v[idx % Dim] = 1.0f;
        return v;
    }
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

file static class Docs
{
    /// <summary>Converts string literals to ReadOnlyMemory&lt;char&gt;[] for UpsertAsync call sites.</summary>
    public static ReadOnlyMemory<char>[] Of(params string[] docs)
        => Array.ConvertAll(docs, s => s.AsMemory());
}

file static class TestDb
{
    /// <summary>Creates an in-memory-like SQLite collection at a temp path.</summary>
    public static SqliteVectorCollection Open()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mempalace_test_{Guid.NewGuid():N}.sqlite3");
        return new SqliteVectorCollection(path);
    }
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

public sealed class SqliteVectorCollectionTests : IDisposable
{
    private readonly SqliteVectorCollection _col = TestDb.Open();
    private readonly StubEmbeddingGenerator _emb = new();

    public void Dispose()
    {
        _col.Dispose();
    }

    // ── Count ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Count_Empty_ReturnsZero()
    {
        Assert.Equal(0, _col.Count());
    }

    // ── Upsert ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Upsert_SingleRecord_CountIncreases()
    {
        await _col.UpsertAsync(["id1"], Docs.Of("hello world"), _emb,
            [Meta("tech", "backend")], TestContext.Current.CancellationToken);
        Assert.Equal(1, _col.Count());
    }

    [Fact]
    public async Task Upsert_DuplicateId_Overwrites()
    {
        await _col.UpsertAsync(["id1"], Docs.Of("v1"), _emb, [Meta("tech", "backend")], TestContext.Current.CancellationToken);
        await _col.UpsertAsync(["id1"], Docs.Of("v2"), _emb, [Meta("tech", "backend")], TestContext.Current.CancellationToken);
        Assert.Equal(1, _col.Count());

        var rows = _col.Get(ids: ["id1"]);
        Assert.Equal("v2", rows[0].Document);
    }

    [Fact]
    public async Task Upsert_Batch_AllInserted()
    {
        await _col.UpsertAsync(
            ["a", "b", "c"],
            Docs.Of("doc a", "doc b", "doc c"),
            _emb,
            [Meta("w", "r1"), Meta("w", "r2"), Meta("w", "r3")],
            TestContext.Current.CancellationToken);
        Assert.Equal(3, _col.Count());
    }

    // ── Get by IDs ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_ById_ReturnsRecord()
    {
        await _col.UpsertAsync(["x1"], Docs.Of("content"), _emb, [Meta("wing1", "room1")],
            TestContext.Current.CancellationToken);
        var rows = _col.Get(ids: ["x1"]);
        Assert.Single(rows);
        Assert.Equal("x1", rows[0].Id);
        Assert.Equal("content", rows[0].Document);
    }

    [Fact]
    public async Task Get_MissingId_ReturnsEmpty()
    {
        await _col.UpsertAsync(["x1"], Docs.Of("content"), _emb, ct: TestContext.Current.CancellationToken);
        var rows = _col.Get(ids: ["nope"]);
        Assert.Empty(rows);
    }

    // ── Get by filter ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_FilterByWing_ReturnsMatchingRows()
    {
        await _col.UpsertAsync(
            ["a", "b", "c"],
            Docs.Of("a doc", "b doc", "c doc"),
            _emb,
            [Meta("alpha", "r"), Meta("beta", "r"), Meta("alpha", "r")],
            TestContext.Current.CancellationToken);

        var rows = _col.Get(MetadataFilter.Where("wing", "alpha"));
        Assert.Equal(2, rows.Length);
        Assert.All(rows, r => Assert.Equal("alpha", r.Metadata?["wing"]?.ToString()));
    }

    [Fact]
    public async Task Get_FilterByWingAndRoom_NarrowsResults()
    {
        await _col.UpsertAsync(
            ["a", "b", "c"],
            Docs.Of("a doc", "b doc", "c doc"),
            _emb,
            [Meta("w", "r1"), Meta("w", "r2"), Meta("w", "r1")],
            TestContext.Current.CancellationToken);

        var rows = _col.Get(MetadataFilter.Where("wing", "w").And("room", "r1"));
        Assert.Equal(2, rows.Length);
    }

    // ── Get pagination ────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_Limit_CapsResults()
    {
        await _col.UpsertAsync(
            ["a", "b", "c", "d", "e"],
            Enumerable.Range(0, 5).Select(i => $"doc{i}".AsMemory()).ToArray(),
            _emb, ct: TestContext.Current.CancellationToken);
        var rows = _col.Get(limit: 3);
        Assert.Equal(3, rows.Length);
    }

    [Fact]
    public async Task Get_Offset_SkipsRows()
    {
        await _col.UpsertAsync(
            ["a", "b", "c"],
            Docs.Of("doc a", "doc b", "doc c"),
            _emb, ct: TestContext.Current.CancellationToken);
        var all = _col.Get();
        var paged = _col.Get(offset: 1);
        Assert.Equal(all.Length - 1, paged.Length);
    }

    // ── Delete by IDs ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_ById_RemovesRecord()
    {
        await _col.UpsertAsync(["d1", "d2"], Docs.Of("a", "b"), _emb, ct: TestContext.Current.CancellationToken);
        _col.Delete(["d1"]);
        Assert.Equal(1, _col.Count());
        Assert.Empty(_col.Get(ids: ["d1"]));
    }

    // ── Delete by filter ──────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_ByFilter_RemovesMatchingRows()
    {
        await _col.UpsertAsync(
            ["a", "b", "c"],
            Docs.Of("a", "b", "c"),
            _emb,
            [Meta("wing1", "r"), Meta("wing2", "r"), Meta("wing1", "r")],
            TestContext.Current.CancellationToken);

        _col.Delete(MetadataFilter.Where("wing", "wing1"));
        Assert.Equal(1, _col.Count());
    }

    // ── Search (brute-force cosine, no vec0 in CI) ────────────────────────────

    [Fact]
    public async Task Search_ReturnsTopNResults()
    {
        // Use a fresh embedder so each document gets a distinct vector
        var emb = new StubEmbeddingGenerator();
        await _col.UpsertAsync(
            ["s1", "s2", "s3"],
            Docs.Of("alpha", "beta", "gamma"),
            emb,
            [Meta("w", "r"), Meta("w", "r"), Meta("w", "r")],
            TestContext.Current.CancellationToken);

        // "alpha" was embedded at index 0; same query text → same vector → sim=1
        var results = await _col.SearchAsync("alpha", emb, 2, ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, results.Length);
        // Top result should be the exact match
        Assert.Equal("s1", results[0].Id);
        Assert.Equal(1.0, results[0].Similarity, 5);
    }

    [Fact]
    public async Task Search_WithFilter_OnlyMatchesFilter()
    {
        var emb = new StubEmbeddingGenerator();
        await _col.UpsertAsync(
            ["x1", "x2"],
            Docs.Of("query doc", "query doc"),
            emb,
            [Meta("wingA", "r"), Meta("wingB", "r")],
            TestContext.Current.CancellationToken);

        var results = await _col.SearchAsync("query doc", emb, 5,
            MetadataFilter.Where("wing", "wingA"),
            TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("x1", results[0].Id);
    }

    // ── BLOB round-trip ───────────────────────────────────────────────────────

    [Fact]
    public void FloatsBlobRoundTrip_Lossless()
    {
        var orig = Enumerable.Range(0, 384).Select(i => i * 0.001f).ToArray();
        var bytes = SqliteVectorCollection.FloatsToBytes(orig);
        var back = SqliteVectorCollection.BytesToFloats(bytes);
        Assert.Equal(orig, back);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Dictionary<string, object?> Meta(string wing, string room)
    {
        return new Dictionary<string, object?>
            { ["wing"] = wing, ["room"] = room, ["source_file"] = "", ["chunk_index"] = 0L };
    }
}

// ---------------------------------------------------------------------------
// PalaceSession SQLite backend smoke tests
// ---------------------------------------------------------------------------

public sealed class PalaceSessionSqliteTests
{
    [Fact]
    public void Open_SqliteBackend_CreatesCollection()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mp_test_{Guid.NewGuid():N}");
        using var session = PalaceSession.Open(dir, backend: VectorBackend.Sqlite);
        Assert.Equal(0, session.Collection.Count());
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task FileAlreadyMined_SqliteBackend_Works()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mp_test_{Guid.NewGuid():N}");
        var emb = new StubEmbeddingGenerator();
        using (var session = PalaceSession.Open(dir, backend: VectorBackend.Sqlite))
        {
            Assert.False(session.FileAlreadyMined("/some/file.cs"));

            await session.Collection.UpsertAsync(
                ["id1"], Docs.Of("content"), emb,
                [new Dictionary<string, object?> { ["source_file"] = "/some/file.cs" }],
                TestContext.Current.CancellationToken);

            Assert.True(session.FileAlreadyMined("/some/file.cs"));
        }

        Directory.Delete(dir, true);
    }
}
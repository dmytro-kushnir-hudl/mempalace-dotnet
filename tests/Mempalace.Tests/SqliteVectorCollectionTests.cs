using System.Runtime.InteropServices;
using Microsoft.Extensions.AI;
using Mempalace.Storage;

namespace Mempalace.Tests;

// ---------------------------------------------------------------------------
// StubEmbeddingGenerator — deterministic unit embeddings for tests.
// Each document gets a unique unit vector via index-based seeding.
// ---------------------------------------------------------------------------

internal sealed class StubEmbeddingGenerator
    : IEmbeddingGenerator<string, Embedding<float>>
{
    private const int Dim = 384;
    private readonly Dictionary<string, float[]> _cache = new();
    private int _next;

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = new List<Embedding<float>>();
        foreach (var v in values)
        {
            if (!_cache.TryGetValue(v, out var emb))
            {
                emb = MakeEmb(_next++);
                _cache[v] = emb;
            }
            list.Add(new Embedding<float>(emb));
        }
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(list));
    }

    object? IEmbeddingGenerator.GetService(Type serviceType, object? key)
        => serviceType.IsInstanceOfType(this) ? this : null;

    public EmbeddingGeneratorMetadata Metadata => new("stub", null, null, Dim);

    public void Dispose() { }

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

    public void Dispose() => _col.Dispose();

    // ── Count ─────────────────────────────────────────────────────────────────

    [Fact] public void Count_Empty_ReturnsZero() => Assert.Equal(0, _col.Count());

    // ── Upsert ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Upsert_SingleRecord_CountIncreases()
    {
        await _col.UpsertAsync(["id1"], ["hello world"], _emb,
            [Meta("tech", "backend")]);
        Assert.Equal(1, _col.Count());
    }

    [Fact]
    public async Task Upsert_DuplicateId_Overwrites()
    {
        await _col.UpsertAsync(["id1"], ["v1"], _emb, [Meta("tech", "backend")]);
        await _col.UpsertAsync(["id1"], ["v2"], _emb, [Meta("tech", "backend")]);
        Assert.Equal(1, _col.Count());

        var rows = _col.Get(ids: ["id1"]);
        Assert.Equal("v2", rows[0].Document);
    }

    [Fact]
    public async Task Upsert_Batch_AllInserted()
    {
        await _col.UpsertAsync(
            ["a","b","c"],
            ["doc a","doc b","doc c"],
            _emb,
            [Meta("w","r1"), Meta("w","r2"), Meta("w","r3")]);
        Assert.Equal(3, _col.Count());
    }

    // ── Get by IDs ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_ById_ReturnsRecord()
    {
        await _col.UpsertAsync(["x1"], ["content"], _emb, [Meta("wing1","room1")]);
        var rows = _col.Get(ids: ["x1"]);
        Assert.Single(rows);
        Assert.Equal("x1",      rows[0].Id);
        Assert.Equal("content", rows[0].Document);
    }

    [Fact]
    public async Task Get_MissingId_ReturnsEmpty()
    {
        await _col.UpsertAsync(["x1"], ["content"], _emb);
        var rows = _col.Get(ids: ["nope"]);
        Assert.Empty(rows);
    }

    // ── Get by filter ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_FilterByWing_ReturnsMatchingRows()
    {
        await _col.UpsertAsync(
            ["a","b","c"],
            ["a doc","b doc","c doc"],
            _emb,
            [Meta("alpha","r"), Meta("beta","r"), Meta("alpha","r")]);

        var rows = _col.Get(MetadataFilter.Where("wing", "alpha"));
        Assert.Equal(2, rows.Length);
        Assert.All(rows, r => Assert.Equal("alpha", r.Metadata?["wing"]?.ToString()));
    }

    [Fact]
    public async Task Get_FilterByWingAndRoom_NarrowsResults()
    {
        await _col.UpsertAsync(
            ["a","b","c"],
            ["a doc","b doc","c doc"],
            _emb,
            [Meta("w","r1"), Meta("w","r2"), Meta("w","r1")]);

        var rows = _col.Get(MetadataFilter.Where("wing","w").And("room","r1"));
        Assert.Equal(2, rows.Length);
    }

    // ── Get pagination ────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_Limit_CapsResults()
    {
        await _col.UpsertAsync(
            ["a","b","c","d","e"],
            Enumerable.Range(0,5).Select(i => $"doc{i}").ToArray(),
            _emb);
        var rows = _col.Get(limit: 3);
        Assert.Equal(3, rows.Length);
    }

    [Fact]
    public async Task Get_Offset_SkipsRows()
    {
        await _col.UpsertAsync(
            ["a","b","c"],
            ["doc a","doc b","doc c"],
            _emb);
        var all    = _col.Get();
        var paged  = _col.Get(offset: 1);
        Assert.Equal(all.Length - 1, paged.Length);
    }

    // ── Delete by IDs ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_ById_RemovesRecord()
    {
        await _col.UpsertAsync(["d1","d2"], ["a","b"], _emb);
        _col.Delete(["d1"]);
        Assert.Equal(1, _col.Count());
        Assert.Empty(_col.Get(ids: ["d1"]));
    }

    // ── Delete by filter ──────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_ByFilter_RemovesMatchingRows()
    {
        await _col.UpsertAsync(
            ["a","b","c"],
            ["a","b","c"],
            _emb,
            [Meta("wing1","r"), Meta("wing2","r"), Meta("wing1","r")]);

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
            ["s1","s2","s3"],
            ["alpha","beta","gamma"],
            emb,
            [Meta("w","r"), Meta("w","r"), Meta("w","r")]);

        // "alpha" was embedded at index 0; same query text → same vector → sim=1
        var results = await _col.SearchAsync("alpha", emb, nResults: 2);
        Assert.Equal(2, results.Length);
        // Top result should be the exact match
        Assert.Equal("s1", results[0].Id);
        Assert.Equal(1.0, results[0].Similarity, precision: 5);
    }

    [Fact]
    public async Task Search_WithFilter_OnlyMatchesFilter()
    {
        var emb = new StubEmbeddingGenerator();
        await _col.UpsertAsync(
            ["x1","x2"],
            ["query doc","query doc"],
            emb,
            [Meta("wingA","r"), Meta("wingB","r")]);

        var results = await _col.SearchAsync("query doc", emb, nResults: 5,
            filter: MetadataFilter.Where("wing", "wingA"));

        Assert.Single(results);
        Assert.Equal("x1", results[0].Id);
    }

    // ── BLOB round-trip ───────────────────────────────────────────────────────

    [Fact]
    public void FloatsBlobRoundTrip_Lossless()
    {
        var orig  = Enumerable.Range(0, 384).Select(i => (float)i * 0.001f).ToArray();
        var bytes = SqliteVectorCollection.FloatsToBytes(orig);
        var back  = SqliteVectorCollection.BytesToFloats(bytes);
        Assert.Equal(orig, back);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Dictionary<string, object?> Meta(string wing, string room) =>
        new() { ["wing"] = wing, ["room"] = room, ["source_file"] = "", ["chunk_index"] = 0L };
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
        Directory.Delete(dir, recursive: true);
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
                ["id1"], ["content"], emb,
                [new Dictionary<string, object?> { ["source_file"] = "/some/file.cs" }]);

            Assert.True(session.FileAlreadyMined("/some/file.cs"));
        }
        Directory.Delete(dir, recursive: true);
    }
}

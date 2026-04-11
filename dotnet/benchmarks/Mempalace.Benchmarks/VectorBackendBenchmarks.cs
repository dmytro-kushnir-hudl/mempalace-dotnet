using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Chroma.Embeddings;
using Mempalace;
using Mempalace.Storage;
using Microsoft.Extensions.AI;

// ── Entry point ───────────────────────────────────────────────────────────────

var summary = BenchmarkRunner.Run<VectorBackendBenchmarks>();

// ── Benchmark class ───────────────────────────────────────────────────────────

/// <summary>
/// Compares SQLite and Chroma backends across the three hot-path operations:
///   - UpsertAsync   (add a drawer)
///   - SearchAsync   (semantic search)
///   - Get           (metadata-filtered list)
///
/// Each operation is measured independently with a pre-warmed palace containing
/// <see cref="WarmupDrawerCount"/> drawers so that search similarity is meaningful.
///
/// Run with:
///   dotnet run -c Release --project benchmarks/Mempalace.Benchmarks/
/// </summary>
[ShortRunJob]
[MemoryDiagnoser]
[Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
[RankColumn]
public class VectorBackendBenchmarks : IDisposable
{
    // ── Config ────────────────────────────────────────────────────────────────

    /// <summary>Drawers inserted during Setup before benchmarks run.</summary>
    [Params(50, 200)]
    public int WarmupDrawerCount { get; set; }

    // ── State ─────────────────────────────────────────────────────────────────

    private DefaultEmbeddingProvider _embedder = null!;
    private string _sqlitePalace = null!;
    private string _chromaPalace = null!;
    private int _upsertCounter;

    // Pre-computed embedding for search benchmarks (avoids embedding cost in timed section)
    private Embedding<float>? _queryEmbedding;

    private static readonly string[] SeedDocuments =
    [
        "We decided to use JWT tokens with RS256 signing for stateless verification across microservices.",
        "Critical bug: auth middleware does not validate token expiry on refresh endpoint.",
        "We migrated from MySQL to PostgreSQL because of superior JSON query support.",
        "Frontend auth flow uses PKCE. Access tokens are stored in memory only.",
        "We decided to migrate from class components to hooks for functional style.",
        "Milestone: deployed zero-downtime auth v2 to production on 2024-06-15.",
        "The connection pool is configured with max 20 connections per service instance.",
        "Rate limiting is enforced at the API gateway with a token-bucket algorithm.",
        "Service mesh uses mTLS for all inter-service communication in production.",
        "Database migrations use Flyway with version-controlled SQL scripts.",
        "All secrets are stored in HashiCorp Vault with dynamic secret leases.",
        "CI pipeline runs unit tests, integration tests, and SAST on every PR.",
        "Feature flags are managed with LaunchDarkly; old flags are cleaned quarterly.",
        "Logging uses structured JSON via Serilog, shipped to Elasticsearch.",
        "The monorepo uses Bazel for hermetic builds across 40+ services.",
        "GraphQL federation is used for the public API with Apollo Router.",
        "Event sourcing is used for the order domain with EventStoreDB.",
        "The search service uses Elasticsearch with custom BM25 tuning.",
        "Caching strategy: write-through cache with Redis, TTL 5 minutes.",
        "Dead-letter queue processing retries failed events up to 3 times.",
    ];

    // ── Setup / Teardown ───────────────────────────────────────────────────────

    [GlobalSetup]
    public async Task Setup()
    {
        _embedder     = await DefaultEmbeddingProvider.CreateAsync();
        _sqlitePalace = Path.Combine(Path.GetTempPath(), $"bench_sqlite_{Guid.NewGuid():N}");
        _chromaPalace = Path.Combine(Path.GetTempPath(), $"bench_chroma_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_sqlitePalace);
        Directory.CreateDirectory(_chromaPalace);

        await SeedPalaceAsync(VectorBackend.Sqlite, _sqlitePalace);
        await SeedPalaceAsync(VectorBackend.Chroma, _chromaPalace);

        // Pre-warm: embed the search query once
        var gen = await _embedder.GenerateAsync(["auth JWT token"]);
        _queryEmbedding = gen[0];
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _embedder.Dispose();
        if (Directory.Exists(_sqlitePalace)) Directory.Delete(_sqlitePalace, recursive: true);
        if (Directory.Exists(_chromaPalace)) Directory.Delete(_chromaPalace, recursive: true);
    }

    public void Dispose() => Cleanup();

    // ── Benchmarks ─────────────────────────────────────────────────────────────

    // ── UpsertAsync ────────────────────────────────────────────────────────────

    [Benchmark]
    public async Task Sqlite_Upsert()
    {
        var (id, doc, meta) = NextDrawer();
        using var session = PalaceSession.Open(_sqlitePalace, backend: VectorBackend.Sqlite);
        await session.Collection.UpsertAsync([id], [doc], _embedder, [meta]);
    }

    [Benchmark]
    public async Task Chroma_Upsert()
    {
        var (id, doc, meta) = NextDrawer();
        using var session = PalaceSession.Open(_chromaPalace, backend: VectorBackend.Chroma);
        await session.Collection.UpsertAsync([id], [doc], _embedder, [meta]);
    }

    // ── SearchAsync ────────────────────────────────────────────────────────────

    [Benchmark]
    public async Task<int> Sqlite_Search()
    {
        using var session = PalaceSession.Open(_sqlitePalace, backend: VectorBackend.Sqlite);
        var results = await session.Collection.SearchAsync("auth JWT token", _embedder, nResults: 5);
        return results.Length;
    }

    [Benchmark]
    public async Task<int> Chroma_Search()
    {
        using var session = PalaceSession.Open(_chromaPalace, backend: VectorBackend.Chroma);
        var results = await session.Collection.SearchAsync("auth JWT token", _embedder, nResults: 5);
        return results.Length;
    }

    // ── Get (metadata filter) ──────────────────────────────────────────────────

    [Benchmark]
    public int Sqlite_GetFiltered()
    {
        using var session = PalaceSession.Open(_sqlitePalace, backend: VectorBackend.Sqlite);
        var filter = MetadataFilter.Where("wing", "backend");
        return session.Collection.Get(filter: filter, limit: 100).Length;
    }

    [Benchmark]
    public int Chroma_GetFiltered()
    {
        using var session = PalaceSession.Open(_chromaPalace, backend: VectorBackend.Chroma);
        var filter = MetadataFilter.Where("wing", "backend");
        return session.Collection.Get(filter: filter, limit: 100).Length;
    }

    // ── Count ──────────────────────────────────────────────────────────────────

    [Benchmark]
    public int Sqlite_Count()
    {
        using var session = PalaceSession.Open(_sqlitePalace, backend: VectorBackend.Sqlite);
        return session.Collection.Count();
    }

    [Benchmark]
    public int Chroma_Count()
    {
        using var session = PalaceSession.Open(_chromaPalace, backend: VectorBackend.Chroma);
        return session.Collection.Count();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task SeedPalaceAsync(VectorBackend backend, string palacePath)
    {
        using var session = PalaceSession.Open(palacePath, backend: backend);
        var wings = new[] { "backend", "frontend", "infra" };
        var rooms = new[] { "auth", "database", "api", "components", "config" };

        var ids   = new string[WarmupDrawerCount];
        var docs  = new string[WarmupDrawerCount];
        var metas = new Dictionary<string, object?>[WarmupDrawerCount];

        for (int i = 0; i < WarmupDrawerCount; i++)
        {
            var doc  = SeedDocuments[i % SeedDocuments.Length] + $" (copy {i})";
            var wing = wings[i % wings.Length];
            var room = rooms[i % rooms.Length];
            ids[i]   = $"bench_{wing}_{room}_{i:D4}";
            docs[i]  = doc;
            metas[i] = new Dictionary<string, object?>
            {
                ["wing"]        = wing,
                ["room"]        = room,
                ["source_file"] = "",
                ["chunk_index"] = (long)i,
                ["added_by"]    = "benchmark",
                ["filed_at"]    = DateTime.UtcNow.ToString("O"),
            };
        }

        // Batch in groups of 50 to avoid embedding OOM on large counts
        const int BatchSize = 50;
        for (int offset = 0; offset < WarmupDrawerCount; offset += BatchSize)
        {
            int end  = Math.Min(offset + BatchSize, WarmupDrawerCount);
            int len  = end - offset;
            await session.Collection.UpsertAsync(
                ids[offset..end], docs[offset..end], _embedder,
                metas[offset..end]);
        }
    }

    private (string Id, string Doc, Dictionary<string, object?> Meta) NextDrawer()
    {
        int n   = System.Threading.Interlocked.Increment(ref _upsertCounter);
        var doc = SeedDocuments[n % SeedDocuments.Length] + $" (bench insert {n})";
        return (
            Id:  $"bench_new_{n:D8}",
            Doc: doc,
            Meta: new Dictionary<string, object?>
            {
                ["wing"]        = "bench",
                ["room"]        = "regression",
                ["source_file"] = "",
                ["chunk_index"] = (long)n,
                ["added_by"]    = "benchmark",
                ["filed_at"]    = DateTime.UtcNow.ToString("O"),
            });
    }
}

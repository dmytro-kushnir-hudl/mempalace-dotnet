using Microsoft.Extensions.AI;
using System.Text;
using Chroma.Embeddings;
using Mempalace.Storage;

namespace Mempalace;

// ---------------------------------------------------------------------------
// Layer 0 — Identity (~100 tokens, always loaded)
// ---------------------------------------------------------------------------

public sealed class Layer0
{
    private readonly string _identityPath;
    private string? _cached;

    public Layer0(string? identityPath = null)
        => _identityPath = identityPath ?? Constants.DefaultIdentityPath;

    public string Render()
    {
        if (_cached is not null) return _cached;
        _cached = File.Exists(_identityPath)
            ? File.ReadAllText(_identityPath).Trim()
            : "";
        return _cached;
    }

    public int TokenEstimate() => Render().Length / 4;
}

// ---------------------------------------------------------------------------
// Layer 1 — Essential story (~500-800 tokens, auto-generated from palace)
// ---------------------------------------------------------------------------

public sealed class Layer1
{
    private const int MaxDrawers = 15;
    private const int MaxChars   = 3_200;
    private const int BatchSize  = 500;

    private readonly string  _palacePath;
    private readonly string? _wing;

    private readonly VectorBackend _backend;

    public Layer1(string? palacePath = null, string? wing = null, VectorBackend backend = VectorBackend.Chroma)
    {
        _palacePath = palacePath ?? Constants.DefaultPalacePath;
        _wing       = wing;
        _backend    = backend;
    }

    public string Generate()
    {
        if (!Directory.Exists(_palacePath)) return "";

        using var session = PalaceSession.Open(_palacePath, backend: _backend);
        var col = session.Collection;

        var docs  = new List<string>();
        var metas = new List<Dictionary<string, object?>>();
        int offset = 0;

        while (true)
        {
            var filter = _wing is not null
                ? MetadataFilter.Where("wing", _wing) : null;

            var batch = col.Get(
                filter: filter,
                limit:  BatchSize,
                offset: offset,
                includeDocuments: true,
                includeMetadatas: true);

            if (batch.Length == 0) break;

            foreach (var row in batch)
            {
                if (row.Document is not null)
                {
                    docs.Add(row.Document);
                    metas.Add(row.Metadata ?? []);
                }
            }

            offset += batch.Length;
            if (batch.Length < BatchSize) break;
        }

        if (docs.Count == 0) return "";

        var scored = docs
            .Select((doc, i) => (doc, meta: metas[i], score: ScoreDrawer(doc)))
            .OrderByDescending(x => x.score)
            .Take(MaxDrawers)
            .ToList();

        var sb     = new StringBuilder();
        var byRoom = scored.GroupBy(x => x.meta.GetValueOrDefault("room") as string ?? "general");

        int totalChars = 0;
        foreach (var group in byRoom)
        {
            sb.AppendLine($"## {group.Key}");
            foreach (var (doc, _, _) in group)
            {
                if (totalChars + doc.Length > MaxChars) goto done;
                sb.AppendLine(doc);
                sb.AppendLine();
                totalChars += doc.Length;
            }
        }

        done:
        return sb.ToString().Trim();
    }

    private static int ScoreDrawer(string text)
    {
        var lower = text.ToLowerInvariant();
        int score = 0;
        foreach (var (_, keywords) in Constants.DefaultHallKeywords)
            score += keywords.Count(k => lower.Contains(k));
        return score;
    }
}

// ---------------------------------------------------------------------------
// Layer 2 — On-demand retrieval (~200-500 tokens, filtered by wing/room)
// ---------------------------------------------------------------------------

public sealed class Layer2
{
    private readonly string _palacePath;
    private readonly VectorBackend _backend;

    public Layer2(string? palacePath = null, VectorBackend backend = VectorBackend.Chroma)
    {
        _palacePath = palacePath ?? Constants.DefaultPalacePath;
        _backend    = backend;
    }

    public string Retrieve(string? wing = null, string? room = null, int nResults = 10)
    {
        if (!Directory.Exists(_palacePath)) return "";

        using var session = PalaceSession.Open(_palacePath, backend: _backend);
        var filter = Searcher.BuildFilter(wing, room);

        var rows = session.Collection.Get(
            filter: filter,
            limit:  nResults,
            includeDocuments: true,
            includeMetadatas: true);

        if (rows.Length == 0) return "";

        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            var r = row.Metadata?.GetValueOrDefault("room") as string ?? "";
            if (row.Document is null) continue;
            sb.AppendLine($"[{r}] {row.Document}");
            sb.AppendLine();
        }
        return sb.ToString().Trim();
    }
}

// ---------------------------------------------------------------------------
// Layer 3 — Deep semantic search (unlimited depth)
// ---------------------------------------------------------------------------

public sealed class Layer3
{
    private readonly string _palacePath;
    private readonly VectorBackend _backend;

    public Layer3(string? palacePath = null, VectorBackend backend = VectorBackend.Chroma)
    {
        _palacePath = palacePath ?? Constants.DefaultPalacePath;
        _backend    = backend;
    }

    public async Task<string> SearchAsync(
        string query,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        string? wing = null,
        string? room = null,
        int nResults = 5,
        CancellationToken ct = default)
    {
        var response = await Searcher.SearchMemoriesAsync(
            query, _palacePath, embedder, wing, room, nResults, backend: _backend, ct: ct);

        if (response.Results.Count == 0) return "";

        var sb = new StringBuilder();
        foreach (var hit in response.Results)
        {
            sb.AppendLine($"[{hit.Wing}/{hit.Room}] (sim={hit.Similarity:F3}) {hit.Text}");
            sb.AppendLine();
        }
        return sb.ToString().Trim();
    }

    public async Task<IReadOnlyList<SearchResult>> SearchRawAsync(
        string query,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        string? wing = null,
        string? room = null,
        int nResults = 5,
        CancellationToken ct = default)
    {
        var response = await Searcher.SearchMemoriesAsync(
            query, _palacePath, embedder, wing, room, nResults, backend: _backend, ct: ct);
        return response.Results;
    }
}

// ---------------------------------------------------------------------------
// MemoryStack — unified interface to all four layers
// ---------------------------------------------------------------------------

public sealed class MemoryStack
{
    private readonly string  _palacePath;
    private readonly Layer0  _l0;
    private readonly Layer2  _l2;
    private readonly Layer3  _l3;
    private readonly VectorBackend _backend;

    public MemoryStack(string? palacePath = null, string? identityPath = null,
        VectorBackend backend = VectorBackend.Chroma)
    {
        _palacePath = palacePath ?? Constants.DefaultPalacePath;
        _l0      = new Layer0(identityPath);
        _l2      = new Layer2(_palacePath, backend);
        _l3      = new Layer3(_palacePath, backend);
        _backend = backend;
    }

    public string WakeUp(string? wing = null)
    {
        var l0 = _l0.Render();
        var l1 = new Layer1(_palacePath, wing, _backend).Generate();

        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(l0)) { sb.AppendLine("# Identity"); sb.AppendLine(l0); sb.AppendLine(); }
        if (!string.IsNullOrEmpty(l1)) { sb.AppendLine("# Memory");   sb.AppendLine(l1); }
        return sb.ToString().Trim();
    }

    public string Recall(string? wing = null, string? room = null, int nResults = 10)
        => _l2.Retrieve(wing, room, nResults);

    public Task<string> SearchAsync(
        string query,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        string? wing = null,
        string? room = null,
        int nResults = 5,
        CancellationToken ct = default)
        => _l3.SearchAsync(query, embedder, wing, room, nResults, ct);

    public MemoryStackStatus Status() => new(
        Layer0Tokens: _l0.TokenEstimate(),
        IdentityPath: File.Exists(
            Path.Combine(Constants.DefaultConfigDir, "identity.txt"))
            ? Constants.DefaultIdentityPath : null);
}

public sealed record MemoryStackStatus(int Layer0Tokens, string? IdentityPath);

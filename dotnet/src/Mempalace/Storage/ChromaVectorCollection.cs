using System.Text.Json.Nodes;
using Chroma;
using Chroma.Embeddings;
using Microsoft.Extensions.AI;

namespace Mempalace.Storage;

/// <summary>
///     <see cref="IVectorCollection"/> adapter over the native Chroma C FFI client.
///     Owns the <see cref="NativeChromaClient"/> lifetime.
/// </summary>
public sealed class ChromaVectorCollection : IVectorCollection
{
    private readonly NativeChromaClient _client;
    private readonly NativeCollection   _col;
    private bool _disposed;

    public ChromaVectorCollection(string palacePath, string collectionName)
    {
        Directory.CreateDirectory(palacePath);
        _client = new NativeChromaClient(palacePath);
        _col    = _client.GetOrCreateCollection(collectionName);
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    public Task UpsertAsync(
        string[] ids,
        string[] documents,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        Dictionary<string, object?>[]? metadatas = null,
        CancellationToken ct = default)
        => _col.UpsertTextsAsync(ids, documents, embedder, metadatas, ct: ct);

    public void Delete(string[] ids)             => _col.Delete(ids: ids);
    public void Delete(MetadataFilter filter)    => _col.Delete(where: ToJsonNode(filter));

    // ── Read ──────────────────────────────────────────────────────────────────

    public async Task<VectorSearchResult[]> SearchAsync(
        string query,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        int nResults = 5,
        MetadataFilter? filter = null,
        CancellationToken ct = default)
    {
        var qr = await _col.QueryByTextAsync(
            queryTexts: [query],
            embedder: embedder,
            nResults: (uint)Math.Max(1, nResults),
            where: filter is null ? null : ToJsonNode(filter),
            include: [Include.Documents, Include.Metadatas, Include.Distances],
            ct: ct);

        if (qr.Ids.Length == 0) return [];

        var ids   = qr.Ids[0];
        var docs  = qr.Documents?[0] ?? [];
        var metas = qr.Metadatas?[0] ?? [];
        var dists = qr.Distances?[0] ?? [];

        var results = new VectorSearchResult[ids.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            var dist = i < dists.Length ? dists[i] ?? 0.0 : 0.0;
            // Chroma returns L2 distance. For L2-normalized unit vectors:
            // cosine_similarity = 1 - (L2² / 2), mapped to [0, 1] via clamp.
            var similarity = Math.Max(0.0, Math.Min(1.0, 1.0 - (dist * dist / 2.0)));
            results[i] = new VectorSearchResult(
                Id:         ids[i],
                Document:   i < docs.Length  ? docs[i]  : null,
                Metadata:   i < metas.Length ? metas[i] : null,
                Similarity: Math.Round(similarity, 6));
        }
        return results;
    }

    public VectorRecord[] Get(
        MetadataFilter? filter = null,
        string[]? ids = null,
        int limit = 100,
        int offset = 0,
        bool includeDocuments = true,
        bool includeMetadatas = true)
    {
        var include = BuildInclude(includeDocuments, includeMetadatas);

        GetResult raw;
        if (ids is { Length: > 0 })
            raw = _col.Get(ids: ids, include: include);
        else
            raw = _col.Get(
                where:  filter is null ? null : ToJsonNode(filter),
                limit:  (uint)limit,
                offset: (uint)offset,
                include: include);

        var n = raw.Ids.Length;
        var result = new VectorRecord[n];
        for (int i = 0; i < n; i++)
            result[i] = new VectorRecord(
                raw.Ids[i],
                includeDocuments ? raw.Documents?[i] : null,
                includeMetadatas ? raw.Metadatas?[i] : null);
        return result;
    }

    public int Count() => (int)_col.Count();

    // ── Helpers ───────────────────────────────────────────────────────────────

    internal static JsonNode ToJsonNode(MetadataFilter filter)
    {
        var clauses = filter.Clauses;
        if (clauses.Count == 1)
        {
            var kv = clauses.First();
            return new JsonObject { [kv.Key] = JsonValue.Create(kv.Value?.ToString()) };
        }

        var and = new JsonArray();
        foreach (var (k, v) in clauses)
            and.Add(new JsonObject { [k] = JsonValue.Create(v?.ToString()) });
        return new JsonObject { ["$and"] = and };
    }

    private static Include[] BuildInclude(bool docs, bool metas)
    {
        if (docs && metas)  return [Include.Documents, Include.Metadatas];
        if (docs)           return [Include.Documents];
        if (metas)          return [Include.Metadatas];
        return [];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
    }
}

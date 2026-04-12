using Microsoft.Extensions.AI;

namespace Mempalace.Storage;

/// <summary>
///     Backend-agnostic vector store collection.
///     One collection = one "palace" (all wings/rooms together).
/// </summary>
public interface IVectorCollection : IDisposable
{
    // ── Write ─────────────────────────────────────────────────────────────────

    /// <summary>Embed <paramref name="documents"/> and upsert with metadata.</summary>
    Task UpsertAsync(
        string[] ids,
        string[] documents,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        Dictionary<string, object?>[]? metadatas = null,
        CancellationToken ct = default);

    /// <summary>Delete records by IDs.</summary>
    void Delete(string[] ids);

    /// <summary>Delete records matching a metadata filter.</summary>
    void Delete(MetadataFilter filter);

    // ── Read ──────────────────────────────────────────────────────────────────

    /// <summary>Semantic search: embed query, return top-N nearest neighbours.</summary>
    Task<VectorSearchResult[]> SearchAsync(
        string query,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        int nResults = 5,
        MetadataFilter? filter = null,
        CancellationToken ct = default);

    /// <summary>Fetch records by metadata filter (no vector, paginated).</summary>
    VectorRecord[] Get(
        MetadataFilter? filter = null,
        string[]? ids = null,
        int limit = 100,
        int offset = 0,
        bool includeDocuments = true,
        bool includeMetadatas = true);

    int Count();

    /// <summary>Returns max source_mtime per source_file for all mined files. One-shot bulk load.</summary>
    Dictionary<string, double> LoadMinedMtimes();
}

public sealed record VectorRecord(
    string Id,
    string? Document,
    Dictionary<string, object?>? Metadata);

public sealed record VectorSearchResult(
    string Id,
    string? Document,
    Dictionary<string, object?>? Metadata,
    double Similarity);  // 0..1, higher = more similar

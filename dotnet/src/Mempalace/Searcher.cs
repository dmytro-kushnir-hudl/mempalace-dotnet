using Microsoft.Extensions.AI;
using Chroma.Embeddings;
using Mempalace.Storage;

namespace Mempalace;

// ---------------------------------------------------------------------------
// Result types
// ---------------------------------------------------------------------------

public sealed record SearchResult(
    string Text,
    string Wing,
    string Room,
    string SourceFile,
    double Similarity);

public sealed record SearchResponse(
    string Query,
    string? Wing,
    string? Room,
    IReadOnlyList<SearchResult> Results);

public sealed class SearchError(string message) : Exception(message);

// ---------------------------------------------------------------------------
// Searcher
// ---------------------------------------------------------------------------

public static class Searcher
{
    /// <summary>
    ///     Semantic search over the palace.
    ///     Mirrors Python <c>search_memories()</c>.
    /// </summary>
    public static async Task<SearchResponse> SearchMemoriesAsync(
        string query,
        string palacePath,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        string? wing = null,
        string? room = null,
        int nResults = 5,
        string collectionName = Constants.DefaultCollectionName,
        VectorBackend backend = VectorBackend.Chroma,
        CancellationToken ct = default)
    {
        PalaceSession session;
        try { session = PalaceSession.Open(palacePath, collectionName, backend); }
        catch (Exception ex) { throw new SearchError($"No palace found at {palacePath}: {ex.Message}"); }

        using (session)
        {
            var filter = BuildFilter(wing, room);

            VectorSearchResult[] raw;
            try
            {
                raw = await session.Collection.SearchAsync(
                    query, embedder, nResults, filter, ct);
            }
            catch (Exception ex)
            {
                throw new SearchError($"Search error: {ex.Message}");
            }

            var hits = raw.Select(r => new SearchResult(
                Text:       r.Document ?? "",
                Wing:       r.Metadata?.GetValueOrDefault("wing")        as string ?? "",
                Room:       r.Metadata?.GetValueOrDefault("room")        as string ?? "",
                SourceFile: Path.GetFileName(
                    r.Metadata?.GetValueOrDefault("source_file") as string ?? ""),
                Similarity: Math.Round(r.Similarity, 3))).ToList();

            return new SearchResponse(query, wing, room, hits);
        }
    }

    // ── Filter helpers ────────────────────────────────────────────────────────

    public static MetadataFilter? BuildFilter(string? wing, string? room)
    {
        if (wing is not null && room is not null)
            return MetadataFilter.Where("wing", wing).And("room", room);
        if (wing is not null) return MetadataFilter.Where("wing", wing);
        if (room is not null) return MetadataFilter.Where("room", room);
        return null;
    }
}

using Microsoft.Extensions.AI;

namespace Mempalace.Embeddings;

/// <summary>Extension helpers bridging MEA generators to the <c>.EmbedAsync()</c> call pattern.</summary>
public static class EmbeddingProviderExtensions
{
    /// <summary>
    ///     Convenience: embed a list of texts and return one
    ///     <see cref="ReadOnlyMemory{T}"/> per text, backed by managed arrays.
    /// </summary>
    public static async Task<ReadOnlyMemory<float>[]> EmbedAsync(
        this IEmbeddingGenerator<string, Embedding<float>> generator,
        IEnumerable<string> texts,
        CancellationToken ct = default)
    {
        var result = await generator.GenerateAsync(texts, cancellationToken: ct)
            .ConfigureAwait(false);
        return result.Select(e => e.Vector).ToArray();
    }
}

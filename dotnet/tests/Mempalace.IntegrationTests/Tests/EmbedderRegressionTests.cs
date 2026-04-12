using Mempalace.Embeddings;
using Mempalace.IntegrationTests.Harness;

namespace Mempalace.IntegrationTests.Tests;

/// <summary>Regression tests for DefaultEmbeddingProvider.EmbedBatch edge cases.</summary>
[Collection("MCP")]
public sealed class EmbedderRegressionTests(EmbedderFixture fixture)
{
    // 300 single-letter words → 300 content tokens; tokenizer truncates to MaxTokens=256,
    // so len==MaxTokens and padLen==0. Used to crash: Unsafe.InitBlockUnaligned took a
    // ref past the end of the rented array (padStart==flatLen on the last batch item).
    private static readonly ReadOnlyMemory<char> FullSlotText =
        string.Join(" ", Enumerable.Repeat("a", 300)).AsMemory();

    [Fact]
    public async Task EmbedBatch_SingleItemFillsAllTokenSlots_DoesNotThrow()
    {
        var embeddings = await fixture.Embedder.GenerateAsync([FullSlotText]);

        Assert.Single(embeddings);
        Assert.Equal(DefaultEmbeddingProvider.EmbeddingDim, embeddings[0].Vector.Length);
    }

    [Fact]
    public async Task EmbedBatch_LastItemInBatchFillsAllTokenSlots_DoesNotThrow()
    {
        // Critical case: last item has padStart==flatLen — the exact out-of-bounds index.
        var embeddings = await fixture.Embedder.GenerateAsync(
        [
            "short text".AsMemory(),
            FullSlotText
        ]);

        Assert.Equal(2, embeddings.Count);
        Assert.All(embeddings, e => Assert.Equal(DefaultEmbeddingProvider.EmbeddingDim, e.Vector.Length));
    }
}

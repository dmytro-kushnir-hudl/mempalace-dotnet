using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.ML.Tokenizers;

namespace Mempalace;

/// <summary>
///     Streaming token-aware chunker — fills the model's context window (up to maxTokens)
///     per chunk using a sliding 32 KB pooled buffer, never materialising the full file.
///     Ported from rag-pg's TokenAwareChunker, adapted for BertTokenizer and mempalace's Chunk record.
/// </summary>
internal sealed class TokenAwareChunker(BertTokenizer tokenizer, int maxTokens)
{
    // Reserve 2 tokens for [CLS] + [SEP] added by the embedder
    private readonly int _maxContentTokens = maxTokens - 2;

    public async IAsyncEnumerable<Chunk> ChunkFileAsync(
        string filePath, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 4096, useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true, bufferSize: 16 * 1024);

        var buffer = ArrayPool<char>.Shared.Rent(32 * 1024);
        int validLength = 0;
        int chunkIndex = 0;

        try
        {
            while (true)
            {
                // 1. Fill available space in the sliding buffer
                int charsRead = await reader.ReadAsync(buffer.AsMemory(validLength), ct);
                validLength += charsRead;

                bool isEof = charsRead == 0;
                if (isEof && validLength == 0) break;

                int offset = 0;

                // 2. Emit chunks from whatever is buffered
                while (offset < validLength)
                {
                    var span = buffer.AsSpan(offset, validLength - offset);
                    var ids = tokenizer.EncodeToIds(span, _maxContentTokens,
                        normalizedText: out _, charsConsumed: out var consumed);

                    // Need more data: haven't hit token limit yet and not at EOF
                    if (consumed <= 0 || (ids.Count < _maxContentTokens && !isEof))
                        break;

                    var boundary = FindSentenceBoundary(span[..consumed]);
                    if (boundary <= 0)
                    {
                        yield return new Chunk(span[..consumed].ToString().AsMemory(), chunkIndex++);
                        offset += consumed;
                    }
                    else
                    {
                        yield return new Chunk(span[..boundary].ToString().AsMemory(), chunkIndex++);
                        offset += boundary;
                    }
                }

                // 3. Slide leftover to front
                int remaining = validLength - offset;
                if (remaining > 0 && offset > 0)
                    buffer.AsSpan(offset, remaining).CopyTo(buffer.AsSpan(0));
                validLength = remaining;

                // 4. Grow buffer if full (very long line with no sentence boundary)
                if (validLength == buffer.Length && !isEof)
                {
                    var bigger = ArrayPool<char>.Shared.Rent(buffer.Length * 2);
                    buffer.AsSpan(0, validLength).CopyTo(bigger);
                    ArrayPool<char>.Shared.Return(buffer);
                    buffer = bigger;
                }

                // 5. Flush tail at EOF
                if (isEof && validLength > 0)
                {
                    var span = buffer.AsSpan(0, validLength);
                    var trimmed = span.Trim();
                    if (trimmed.Length >= Constants.MinChunkSize)
                        yield return new Chunk(trimmed.ToString().AsMemory(), chunkIndex++);
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private static int FindSentenceBoundary(ReadOnlySpan<char> text)
    {
        for (var i = text.Length - 1; i >= text.Length / 2; i--)
        {
            var c = text[i];
            if (c is '.' or '!' or '?')
            {
                if (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1]))
                    return i + 1;
            }
            if (c == '\n') return i + 1;
        }
        return -1;
    }
}

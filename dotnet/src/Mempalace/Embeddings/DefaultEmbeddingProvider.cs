using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Mempalace.Embeddings;

// OnnxRuntime ≥1.21 changed DllImport name to "onnxruntime.dll" on all platforms.
// On macOS the actual file is libonnxruntime.dylib — register a resolver once.
internal static class OnnxNativeResolver
{
    private static int _registered;

    internal static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0) return;
        NativeLibrary.SetDllImportResolver(
            typeof(OrtEnv).Assembly,
            static (name, assembly, path) =>
            {
                if (!name.Equals("onnxruntime.dll", StringComparison.OrdinalIgnoreCase))
                    return IntPtr.Zero;
                // Try libonnxruntime.dylib next to the assembly, then default search
                var dir = Path.GetDirectoryName(assembly.Location) ?? ".";
                var candidate = Path.Combine(dir, "libonnxruntime.dylib");
                if (NativeLibrary.TryLoad(candidate, out var h)) return h;
                if (NativeLibrary.TryLoad("libonnxruntime.dylib", assembly, path, out h)) return h;
                return IntPtr.Zero;
            });
    }
}

/// <summary>
///     ONNX-based embedding provider that exactly matches the Python ChromaDB default:
///     <c>all-MiniLM-L6-v2</c> with mean pooling and L2 normalisation (384 dims).
///     <para>
///         On first use the model is downloaded from the same S3 URL ChromaDB uses
///         and cached to <c>~/.cache/chroma/onnx_models/all-MiniLM-L6-v2/onnx/</c>.
///         SHA-256 is verified before extraction.
///     </para>
///     <para>Use <see cref="CreateAsync"/> to construct; implements <see cref="IDisposable"/>.</para>
/// </summary>
/// <inheritdoc cref="IEmbeddingGenerator{TInput,TEmbedding}"/>
public sealed class DefaultEmbeddingProvider
    : IEmbeddingGenerator<ReadOnlyMemory<char>, Embedding<float>>, IDisposable, IAsyncDisposable
{
    // Same constants as Python ONNXMiniLM_L6_V2
    public const string ModelName = "all-MiniLM-L6-v2";
    public const string ArchiveUrl = "https://chroma-onnx-models.s3.amazonaws.com/all-MiniLM-L6-v2/onnx.tar.gz";
    public const string ArchiveSha256 = "913d7300ceae3b2dbc2c50d1de4baacab4be7b9380491c27fab7418616a16ec3";
    public const int MaxTokens = 256;
    public const int EmbeddingDim = 384;

    // INT8 quantized model — dynamic quantization, ~3–4× faster on ARM64 UDOT
    // ARM64-optimized INT8 — uses UDOT instructions on Apple Silicon
    public const string Int8ModelUrl = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model_qint8_arm64.onnx";

    private static readonly string ModelCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache", "chroma", "onnx_models", ModelName, "onnx");

    private static readonly string Int8CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache", "chroma", "onnx_models", ModelName + "-int8", "onnx");

    private static readonly HttpClient Http = new();

    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private bool _disposed;

    public static int Dimensions => EmbeddingDim;

    private DefaultEmbeddingProvider(InferenceSession session, BertTokenizer tokenizer)
    {
        _session = session;
        _tokenizer = tokenizer;
    }

    /// <summary>
    ///     Creates and initialises the provider, downloading the ONNX model if needed.
    /// </summary>
    /// <param name="useInt8">Use INT8 quantized model (~23 MB, ~3–4× faster inference).</param>
    public static async Task<DefaultEmbeddingProvider> CreateAsync(
        bool useInt8 = false, CancellationToken ct = default)
    {
        Console.WriteLine("[embedder] ensuring model cache...");
        await EnsureModelAsync(ct).ConfigureAwait(false);

        string vocabPath, modelPath;
        if (useInt8)
        {
            vocabPath = Path.Combine(ModelCacheDir, "vocab.txt"); // reuse float32 vocab
            modelPath = Path.Combine(Int8CacheDir, "model_qint8_arm64.onnx");
            await EnsureInt8ModelAsync(modelPath, ct).ConfigureAwait(false);
        }
        else
        {
            vocabPath = Path.Combine(ModelCacheDir, "vocab.txt");
            modelPath = Path.Combine(ModelCacheDir, "model.onnx");
        }

        Console.WriteLine("[embedder] loading tokenizer...");
        var tokenizer = await BertTokenizer.CreateAsync(vocabPath, new BertOptions { LowerCaseBeforeTokenization = true }, ct);

        var label = useInt8 ? "~23 MB INT8 quantized" : "~90 MB float32";
        Console.WriteLine($"[embedder] loading ONNX session ({label})...");
        OnnxNativeResolver.EnsureRegistered();
        var opts = new SessionOptions();
        opts.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR;
        opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

        var session = new InferenceSession(modelPath, opts);
        Console.WriteLine("[embedder] ready.");

        return new DefaultEmbeddingProvider(session, tokenizer);
    }

    // -------------------------------------------------------------------------
    // IEmbeddingGenerator<string, Embedding<float>>
    // -------------------------------------------------------------------------

    /// <summary>
    ///     Generates embeddings for <paramref name="values"/>.
    ///     Each <see cref="Embedding{T}.Vector"/> is a <see cref="ReadOnlyMemory{T}"/>
    ///     backed by a managed <c>float[]</c> — compatible with
    ///     <c>Microsoft.Extensions.VectorData</c> vector properties.
    /// </summary>
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<ReadOnlyMemory<char>> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        const int batchSize = 32;
        var texts = values is IReadOnlyList<ReadOnlyMemory<char>> list ? list : values.ToList();
        var embeddings = new List<Embedding<float>>(texts.Count);

        for (int i = 0; i < texts.Count; i += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int end = Math.Min(i + batchSize, texts.Count);
            var batch = new ReadOnlyMemory<char>[end - i];
            for (int j = 0; j < batch.Length; j++) batch[j] = texts[i + j];

            var batchFloats = EmbedBatch(batch);
            foreach (var vec in batchFloats)
                embeddings.Add(new Embedding<float>(vec));
        }

        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
    }

    // IEmbeddingGenerator (non-generic base) service locator
    object? IEmbeddingGenerator.GetService(Type serviceType, object? key)
        => serviceType.IsInstanceOfType(this) ? this : null;

    // -------------------------------------------------------------------------
    // Inference pipeline (mirrors Python _forward)
    // -------------------------------------------------------------------------

    private float[][] EmbedBatch(ReadOnlyMemory<char>[] texts)
    {
        int batch   = texts.Length;
        int flatLen = batch * MaxTokens;

        var inputIds      = ArrayPool<long>.Shared.Rent(flatLen);
        var attentionMask = ArrayPool<long>.Shared.Rent(flatLen);
        var tokenTypeIds  = ArrayPool<long>.Shared.Rent(flatLen);

        // token_type_ids is always all-zero — one vectorised AVX sweep before the loop
        Unsafe.InitBlockUnaligned(
            ref Unsafe.As<long, byte>(ref tokenTypeIds[0]),
            0,
            (uint)(flatLen * sizeof(long)));

        for (int b = 0; b < batch; b++)
        {
            // BertTokenizer adds [CLS]=101 and [SEP]=102 automatically.
            // maxTokenCount caps the result (including special tokens) to MaxTokens.
            var chars = texts[b].Span;

            // Strip invalid Unicode — scan first, compact into pooled buf only if dirty
            bool dirty = false;
            foreach (var c in chars)
                if (char.IsSurrogate(c) || c == '\uFFFE' || c == '\uFFFF') { dirty = true; break; }

            ReadOnlySpan<char> tokenInput;
            char[]? cleanBuf = null;
            if (dirty)
            {
                cleanBuf = System.Buffers.ArrayPool<char>.Shared.Rent(chars.Length);
                int w = 0;
                foreach (var c in chars)
                    if (!char.IsSurrogate(c) && c != '\uFFFE' && c != '\uFFFF') cleanBuf[w++] = c;
                tokenInput = cleanBuf.AsSpan(0, w);
            }
            else
            {
                tokenInput = chars;
            }

            // Normalization — skip alloc for pure-ASCII (always NFC)
            string? normalized = null;
            bool hasNonAscii = false;
            foreach (var c in tokenInput)
                if (c > 127) { hasNonAscii = true; break; }
            if (hasNonAscii)
            {
                var s = new string(tokenInput);
                if (!s.IsNormalized())
                {
                    normalized = s.Normalize();
                    tokenInput = normalized.AsSpan();
                }
            }

            var ids = _tokenizer.EncodeToIds(tokenInput, MaxTokens, out _, out _);
            if (cleanBuf is not null) System.Buffers.ArrayPool<char>.Shared.Return(cleanBuf);
            int len = ids.Count;

            int baseIdx  = b * MaxTokens;
            int padStart = baseIdx + len;
            int padLen   = MaxTokens - len;

            for (int t = 0; t < len; t++)
                inputIds[baseIdx + t] = ids[t];
            // Zero padding slots — rented array is dirty; guard padLen>0 to avoid
            // out-of-bounds ref when len==MaxTokens (text fills all slots exactly)
            if (padLen > 0)
            {
                Unsafe.InitBlockUnaligned(
                    ref Unsafe.As<long, byte>(ref inputIds[padStart]),
                    0,
                    (uint)(padLen * sizeof(long)));
            }

            for (int t = 0; t < len; t++)
                attentionMask[baseIdx + t] = 1L;
            if (padLen > 0)
            {
                Unsafe.InitBlockUnaligned(
                    ref Unsafe.As<long, byte>(ref attentionMask[padStart]),
                    0,
                    (uint)(padLen * sizeof(long)));
            }
        }

        int[] dims = [batch, MaxTokens];

        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor("input_ids",
                new DenseTensor<long>(inputIds.AsMemory(0, flatLen), dims)),
            NamedOnnxValue.CreateFromTensor("attention_mask",
                new DenseTensor<long>(attentionMask.AsMemory(0, flatLen), dims)),
            NamedOnnxValue.CreateFromTensor("token_type_ids",
                new DenseTensor<long>(tokenTypeIds.AsMemory(0, flatLen), dims)),
        };

        using var outputs = _session.Run(inputs);
        ArrayPool<long>.Shared.Return(inputIds);
        ArrayPool<long>.Shared.Return(attentionMask);
        ArrayPool<long>.Shared.Return(tokenTypeIds);
        // last_hidden_state: [batch, MaxTokens, EmbeddingDim]
        var hidden = outputs[0].AsTensor<float>();

        var result = new float[batch][];
        for (int b = 0; b < batch; b++)
        {
            var emb = new float[EmbeddingDim];
            float maskSum = 0f;

            int baseIdx = b * MaxTokens;
            for (int t = 0; t < MaxTokens; t++)
            {
                float mask = attentionMask[baseIdx + t]; // 0 or 1
                if (mask == 0f) continue;
                maskSum += mask;
                for (int d = 0; d < EmbeddingDim; d++)
                    emb[d] += hidden[b, t, d] * mask;
            }

            // Mean pool (clip denominator to avoid div-by-zero)
            maskSum = MathF.Max(maskSum, 1e-9f);
            for (int d = 0; d < EmbeddingDim; d++)
                emb[d] /= maskSum;

            // L2 normalise (PyTorch default eps = 1e-12)
            float norm = 0f;
            for (int d = 0; d < EmbeddingDim; d++)
                norm += emb[d] * emb[d];
            norm = MathF.Max(MathF.Sqrt(norm), 1e-12f);
            for (int d = 0; d < EmbeddingDim; d++)
                emb[d] /= norm;

            result[b] = emb;
        }

        return result;
    }

    // -------------------------------------------------------------------------
    // Model bootstrap
    // -------------------------------------------------------------------------

    private static async Task EnsureModelAsync(CancellationToken ct)
    {
        var modelPath     = Path.Combine(ModelCacheDir, "model.onnx");
        var tokenizerPath = Path.Combine(ModelCacheDir, "tokenizer.json");

        if (File.Exists(modelPath) && File.Exists(tokenizerPath))
            return;

        var parentDir    = Path.GetDirectoryName(ModelCacheDir)!;
        var archivePath  = Path.Combine(parentDir, "onnx.tar.gz");

        Directory.CreateDirectory(parentDir);

        if (!File.Exists(archivePath) || !VerifySha256(archivePath, ArchiveSha256))
        {
            Console.WriteLine($"[embedder] downloading model from {ArchiveUrl} ...");
            await DownloadFileAsync(ArchiveUrl, archivePath, ct).ConfigureAwait(false);
            Console.WriteLine("[embedder] download complete, verifying SHA-256...");

            if (!VerifySha256(archivePath, ArchiveSha256))
            {
                File.Delete(archivePath);
                throw new InvalidOperationException(
                    "Downloaded ONNX model archive SHA-256 mismatch — possible corruption or tampering.");
            }
        }

        Console.WriteLine("[embedder] extracting archive...");
        ExtractTarGz(archivePath, parentDir);
        Console.WriteLine("[embedder] model extracted.");
    }

    private static async Task EnsureInt8ModelAsync(string modelPath, CancellationToken ct)
    {
        if (File.Exists(modelPath)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        Console.WriteLine($"[embedder] downloading INT8 model from HuggingFace (~23 MB)...");
        await DownloadFileAsync(Int8ModelUrl, modelPath, ct).ConfigureAwait(false);
        Console.WriteLine("[embedder] INT8 model downloaded.");
    }

    private static async Task DownloadFileAsync(string url, string dest, CancellationToken ct)
    {
        using var response = await Http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);
        await response.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
    }

    private static void ExtractTarGz(string archivePath, string destDir)
    {
        using var fs = File.OpenRead(archivePath);
        using var gz = new System.IO.Compression.GZipStream(
            fs, System.IO.Compression.CompressionMode.Decompress);
        System.Formats.Tar.TarFile.ExtractToDirectory(gz, destDir, overwriteFiles: true);
    }

    private static bool VerifySha256(string path, string expectedHex)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).Equals(expectedHex, StringComparison.OrdinalIgnoreCase);
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

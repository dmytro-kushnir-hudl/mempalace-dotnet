using System.Buffers;
using System.Formats.Tar;
using System.IO.Compression;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntime;
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
        try
        {
            NativeLibrary.SetDllImportResolver(
                typeof(OrtEnv).Assembly,
                static (name, _, path) =>
                {
                    if (!name.Equals("onnxruntime.dll", StringComparison.OrdinalIgnoreCase))
                        return IntPtr.Zero;
                    var candidate = Path.Combine(AppContext.BaseDirectory, "libonnxruntime.dylib");
                    if (NativeLibrary.TryLoad(candidate, out var h)) return h;
                    if (NativeLibrary.TryLoad("libonnxruntime.dylib", out h)) return h;
                    return IntPtr.Zero;
                });
        }
        catch (InvalidOperationException)
        {
            // Resolver already registered by another component
        }
    }
}

public enum EmbeddingModel
{
    /// <summary>all-MiniLM-L6-v2 — 256 tokens, 384 dims. ChromaDB default.</summary>
    MiniLM,
    /// <summary>bge-small-en-v1.5 — 512 tokens, 384 dims. Better recall on longer chunks.</summary>
    BgeSmall,
}

/// <summary>
///     ONNX CPU embedding provider supporting MiniLM-L6-v2 and BGE-small-en-v1.5.
///     Mean pooling + L2 normalisation, INT8 quantized by default.
///     <para>Use <see cref="CreateAsync"/> to construct.</para>
/// </summary>
public sealed class DefaultEmbeddingProvider
    : IEmbeddingGenerator<ReadOnlyMemory<char>, Embedding<float>>, IDisposable, IAsyncDisposable
{
    public const int EmbeddingDim = 384;

    // ── MiniLM-L6-v2 ────────────────────────────────────────────────────────
    private static readonly string MiniLmDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache", "chroma", "onnx_models", "all-MiniLM-L6-v2", "onnx");
    private static readonly string MiniLmInt8Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache", "chroma", "onnx_models", "all-MiniLM-L6-v2-int8", "onnx");
    private const string MiniLmArchiveUrl  = "https://chroma-onnx-models.s3.amazonaws.com/all-MiniLM-L6-v2/onnx.tar.gz";
    private const string MiniLmArchiveSha  = "913d7300ceae3b2dbc2c50d1de4baacab4be7b9380491c27fab7418616a16ec3";
    private const string MiniLmInt8Url     = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model_qint8_arm64.onnx";

    // ── BGE-small-en-v1.5 ────────────────────────────────────────────────────
    private static readonly string BgeSmallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache", "rag-pg", "bge-small-en-v1.5");
    private const string BgeSmallBaseUrl   = "https://huggingface.co/BAAI/bge-small-en-v1.5/resolve/main";
    private const string BgeSmallInt8Url   = "https://huggingface.co/keisuke-miyako/bge-small-en-v1.5-onnx-int8/resolve/b51639d8a18149c302c5f6e9701cf14db8dfc296/model_quantized.onnx";

    private static readonly HttpClient Http = new() { DefaultRequestHeaders = { { "User-Agent", "mempalace/1.0" } } };

    // Static RunOptions and input names — avoids per-call allocation
    private static readonly RunOptions s_runOptions = new();
    private static readonly string[] s_inputNames = ["input_ids", "attention_mask", "token_type_ids"];

    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly int _maxTokens;
    private bool _disposed;

    public static int Dimensions => EmbeddingDim;
    public BertTokenizer Tokenizer => _tokenizer;
    public int MaxTokens => _maxTokens;

    private DefaultEmbeddingProvider(InferenceSession session, BertTokenizer tokenizer, int maxTokens)
    {
        _session   = session;
        _tokenizer = tokenizer;
        _maxTokens = maxTokens;
    }

    /// <summary>Creates and initialises the provider, downloading the model if needed.</summary>
    /// <param name="model">Which model to load. Default: <see cref="EmbeddingModel.MiniLM"/>.</param>
    /// <param name="useInt8">INT8 quantized — ~3–4× faster via UDOT NEON, much smaller. Default: <c>true</c>.</param>
    public static async Task<DefaultEmbeddingProvider> CreateAsync(
        EmbeddingModel model = EmbeddingModel.MiniLM,
        bool useInt8 = true,
        CancellationToken ct = default)
    {
        Console.WriteLine("[embedder] ensuring model cache...");

        string vocabPath, modelPath;
        int maxTokens;
        bool lowerCase;

        switch (model)
        {
            case EmbeddingModel.BgeSmall:
                maxTokens = 512;
                lowerCase = false; // BGE is cased
                (vocabPath, modelPath) = await EnsureBgeSmallAsync(useInt8, ct).ConfigureAwait(false);
                break;

            default: // MiniLM
                maxTokens = 256;
                lowerCase = true;
                (vocabPath, modelPath) = await EnsureMiniLmAsync(useInt8, ct).ConfigureAwait(false);
                break;
        }

        Console.WriteLine("[embedder] loading tokenizer...");
        var tokenizer = await BertTokenizer.CreateAsync(
            vocabPath, new BertOptions { LowerCaseBeforeTokenization = lowerCase }, ct);

        var label = useInt8 ? "INT8 quantized" : "float32";
        Console.WriteLine($"[embedder] loading ONNX session ({model} {label}, {maxTokens} tok)...");
        OnnxNativeResolver.EnsureRegistered();

        var session = new InferenceSession(modelPath, CreateCpuSessionOptions());
        Console.WriteLine("[embedder] ready.");

        return new DefaultEmbeddingProvider(session, tokenizer, maxTokens);
    }

    // -------------------------------------------------------------------------
    // IEmbeddingGenerator<string, Embedding<float>>
    // -------------------------------------------------------------------------

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

            foreach (var vec in EmbedBatch(batch))
                embeddings.Add(new Embedding<float>(vec));
        }

        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
    }

    object? IEmbeddingGenerator.GetService(Type serviceType, object? key)
        => serviceType.IsInstanceOfType(this) ? this : null;

    // -------------------------------------------------------------------------
    // Full batch, dynamic seq len — trims padding to actual max in batch,
    // avoiding wasted compute on short chunks.
    // -------------------------------------------------------------------------

    private float[][] EmbedBatch(ReadOnlyMemory<char>[] texts)
    {
        var batchCount = texts.Length;

        // Tokenize all texts first — needed to compute per-batch maxSeqLen
        var allIds = new IReadOnlyList<int>[batchCount];
        var maxSeqLen = 1;
        for (var b = 0; b < batchCount; b++)
        {
            allIds[b] = TokenizeText(texts[b].Span);
            maxSeqLen = Math.Max(maxSeqLen, allIds[b].Count);
        }

        var totalTokens = batchCount * maxSeqLen;
        var inputIds = ArrayPool<long>.Shared.Rent(totalTokens);
        var attnMask = ArrayPool<long>.Shared.Rent(totalTokens);
        var typeIds  = ArrayPool<long>.Shared.Rent(totalTokens);

        try
        {
            inputIds.AsSpan(0, totalTokens).Clear();
            attnMask.AsSpan(0, totalTokens).Clear();
            typeIds.AsSpan(0, totalTokens).Clear();

            for (var b = 0; b < batchCount; b++)
            {
                var ids = allIds[b];
                var rowBase = b * maxSeqLen;
                var len = Math.Min(ids.Count, maxSeqLen);
                for (var t = 0; t < len; t++)
                {
                    inputIds[rowBase + t] = ids[t];
                    attnMask[rowBase + t] = 1L;
                }
            }

            var shape = new long[] { batchCount, maxSeqLen };
            var info = OrtMemoryInfo.DefaultInstance;

            using var inputIdsVal = OrtValue.CreateTensorValueFromMemory(info, inputIds.AsMemory(0, totalTokens), shape);
            using var attnMaskVal = OrtValue.CreateTensorValueFromMemory(info, attnMask.AsMemory(0, totalTokens), shape);
            using var typeIdsVal  = OrtValue.CreateTensorValueFromMemory(info, typeIds.AsMemory(0, totalTokens), shape);
            OrtValue[] inputValues = [inputIdsVal, attnMaskVal, typeIdsVal];

            using var outputs = _session.Run(s_runOptions, s_inputNames, inputValues, _session.OutputNames);
            var hidden = outputs[0].GetTensorDataAsSpan<float>();

            var result = new float[batchCount][];
            for (var b = 0; b < batchCount; b++)
                result[b] = PoolEmbedding(hidden, attnMask, b, maxSeqLen);

            return result;
        }
        finally
        {
            // Return AFTER the pooling loop has finished reading attnMask
            ArrayPool<long>.Shared.Return(inputIds);
            ArrayPool<long>.Shared.Return(attnMask);
            ArrayPool<long>.Shared.Return(typeIds);
        }
    }

    // -------------------------------------------------------------------------
    // Tokenize one text — strips surrogates + NFC normalises non-ASCII
    // -------------------------------------------------------------------------

    private IReadOnlyList<int> TokenizeText(ReadOnlySpan<char> chars)
    {
        bool dirty = false;
        foreach (var c in chars)
            if (char.IsSurrogate(c) || c == '\uFFFE' || c == '\uFFFF') { dirty = true; break; }

        char[]? rentedClean = null;
        int cleanLen = 0;
        if (dirty)
        {
            rentedClean = ArrayPool<char>.Shared.Rent(chars.Length);
            foreach (var c in chars)
                if (!char.IsSurrogate(c) && c != '\uFFFE' && c != '\uFFFF') rentedClean[cleanLen++] = c;
        }

        ReadOnlySpan<char> input = dirty ? rentedClean!.AsSpan(0, cleanLen) : chars;

        // NFC normalise non-ASCII — lazy: skip pure-ASCII strings (always NFC)
        string? normalized = null;
        bool hasNonAscii = false;
        foreach (var c in input)
            if (c > 127) { hasNonAscii = true; break; }
        if (hasNonAscii)
        {
            var s = new string(input);
            if (!s.IsNormalized())
                normalized = s.Normalize();
        }

        var tokenInput = normalized is not null ? normalized.AsSpan() : input;
        var ids = _tokenizer.EncodeToIds(tokenInput, _maxTokens, normalizedText: out _, charsConsumed: out _);

        if (rentedClean is not null) ArrayPool<char>.Shared.Return(rentedClean);
        return ids;
    }

    // -------------------------------------------------------------------------
    // SIMD mean pooling + L2 normalisation via TensorPrimitives
    // -------------------------------------------------------------------------

    private static float[] PoolEmbedding(ReadOnlySpan<float> hidden, long[] attnMask, int batchIdx, int seqLen)
    {
        var emb = new float[EmbeddingDim];
        var rowOffset = batchIdx * seqLen;
        float maskSum = 0f;

        for (var t = 0; t < seqLen; t++)
        {
            if (attnMask[rowOffset + t] == 0) continue;
            maskSum++;
            TensorPrimitives.Add(
                (ReadOnlySpan<float>)emb,
                hidden.Slice((rowOffset + t) * EmbeddingDim, EmbeddingDim),
                emb);
        }

        maskSum = MathF.Max(maskSum, 1e-9f);
        TensorPrimitives.Divide((ReadOnlySpan<float>)emb, maskSum, emb);

        var norm = TensorPrimitives.Norm((ReadOnlySpan<float>)emb);
        norm = MathF.Max(norm, 1e-12f);
        TensorPrimitives.Divide((ReadOnlySpan<float>)emb, norm, emb);

        return emb;
    }

    // -------------------------------------------------------------------------
    // Session options — CPU, P-cores only, BERT-optimized
    // -------------------------------------------------------------------------

    private static SessionOptions CreateCpuSessionOptions()
    {
        var opts = new SessionOptions();
        opts.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR;
        // Level 3: layout optimizations + op fusion (critical for BERT)
        opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        // BERT layers are linearly dependent — sequential is optimal
        opts.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
        // Pre-allocate contiguous memory blocks for activations
        opts.EnableMemoryPattern = true;
        opts.EnableCpuMemArena = true;
        // Cap at 10 to stay on P-cores only (Apple Silicon / modern Intel)
        opts.IntraOpNumThreads = Math.Min(Environment.ProcessorCount, 10);
        // Thread spinning keeps P-cores awake between ops, minimising context-switch latency
        opts.AddSessionConfigEntry("session.intra_op.thread_affinities", "1");
        return opts;
    }

    // -------------------------------------------------------------------------
    // Model bootstrap
    // -------------------------------------------------------------------------

    private static async Task<(string VocabPath, string ModelPath)> EnsureMiniLmAsync(
        bool useInt8, CancellationToken ct)
    {
        var vocabPath = Path.Combine(MiniLmDir, "vocab.txt");
        var modelPath = Path.Combine(MiniLmDir, "model.onnx");

        if (!File.Exists(modelPath))
        {
            var parentDir   = Path.GetDirectoryName(MiniLmDir)!;
            var archivePath = Path.Combine(parentDir, "onnx.tar.gz");
            Directory.CreateDirectory(parentDir);

            if (!File.Exists(archivePath) || !VerifySha256(archivePath, MiniLmArchiveSha))
            {
                Console.WriteLine($"[embedder] downloading MiniLM (~90 MB)...");
                await DownloadAsync(MiniLmArchiveUrl, archivePath, ct).ConfigureAwait(false);
                if (!VerifySha256(archivePath, MiniLmArchiveSha))
                {
                    File.Delete(archivePath);
                    throw new InvalidOperationException("MiniLM archive SHA-256 mismatch.");
                }
            }
            using var fs = File.OpenRead(archivePath);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gz, parentDir, overwriteFiles: true);
        }

        if (!useInt8) return (vocabPath, modelPath);

        var int8Path = Path.Combine(MiniLmInt8Dir, "model_qint8_arm64.onnx");
        if (!File.Exists(int8Path))
        {
            Directory.CreateDirectory(MiniLmInt8Dir);
            Console.WriteLine("[embedder] downloading MiniLM INT8 (~23 MB)...");
            await DownloadAsync(MiniLmInt8Url, int8Path, ct).ConfigureAwait(false);
        }
        return (vocabPath, int8Path);
    }

    private static async Task<(string VocabPath, string ModelPath)> EnsureBgeSmallAsync(
        bool useInt8, CancellationToken ct)
    {
        Directory.CreateDirectory(BgeSmallDir);

        var vocabPath = Path.Combine(BgeSmallDir, "vocab.txt");
        if (!File.Exists(vocabPath))
        {
            Console.WriteLine("[embedder] downloading BGE-small vocab...");
            await DownloadAsync($"{BgeSmallBaseUrl}/vocab.txt", vocabPath, ct).ConfigureAwait(false);
        }

        if (useInt8)
        {
            var int8Path = Path.Combine(BgeSmallDir, "model.int8.onnx");
            if (!File.Exists(int8Path))
            {
                Console.WriteLine("[embedder] downloading BGE-small INT8 (~23 MB)...");
                await DownloadAsync(BgeSmallInt8Url, int8Path, ct).ConfigureAwait(false);
            }
            return (vocabPath, int8Path);
        }

        var modelPath = Path.Combine(BgeSmallDir, "model.onnx");
        if (!File.Exists(modelPath))
        {
            Console.WriteLine("[embedder] downloading BGE-small float32 (~133 MB)...");
            await DownloadAsync($"{BgeSmallBaseUrl}/onnx/model.onnx", modelPath, ct).ConfigureAwait(false);
        }
        return (vocabPath, modelPath);
    }

    private static async Task DownloadAsync(string url, string dest, CancellationToken ct)
    {
        using var response = await Http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);
        await response.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
    }

    private static bool VerifySha256(string path, string expectedHex)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
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

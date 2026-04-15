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
            // Resolver already registered by another component (ORT's own AOT initializer)
        }
    }
}

/// <summary>Requested / active ONNX execution provider.</summary>
public enum ExecutionProvider { Auto, Cpu, CoreML }

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

    // INT8 quantized model — ARM64-optimized, uses UDOT instructions on Apple Silicon
    public const string Int8ModelUrl = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model_qint8_arm64.onnx";

    private static readonly string ModelCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache", "chroma", "onnx_models", ModelName, "onnx");

    private static readonly string Int8CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache", "chroma", "onnx_models", ModelName + "-int8", "onnx");

    private static readonly HttpClient Http = new();

    // Static RunOptions and input names — avoids per-call allocation
    private static readonly RunOptions s_runOptions = new();
    private static readonly string[] s_inputNames = ["input_ids", "attention_mask", "token_type_ids"];

    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly ExecutionProvider _activeProvider;
    private bool _disposed;

    public static int Dimensions => EmbeddingDim;
    public ExecutionProvider ActiveProvider => _activeProvider;

    private DefaultEmbeddingProvider(InferenceSession session, BertTokenizer tokenizer, ExecutionProvider activeProvider)
    {
        _session = session;
        _tokenizer = tokenizer;
        _activeProvider = activeProvider;
    }

    /// <summary>
    ///     Creates and initialises the provider, downloading the ONNX model if needed.
    /// </summary>
    /// <param name="useInt8">Use INT8 quantized model (~23 MB, ~3–4× faster inference).</param>
    /// <param name="provider">
    ///     <see cref="ExecutionProvider.Auto"/> (default) tries CoreML on macOS, falls back to CPU.
    ///     <see cref="ExecutionProvider.CoreML"/> routes to Apple ANE/GPU on macOS Apple Silicon.
    ///     <see cref="ExecutionProvider.Cpu"/> uses ONNX CPU provider with all graph optimizations.
    /// </param>
    public static async Task<DefaultEmbeddingProvider> CreateAsync(
        bool useInt8 = false,
        ExecutionProvider provider = ExecutionProvider.Auto,
        CancellationToken ct = default)
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

        var (session, activeProvider) = CreateSession(modelPath, provider);
        Console.WriteLine($"[embedder] ready ({activeProvider}).");

        return new DefaultEmbeddingProvider(session, tokenizer, activeProvider);
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

            // ANE: one ONNX call per item with fixed [1, MaxTokens] shape.
            // CPU: full batch with dynamic seq len to avoid wasted compute on short chunks.
            var batchFloats = _activeProvider == ExecutionProvider.CoreML
                ? EmbedBatchCoreML(batch)
                : EmbedBatchCpu(batch);

            foreach (var vec in batchFloats)
                embeddings.Add(new Embedding<float>(vec));
        }

        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
    }

    // IEmbeddingGenerator (non-generic base) service locator
    object? IEmbeddingGenerator.GetService(Type serviceType, object? key)
        => serviceType.IsInstanceOfType(this) ? this : null;

    // -------------------------------------------------------------------------
    // CPU path — full batch, dynamic seq len to skip padding on short chunks
    // -------------------------------------------------------------------------

    private float[][] EmbedBatchCpu(ReadOnlyMemory<char>[] texts)
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
            // Return AFTER the pooling loop above has finished reading attnMask
            ArrayPool<long>.Shared.Return(inputIds);
            ArrayPool<long>.Shared.Return(attnMask);
            ArrayPool<long>.Shared.Return(typeIds);
        }
    }

    // -------------------------------------------------------------------------
    // CoreML path — static shape [1, MaxTokens] per item, delegates to ANE/GPU
    // -------------------------------------------------------------------------

    private float[][] EmbedBatchCoreML(ReadOnlyMemory<char>[] texts)
    {
        var batchCount = texts.Length;
        var result = new float[batchCount][];

        var inputIds = ArrayPool<long>.Shared.Rent(MaxTokens);
        var attnMask = ArrayPool<long>.Shared.Rent(MaxTokens);
        var typeIds  = ArrayPool<long>.Shared.Rent(MaxTokens);
        typeIds.AsSpan(0, MaxTokens).Clear(); // token_type_ids is always 0

        try
        {
            var shape = new long[] { 1L, MaxTokens };
            var info = OrtMemoryInfo.DefaultInstance;

            for (var b = 0; b < batchCount; b++)
            {
                var ids = TokenizeText(texts[b].Span);

                inputIds.AsSpan(0, MaxTokens).Clear();
                attnMask.AsSpan(0, MaxTokens).Clear();
                var len = Math.Min(ids.Count, MaxTokens);
                for (var t = 0; t < len; t++)
                {
                    inputIds[t] = ids[t];
                    attnMask[t] = 1L;
                }

                using var inputIdsVal = OrtValue.CreateTensorValueFromMemory(info, inputIds.AsMemory(0, MaxTokens), shape);
                using var attnMaskVal = OrtValue.CreateTensorValueFromMemory(info, attnMask.AsMemory(0, MaxTokens), shape);
                using var typeIdsVal  = OrtValue.CreateTensorValueFromMemory(info, typeIds.AsMemory(0, MaxTokens), shape);
                OrtValue[] inputValues = [inputIdsVal, attnMaskVal, typeIdsVal];

                using var outputs = _session.Run(s_runOptions, s_inputNames, inputValues, _session.OutputNames);
                result[b] = PoolEmbedding(outputs[0].GetTensorDataAsSpan<float>(), attnMask, 0, MaxTokens);
            }
        }
        finally
        {
            ArrayPool<long>.Shared.Return(inputIds);
            ArrayPool<long>.Shared.Return(attnMask);
            ArrayPool<long>.Shared.Return(typeIds);
        }

        return result;
    }

    // -------------------------------------------------------------------------
    // Tokenize one text — strips surrogates + NFC normalises non-ASCII
    // -------------------------------------------------------------------------

    private IReadOnlyList<int> TokenizeText(ReadOnlySpan<char> chars)
    {
        // Strip surrogates and reserved Unicode code points
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
        var ids = _tokenizer.EncodeToIds(tokenInput, MaxTokens, normalizedText: out _, charsConsumed: out _);

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

        // Mean pool then L2 normalise
        maskSum = MathF.Max(maskSum, 1e-9f);
        TensorPrimitives.Divide((ReadOnlySpan<float>)emb, maskSum, emb);

        var norm = TensorPrimitives.Norm((ReadOnlySpan<float>)emb);
        norm = MathF.Max(norm, 1e-12f);
        TensorPrimitives.Divide((ReadOnlySpan<float>)emb, norm, emb);

        return emb;
    }

    // -------------------------------------------------------------------------
    // Session factory — mirrors rag-pg OnnxEmbedder.CreateSession
    // -------------------------------------------------------------------------

    private static (InferenceSession Session, ExecutionProvider ActiveProvider) CreateSession(
        string modelPath, ExecutionProvider provider)
    {
        switch (provider)
        {
            case ExecutionProvider.Cpu:
                return (new InferenceSession(modelPath, CreateCpuSessionOptions()), ExecutionProvider.Cpu);

            case ExecutionProvider.CoreML:
                return (new InferenceSession(modelPath, CreateCoreMlSessionOptions()),
                    ExecutionProvider.CoreML);

            case ExecutionProvider.Auto:
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    return (new InferenceSession(modelPath, CreateCpuSessionOptions()), ExecutionProvider.Cpu);
                try
                {
                    return (new InferenceSession(modelPath, CreateCoreMlSessionOptions()),
                        ExecutionProvider.CoreML);
                }
                catch (Exception ex) when (ex is OnnxRuntimeException or NotSupportedException)
                {
                    Console.WriteLine($"[embedder] CoreML unavailable, falling back to CPU: {ex.Message}");
                    return (new InferenceSession(modelPath, CreateCpuSessionOptions()), ExecutionProvider.Cpu);
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(provider));
        }
    }

    private static SessionOptions CreateBaseSessionOptions()
    {
        var opts = new SessionOptions();
        opts.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR;
        // Level 3: includes layout optimizations and op fusion (critical for BERT)
        opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        // BERT layers are linearly dependent — sequential execution is optimal
        opts.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
        // Pre-allocate contiguous memory blocks for activations
        opts.EnableMemoryPattern = true;
        opts.EnableCpuMemArena = true;
        return opts;
    }

    private static SessionOptions CreateCpuSessionOptions()
    {
        var opts = CreateBaseSessionOptions();
        opts.IntraOpNumThreads = Environment.ProcessorCount;
        // Thread spinning keeps threads awake between ops, reducing latency
        opts.AddSessionConfigEntry("session.intra_op.thread_affinities", "1");
        return opts;
    }

    private static SessionOptions CreateCoreMlSessionOptions()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            throw new NotSupportedException("CoreML requires macOS.");
        var opts = CreateBaseSessionOptions();
        // Keep CPU thread pool minimal — ANE/GPU handles computation
        opts.IntraOpNumThreads = 1;
        // MLPROGRAM is the modern CoreML backend required to dispatch to Apple Neural Engine
        opts.AppendExecutionProvider_CoreML(
            CoreMLFlags.COREML_FLAG_ONLY_ALLOW_STATIC_INPUT_SHAPES |
            CoreMLFlags.COREML_FLAG_CREATE_MLPROGRAM);
        return opts;
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
        Console.WriteLine("[embedder] downloading INT8 model from HuggingFace (~23 MB)...");
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
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gz, destDir, overwriteFiles: true);
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

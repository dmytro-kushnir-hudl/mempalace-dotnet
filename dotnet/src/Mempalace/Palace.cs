using Mempalace.Storage;

namespace Mempalace;

public enum VectorBackend
{
    Chroma,
    Sqlite
}

/// <summary>
///     Wraps an <see cref="IVectorCollection" /> for a single palace session.
///     Dispose to close the underlying database.
/// </summary>
public sealed class PalaceSession : IDisposable
{
    private bool _disposed;

    private PalaceSession(IVectorCollection collection)
    {
        Collection = collection;
    }

    public IVectorCollection Collection { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Collection.Dispose();
    }

    /// <summary>Opens (or creates) a palace at <paramref name="palacePath" />.</summary>
    public static PalaceSession Open(
        string palacePath,
        string collectionName = Constants.DefaultCollectionName,
        VectorBackend backend = VectorBackend.Chroma)
    {
        Directory.CreateDirectory(palacePath);
        IVectorCollection col = backend switch
        {
            VectorBackend.Chroma => new ChromaVectorCollection(palacePath, collectionName),
            VectorBackend.Sqlite => new SqliteVectorCollection(
                Path.Combine(palacePath, "palace.sqlite3"), 384),
            _ => throw new ArgumentOutOfRangeException(nameof(backend))
        };
        return new PalaceSession(col);
    }

    // ── File-mining helpers ───────────────────────────────────────────────────

    /// <summary>
    ///     Returns true if <paramref name="sourceFile" /> has already been filed.
    ///     When <paramref name="checkMtime" /> is true, also verifies the stored
    ///     modification time matches the current file on disk.
    /// </summary>
    public bool FileAlreadyMined(string sourceFile, bool checkMtime = false)
    {
        var rows = Collection.Get(
            MetadataFilter.Where("source_file", sourceFile),
            limit: 1,
            includeMetadatas: true,
            includeDocuments: false);

        if (rows.Length == 0) return false;
        if (!checkMtime) return true;

        var meta = rows[0].Metadata;
        if (meta is null || !meta.TryGetValue("source_mtime", out var storedRaw))
            return false;

        var storedMtime = storedRaw switch
        {
            double d => d,
            JsonElement je => je.GetDouble(),
            _ => Convert.ToDouble(storedRaw, CultureInfo.InvariantCulture)
        };
        var currentMtime = GetUnixMtime(sourceFile);
        return Math.Abs(storedMtime - currentMtime) < 0.001;
    }

    public static double GetUnixMtime(string path)
    {
        return (File.GetLastWriteTimeUtc(path) - DateTime.UnixEpoch).TotalSeconds;
    }
}
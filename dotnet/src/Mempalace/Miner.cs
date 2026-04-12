using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Mempalace.Storage;
using Microsoft.Extensions.AI;

namespace Mempalace;

// ---------------------------------------------------------------------------
// Data types
// ---------------------------------------------------------------------------

public sealed record Chunk(ReadOnlyMemory<char> Content, int ChunkIndex);

public sealed record MinerOptions(
    string? WingOverride = null,
    string Agent = "mempalace",
    int Limit = 0,
    bool DryRun = false,
    bool RespectGitignore = true,
    IReadOnlyList<string>? IncludeIgnored = null,
    VectorBackend Backend = VectorBackend.Sqlite,
    bool Parallel = false);

// ---------------------------------------------------------------------------
// Miner
// ---------------------------------------------------------------------------

public static class Miner
{
    private const int BatchSize = 32;

    private static readonly ArrayPool<ReadOnlyMemory<char>> DocPool =
        ArrayPool<ReadOnlyMemory<char>>.Shared;

    private static readonly ArrayPool<string> StringPool =
        ArrayPool<string>.Shared;

    private static readonly ArrayPool<Dictionary<string, object?>> MetaPool =
        ArrayPool<Dictionary<string, object?>>.Shared;
    // -------------------------------------------------------------------------
    // ChunkText — mirrors Python chunk_text exactly
    // -------------------------------------------------------------------------

    public static List<Chunk> ChunkText(string content)
    {
        var chunks = new List<Chunk>();
        var mem = content.AsMemory();
        var span = content.AsSpan();
        var start = 0;

        while (start < span.Length)
        {
            var end = Math.Min(start + Constants.ChunkSize, span.Length);

            if (end < span.Length)
            {
                var half = start + Constants.ChunkSize / 2;
                var breakAt = content.LastIndexOf("\n\n", end, end - half, StringComparison.Ordinal);
                if (breakAt == -1)
                    breakAt = content.LastIndexOf('\n', end, end - half);
                if (breakAt != -1)
                    end = breakAt + 1;
            }

            // Compute trim offsets without materializing a new string
            var raw = span[start..end];
            var trimmed = raw.TrimStart();
            var trimStart = raw.Length - trimmed.Length;
            var trimLen = trimmed.TrimEnd().Length;

            if (trimLen >= Constants.MinChunkSize)
                chunks.Add(new Chunk(mem.Slice(start + trimStart, trimLen), chunks.Count));

            start = end < span.Length ? end - Constants.ChunkOverlap : end;
        }

        return chunks;
    }

    // -------------------------------------------------------------------------
    // ID generation — matches Python drawer_id format
    // -------------------------------------------------------------------------

    public static string DrawerId(string wing, string room, string sourceFile, int chunkIndex)
    {
        // Encode directly into a pooled buffer — avoid sourceFile+chunkIndex string alloc
        var maxBytes = Encoding.UTF8.GetMaxByteCount(sourceFile.Length) + 11; // 11 = max int digits
        var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(maxBytes);
        try
        {
            int written = Encoding.UTF8.GetBytes(sourceFile, buf);
            // Append chunkIndex bytes
            int idx = chunkIndex;
            do { buf[written++] = (byte)('0' + idx % 10); idx /= 10; } while (idx > 0);
            Span<byte> hashBytes = stackalloc byte[32];
            SHA256.HashData(buf.AsSpan(0, written), hashBytes);
            // Lower-hex first 12 bytes → 24 chars
            return $"drawer_{wing}_{room}_{Convert.ToHexString(hashBytes[..12]).ToLowerInvariant()}";
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buf); }
    }

    // -------------------------------------------------------------------------
    // AddDrawer
    // -------------------------------------------------------------------------

    public static async Task AddDrawerAsync(
        IVectorCollection collection,
        IEmbeddingGenerator<ReadOnlyMemory<char>, Embedding<float>> embedder,
        string wing,
        string room,
        string content,
        string sourceFile,
        int chunkIndex,
        string agent,
        CancellationToken ct = default)
    {
        var id = DrawerId(wing, room, sourceFile, chunkIndex);
        var metadata = new Dictionary<string, object?>
        {
            ["wing"] = wing,
            ["room"] = room,
            ["source_file"] = sourceFile,
            ["chunk_index"] = (long)chunkIndex,
            ["added_by"] = agent,
            ["filed_at"] = DateTime.UtcNow.ToString("O"),
            ["source_mtime"] = PalaceSession.GetUnixMtime(sourceFile)
        };

        await collection.UpsertAsync(
            [id],
            [content.AsMemory()],
            embedder,
            [metadata],
            ct);
    }

    private static async Task FlushBatchAsync(
        IVectorCollection collection,
        IEmbeddingGenerator<ReadOnlyMemory<char>, Embedding<float>> embedder,
        List<PendingChunk> batch,
        CancellationToken ct)
    {
        if (batch.Count == 0) return;
        var n = batch.Count;
        var now = DateTime.UtcNow.ToString("O");
        var ids = StringPool.Rent(n);
        var docs = DocPool.Rent(n);
        var metas = MetaPool.Rent(n);
        try
        {
            for (var i = 0; i < n; i++)
            {
                var c = batch[i];
                ids[i] = DrawerId(c.Wing, c.Room, c.SourceFile, c.ChunkIndex);
                docs[i] = c.Content;
                metas[i] = new Dictionary<string, object?>(7)
                {
                    ["wing"] = c.Wing,
                    ["room"] = c.Room,
                    ["source_file"] = c.SourceFile,
                    ["chunk_index"] = (long)c.ChunkIndex,
                    ["added_by"] = c.Agent,
                    ["filed_at"] = now,
                    ["source_mtime"] = c.Mtime
                };
            }

            // UpsertAsync only reads [0..n), rented arrays may be larger — pass exact slices
            await collection.UpsertAsync(ids[..n], docs[..n], embedder, metas[..n], ct).ConfigureAwait(false);
        }
        finally
        {
            StringPool.Return(ids, true);
            DocPool.Return(docs, true);
            MetaPool.Return(metas, true);
            batch.Clear();
        }
    }

    // -------------------------------------------------------------------------
    // DetectRoom — priority: folder path → filename → keyword scoring → "general"
    // -------------------------------------------------------------------------

    public static string DetectRoom(
        string filePath,
        string content,
        IReadOnlyList<RoomConfig> rooms,
        string projectPath)
    {
        if (rooms.Count == 0) return "general";

        var relative = Path.GetRelativePath(projectPath, filePath).ToLowerInvariant();
        var filename = Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant();
        var pathParts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // 1. Folder path matches room name or keyword
        foreach (var room in rooms)
        {
            if (pathParts.Any(p => p.Equals(room.Name, StringComparison.OrdinalIgnoreCase))) return room.Name;
            if (room.Keywords.Any(k => pathParts.Any(p => p.Contains(k, StringComparison.OrdinalIgnoreCase))))
                return room.Name;
        }

        // 2. Filename matches
        foreach (var room in rooms)
        {
            if (filename.Contains(room.Name, StringComparison.OrdinalIgnoreCase)) return room.Name;
            if (room.Keywords.Any(k => filename.Contains(k, StringComparison.OrdinalIgnoreCase))) return room.Name;
        }

        // 3. Content keyword scoring
        var scores = rooms
            .Select(r => (Room: r,
                Score: r.Keywords.Count(k => content.Contains(k, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();

        return scores.Room?.Name ?? "general";
    }

    // -------------------------------------------------------------------------
    // Mine — main entry point
    // -------------------------------------------------------------------------

    public static Task MineAsync(
        string projectDir,
        string palacePath,
        IEmbeddingGenerator<ReadOnlyMemory<char>, Embedding<float>> embedder,
        MinerOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new MinerOptions();
        return options.Parallel
            ? MineAsyncPipelined(projectDir, palacePath, embedder, options, ct)
            : MineAsyncSerial(projectDir, palacePath, embedder, options, ct);
    }

    private static async Task MineAsyncPipelined(
        string projectDir,
        string palacePath,
        IEmbeddingGenerator<ReadOnlyMemory<char>, Embedding<float>> embedder,
        MinerOptions options,
        CancellationToken ct)
    {
        projectDir = Path.GetFullPath(projectDir);
        var (wing, rooms) = ResolveWingAndRooms(projectDir, options);
        using var session = PalaceSession.Open(palacePath, backend: options.Backend);
        var minedMtimes = options.DryRun ? [] : session.Collection.LoadMinedMtimes();
        var progress = new MineProgress(projectDir);

        // Channel: producer writes batches, consumer embeds+writes
        var channel = Channel.CreateBounded<(List<PendingChunk> batch, bool last)>(
            new BoundedChannelOptions(4)
                { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });

        var skipped = 0;

        // Producer: enumerate files, read, chunk, fill batches
        var producer = Task.Run(async () =>
        {
            var batch = new List<PendingChunk>(BatchSize);
            var mined = 0;

            foreach (var file in EnumerateMineable(projectDir, options))
            {
                ct.ThrowIfCancellationRequested();

                if (!options.DryRun)
                {
                    var currentMtime = PalaceSession.GetUnixMtime(file);
                    if (minedMtimes.TryGetValue(file, out var stored) && Math.Abs(stored - currentMtime) < 0.001)
                    {
                        progress.File(file, true);
                        Interlocked.Increment(ref skipped);
                        continue;
                    }
                }

                string content;
                double fileMtime;
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > Constants.MaxFileSize) continue;
                    fileMtime = (info.LastWriteTimeUtc - DateTime.UnixEpoch).TotalSeconds;
                    
                    content = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(content)) continue;

                var room = DetectRoom(file, content, rooms, projectDir);
                var chunks = ChunkText(content);
                progress.File(file, false);

                foreach (var chunk in chunks)
                {
                    if (options.DryRun)
                    {
                        Console.WriteLine($"[dry-run] {wing}/{room} chunk {chunk.ChunkIndex}: {file}");
                        continue;
                    }

                    progress.Chunk();
                    batch.Add(new PendingChunk(wing, room, chunk.Content, file, chunk.ChunkIndex, options.Agent,
                        fileMtime));
                    if (batch.Count >= BatchSize)
                    {
                        await channel.Writer.WriteAsync((batch, false), ct).ConfigureAwait(false);
                        batch = new List<PendingChunk>(BatchSize);
                    }
                }

                mined++;
                if (options.Limit > 0 && mined >= options.Limit) break;
            }

            if (batch.Count > 0)
                await channel.Writer.WriteAsync((batch, false), ct).ConfigureAwait(false);

            channel.Writer.Complete();
        }, ct);

        // Consumer: embed + write batches as they arrive
        var minedConsumer = 0;
        await foreach (var (batch, _) in channel.Reader.ReadAllAsync(ct))
        {
            await FlushBatchAsync(session.Collection, embedder, batch, ct).ConfigureAwait(false);
            progress.Flushed(batch.Count);
            minedConsumer++;
        }

        await producer.ConfigureAwait(false); // propagate producer exceptions
        progress.Done(minedConsumer, skipped, wing);
    }

    private static (string Wing, IReadOnlyList<RoomConfig> Rooms) ResolveWingAndRooms(string projectDir,
        MinerOptions options)
    {
        var projectConfig = ProjectConfig.TryLoad(projectDir);
        var wing = options.WingOverride ?? projectConfig?.Wing ?? Path.GetFileName(projectDir);
        var rooms = projectConfig?.Rooms
                    ?? Constants.DefaultHallKeywords
                        .Select(kv => new RoomConfig(kv.Key, null, kv.Value))
                        .Append(RoomConfig.General)
                        .ToList<RoomConfig>();
        return (wing, rooms);
    }

    private static async Task MineAsyncSerial(
        string projectDir,
        string palacePath,
        IEmbeddingGenerator<ReadOnlyMemory<char>, Embedding<float>> embedder,
        MinerOptions options,
        CancellationToken ct)
    {
        projectDir = Path.GetFullPath(projectDir);
        var (wing, rooms) = ResolveWingAndRooms(projectDir, options);
        using var session = PalaceSession.Open(palacePath, backend: options.Backend);

        // Bulk-load already-mined mtimes in one query instead of per-file queries
        var minedMtimes = options.DryRun
            ? new Dictionary<string, double>()
            : session.Collection.LoadMinedMtimes();

        var files = EnumerateMineable(projectDir, options);
        var mined = 0;
        var skipped = 0;
        var batch = new List<PendingChunk>(BatchSize);
        var progress = new MineProgress(projectDir);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            if (!options.DryRun)
            {
                var currentMtime = PalaceSession.GetUnixMtime(file);
                if (minedMtimes.TryGetValue(file, out var storedMtime) &&
                    Math.Abs(storedMtime - currentMtime) < 0.001)
                {
                    progress.File(file, true);
                    skipped++;
                    continue;
                }
            }

            string content;
            double fileMtime;
            try
            {
                var info = new FileInfo(file);
                if (info.Length > Constants.MaxFileSize) continue;
                fileMtime = (info.LastWriteTimeUtc - DateTime.UnixEpoch).TotalSeconds;
                content = await File.ReadAllTextAsync(file, ct);
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(content)) continue;

            var room = DetectRoom(file, content, rooms, projectDir);
            var chunks = ChunkText(content);

            progress.File(file, false);

            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();
                if (options.DryRun)
                {
                    Console.WriteLine($"[dry-run] {wing}/{room} chunk {chunk.ChunkIndex}: {file}");
                    continue;
                }

                progress.Chunk();
                batch.Add(new PendingChunk(wing, room, chunk.Content, file, chunk.ChunkIndex, options.Agent,
                    fileMtime));
                if (batch.Count >= BatchSize)
                {
                    await FlushBatchAsync(session.Collection, embedder, batch, ct).ConfigureAwait(false);
                    progress.Flushed(BatchSize);
                }
            }

            mined++;
            if (options.Limit > 0 && mined >= options.Limit) break;
        }

        if (batch.Count > 0)
        {
            await FlushBatchAsync(session.Collection, embedder, batch, ct).ConfigureAwait(false);
            progress.Flushed(batch.Count);
        }

        if (!options.DryRun)
            progress.Done(mined, skipped, wing);
    }

    // -------------------------------------------------------------------------
    // File enumeration with gitignore support
    // -------------------------------------------------------------------------

    private static IEnumerable<string> EnumerateMineable(string root, MinerOptions options)
    {
        var includeSet = options.IncludeIgnored is not null
            ? new HashSet<string>(options.IncludeIgnored.Select(Path.GetFullPath))
            : null;

        return EnumerateDir(root, root, options.RespectGitignore, includeSet);
    }

    private static IEnumerable<string> EnumerateDir(
        string dir, string root, bool useGitignore,
        HashSet<string>? forceInclude)
    {
        var gitignore = useGitignore ? GitignoreMatcher.TryLoad(dir) : null;

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(dir);
        }
        catch
        {
            yield break;
        }

        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            var isDir = Directory.Exists(entry);

            if (Constants.SkipDirs.Contains(name) && isDir) continue;

            if (gitignore?.IsIgnored(entry, isDir) == true
                && forceInclude?.Contains(entry) != true)
                continue;

            if (isDir)
            {
                foreach (var f in EnumerateDir(entry, root, useGitignore, forceInclude))
                    yield return f;
            }
            else
            {
                var ext = Path.GetExtension(entry);
                if (!Constants.ReadableExtensions.Contains(ext)) continue;
                if (Constants.SkipFilenames.Contains(name)) continue;
                yield return entry;
            }
        }
    }

    private record PendingChunk(
        string Wing,
        string Room,
        ReadOnlyMemory<char> Content,
        string SourceFile,
        int ChunkIndex,
        string Agent,
        double Mtime);
}

// ---------------------------------------------------------------------------
// Minimal gitignore matcher
// ---------------------------------------------------------------------------

internal sealed class GitignoreMatcher
{
    private readonly string _baseDir;
    private readonly IReadOnlyList<GitignoreRule> _rules;

    private GitignoreMatcher(string baseDir, IReadOnlyList<GitignoreRule> rules)
    {
        _baseDir = baseDir;
        _rules = rules;
    }

    public static GitignoreMatcher? TryLoad(string dir)
    {
        var path = Path.Combine(dir, ".gitignore");
        if (!File.Exists(path)) return null;

        var rules = new List<GitignoreRule>();
        foreach (var line in File.ReadLines(path))
        {
            var rule = GitignoreRule.TryParse(line);
            if (rule is not null) rules.Add(rule);
        }

        return rules.Count > 0 ? new GitignoreMatcher(dir, rules) : null;
    }

    public bool IsIgnored(string fullPath, bool isDir)
    {
        var relative = Path.GetRelativePath(_baseDir, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/');

        var ignored = false;
        foreach (var rule in _rules)
        {
            if (rule.DirOnly && !isDir) continue;
            if (rule.Matches(relative))
                ignored = !rule.Negated;
        }

        return ignored;
    }
}

// ---------------------------------------------------------------------------
// Mining progress display
// ---------------------------------------------------------------------------

internal sealed class MineProgress(string root)
{
    private static readonly string[] Spinner = ["⠋", "⠙", "⠚", "⠞", "⠖", "⠦", "⠴", "⠲", "⠳", "⠵"];
    private static readonly bool IsTty = !Console.IsErrorRedirected;

    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private int _chunks;
    private string _current = "";
    private int _files;
    private int _skipped;
    private int _spin;

    public void File(string path, bool skipped)
    {
        if (skipped) _skipped++;
        else _current = Path.GetRelativePath(root, path);
        Render();
    }

    public void Chunk()
    {
        _chunks++;
    }

    public void Flushed(int count)
    {
        _files++;
        _spin = (_spin + 1) % Spinner.Length;
        Render();
    }

    public void Done(int mined, int skipped, string wing)
    {
        if (IsTty) Console.Error.Write("\r" + new string(' ', Math.Max(80, Console.WindowWidth - 1)) + "\r");
        Console.Error.WriteLine(
            $"⛏  done  {mined} mined  {skipped} skipped  {_chunks} chunks  " +
            $"{_chunks / Math.Max(_sw.Elapsed.TotalSeconds, 0.001):0.#}/s  → {wing}");
    }

    private void Render()
    {
        if (!IsTty) return;
        var rate = _chunks / Math.Max(_sw.Elapsed.TotalSeconds, 0.001);
        var spin = Spinner[_spin];
        var prefix = $"⛏  {spin}  {_files} files  {_skipped} skipped  {_chunks} chunks  {rate:0.#}/s  ";
        var width = Math.Max(40, Console.WindowWidth - 1);
        var pathBudget = width - prefix.Length;
        var path = pathBudget > 3 && _current.Length > pathBudget
            ? "…" + _current[^(pathBudget - 1)..]
            : _current;
        var line = prefix + path;
        Console.Error.Write($"\r{line.PadRight(width)[..width]}");
    }
}

internal sealed class GitignoreRule
{
    private readonly Regex? _regex;

    private GitignoreRule(string glob, bool negated, bool dirOnly, bool anchored)
    {
        Negated = negated;
        DirOnly = dirOnly;

        var pattern = glob.Replace(".", "\\.").Replace("**", "\x00").Replace("*", "[^/]*").Replace("\x00", ".*")
            .Replace("?", "[^/]");
        pattern = anchored
            ? "^" + pattern + "(/.*)?$"
            : "(^|.*/)(" + pattern + ")(/.*)?$";

        try
        {
            _regex = new Regex(pattern,
                RegexOptions.IgnoreCase |
                RegexOptions.Compiled);
        }
        catch (RegexParseException)
        {
        }
    }

    public bool Negated { get; }
    public bool DirOnly { get; }

    public static GitignoreRule? TryParse(string line)
    {
        line = line.TrimEnd();
        if (string.IsNullOrEmpty(line) || line.StartsWith('#')) return null;

        var negated = line.StartsWith('!');
        if (negated) line = line[1..];

        var dirOnly = line.EndsWith('/');
        if (dirOnly) line = line[..^1];

        var anchored = line.Contains('/');
        line = line.TrimStart('/');

        if (string.IsNullOrEmpty(line)) return null;
        return new GitignoreRule(line, negated, dirOnly, anchored);
    }

    public bool Matches(string relative)
    {
        try
        {
            return _regex?.IsMatch(relative) ?? false;
        }
        catch (RegexParseException)
        {
            return false;
        }
    }
}
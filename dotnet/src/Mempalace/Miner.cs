using Microsoft.Extensions.AI;
using System.Security.Cryptography;
using System.Text;
using Chroma.Embeddings;
using Mempalace.Storage;

namespace Mempalace;

// ---------------------------------------------------------------------------
// Data types
// ---------------------------------------------------------------------------

public sealed record Chunk(string Content, int ChunkIndex);

public sealed record MinerOptions(
    string? WingOverride = null,
    string Agent = "mempalace",
    int Limit = 0,
    bool DryRun = false,
    bool RespectGitignore = true,
    IReadOnlyList<string>? IncludeIgnored = null,
    VectorBackend Backend = VectorBackend.Chroma);

// ---------------------------------------------------------------------------
// Miner
// ---------------------------------------------------------------------------

public static class Miner
{
    // -------------------------------------------------------------------------
    // ChunkText — mirrors Python chunk_text exactly
    // -------------------------------------------------------------------------

    public static IReadOnlyList<Chunk> ChunkText(string content)
    {
        var chunks = new List<Chunk>();
        int start = 0;

        while (start < content.Length)
        {
            int end = Math.Min(start + Constants.ChunkSize, content.Length);

            if (end < content.Length)
            {
                // Try double-newline break first (paragraph boundary)
                int half = start + Constants.ChunkSize / 2;
                int breakAt = content.LastIndexOf("\n\n", end, end - half, StringComparison.Ordinal);
                if (breakAt == -1)
                    breakAt = content.LastIndexOf('\n', end, end - half);
                if (breakAt != -1)
                    end = breakAt + 1;
            }

            var chunk = content[start..end].Trim();
            if (chunk.Length >= Constants.MinChunkSize)
                chunks.Add(new Chunk(chunk, chunks.Count));

            start = end < content.Length ? end - Constants.ChunkOverlap : end;
        }

        return chunks;
    }

    // -------------------------------------------------------------------------
    // ID generation — matches Python drawer_id format
    // -------------------------------------------------------------------------

    public static string DrawerId(string wing, string room, string sourceFile, int chunkIndex)
    {
        var input = Encoding.UTF8.GetBytes(sourceFile + chunkIndex);
        var hash  = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant()[..24];
        return $"drawer_{wing}_{room}_{hash}";
    }

    // -------------------------------------------------------------------------
    // AddDrawer
    // -------------------------------------------------------------------------

    public static async Task AddDrawerAsync(
        IVectorCollection collection,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
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
            ["wing"]         = wing,
            ["room"]         = room,
            ["source_file"]  = sourceFile,
            ["chunk_index"]  = (long)chunkIndex,
            ["added_by"]     = agent,
            ["filed_at"]     = DateTime.UtcNow.ToString("O"),
            ["source_mtime"] = PalaceSession.GetUnixMtime(sourceFile),
        };

        await collection.UpsertAsync(
            ids: [id],
            documents: [content],
            embedder: embedder,
            metadatas: [metadata],
            ct: ct);
    }

    private const int BatchSize = 32;

    private record PendingChunk(string Wing, string Room, string Content,
        string SourceFile, int ChunkIndex, string Agent, double Mtime);

    private static async Task FlushBatchAsync(
        IVectorCollection collection,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        List<PendingChunk> batch,
        CancellationToken ct)
    {
        if (batch.Count == 0) return;
        var now = DateTime.UtcNow.ToString("O");
        var ids  = new string[batch.Count];
        var docs = new string[batch.Count];
        var metas = new Dictionary<string, object?>[batch.Count];
        for (int i = 0; i < batch.Count; i++)
        {
            var c = batch[i];
            ids[i]   = DrawerId(c.Wing, c.Room, c.SourceFile, c.ChunkIndex);
            docs[i]  = c.Content;
            metas[i] = new Dictionary<string, object?>
            {
                ["wing"]         = c.Wing,
                ["room"]         = c.Room,
                ["source_file"]  = c.SourceFile,
                ["chunk_index"]  = (long)c.ChunkIndex,
                ["added_by"]     = c.Agent,
                ["filed_at"]     = now,
                ["source_mtime"] = c.Mtime,
            };
        }
        await collection.UpsertAsync(ids, docs, embedder, metas, ct).ConfigureAwait(false);
        batch.Clear();
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

        var relative   = Path.GetRelativePath(projectPath, filePath).ToLowerInvariant();
        var filename   = Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant();
        var pathParts  = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // 1. Folder path matches room name or keyword
        foreach (var room in rooms)
        {
            var rName = room.Name.ToLowerInvariant();
            if (pathParts.Any(p => p == rName)) return room.Name;
            if (room.Keywords.Any(k => pathParts.Any(p => p.Contains(k.ToLowerInvariant()))))
                return room.Name;
        }

        // 2. Filename matches
        foreach (var room in rooms)
        {
            if (filename.Contains(room.Name.ToLowerInvariant())) return room.Name;
            if (room.Keywords.Any(k => filename.Contains(k.ToLowerInvariant()))) return room.Name;
        }

        // 3. Content keyword scoring
        var lower  = content.ToLowerInvariant();
        var scores = rooms
            .Select(r => (Room: r, Score: r.Keywords.Count(k => lower.Contains(k.ToLowerInvariant()))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();

        return scores.Room?.Name ?? "general";
    }

    // -------------------------------------------------------------------------
    // Mine — main entry point
    // -------------------------------------------------------------------------

    public static async Task MineAsync(
        string projectDir,
        string palacePath,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        MinerOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new MinerOptions();
        projectDir = Path.GetFullPath(projectDir);

        var projectConfig = ProjectConfig.TryLoad(projectDir);
        var wing  = options.WingOverride
            ?? projectConfig?.Wing
            ?? Path.GetFileName(projectDir);
        var rooms = projectConfig?.Rooms
            ?? Constants.DefaultHallKeywords
                .Select(kv => new RoomConfig(kv.Key, null, (IReadOnlyList<string>)kv.Value))
                .Append(RoomConfig.General)
                .ToList<RoomConfig>();

        using var session = PalaceSession.Open(palacePath, backend: options.Backend);

        // Bulk-load already-mined mtimes in one query instead of per-file queries
        var minedMtimes = options.DryRun
            ? new Dictionary<string, double>()
            : session.Collection.LoadMinedMtimes();

        var files    = EnumerateMineable(projectDir, options);
        int mined    = 0;
        int skipped  = 0;
        var batch    = new List<PendingChunk>(BatchSize);
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
                    progress.File(file, skipped: true);
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
                content   = await File.ReadAllTextAsync(file, ct);
            }
            catch { continue; }

            if (string.IsNullOrWhiteSpace(content)) continue;

            var room   = DetectRoom(file, content, rooms, projectDir);
            var chunks = ChunkText(content);

            progress.File(file, skipped: false);

            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();
                if (options.DryRun)
                {
                    Console.WriteLine($"[dry-run] {wing}/{room} chunk {chunk.ChunkIndex}: {file}");
                    continue;
                }

                progress.Chunk();
                batch.Add(new PendingChunk(wing, room, chunk.Content, file, chunk.ChunkIndex, options.Agent, fileMtime));
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
        GitignoreMatcher? gitignore = useGitignore ? GitignoreMatcher.TryLoad(dir) : null;

        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir); }
        catch { yield break; }

        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            bool isDir = Directory.Exists(entry);

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
        _rules   = rules;
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

        bool ignored = false;
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

internal sealed class MineProgress
{
    private static readonly string[] Spinner = ["⠋","⠙","⠚","⠞","⠖","⠦","⠴","⠲","⠳","⠵"];
    private static readonly bool IsTty = !Console.IsErrorRedirected;

    private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
    private readonly string _root;
    private int _spin;
    private int _files;
    private int _skipped;
    private int _chunks;
    private string _current = "";

    public MineProgress(string root) => _root = root;

    public void File(string path, bool skipped)
    {
        if (skipped) _skipped++;
        else         _current = System.IO.Path.GetRelativePath(_root, path);
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
        var rate   = _chunks / Math.Max(_sw.Elapsed.TotalSeconds, 0.001);
        var spin   = Spinner[_spin];
        var prefix = $"⛏  {spin}  {_files} files  {_skipped} skipped  {_chunks} chunks  {rate:0.#}/s  ";
        var width  = Math.Max(40, Console.WindowWidth - 1);
        var pathBudget = width - prefix.Length;
        var path   = pathBudget > 3 && _current.Length > pathBudget
            ? "…" + _current[^(pathBudget - 1)..]
            : _current;
        var line   = prefix + path;
        Console.Error.Write($"\r{line.PadRight(width)[..width]}");
    }
}

internal sealed class GitignoreRule
{
    public bool Negated { get; }
    public bool DirOnly { get; }
    private readonly System.Text.RegularExpressions.Regex? _regex;

    private GitignoreRule(string glob, bool negated, bool dirOnly, bool anchored)
    {
        Negated = negated;
        DirOnly = dirOnly;

        var pattern = glob.Replace(".", "\\.").Replace("**", "\x00").Replace("*", "[^/]*").Replace("\x00", ".*").Replace("?", "[^/]");
        pattern = anchored
            ? "^" + pattern + "(/.*)?$"
            : "(^|.*/)(" + pattern + ")(/.*)?$";

        try { _regex = new System.Text.RegularExpressions.Regex(pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.Compiled); }
        catch (System.Text.RegularExpressions.RegexParseException) { }
    }

    public static GitignoreRule? TryParse(string line)
    {
        line = line.TrimEnd();
        if (string.IsNullOrEmpty(line) || line.StartsWith('#')) return null;

        bool negated = line.StartsWith('!');
        if (negated) line = line[1..];

        bool dirOnly = line.EndsWith('/');
        if (dirOnly) line = line[..^1];

        bool anchored = line.Contains('/');
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
        catch (System.Text.RegularExpressions.RegexParseException)
        {
            return false;
        }
    }
}

using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace Mempalace;

// ---------------------------------------------------------------------------
// SplitMegaFiles — port of split_mega_files.py
//
// Splits a concatenated transcript file (multiple Claude Code sessions)
// into per-session files named with date, time, people, and subject.
//
// True session starts: "Claude Code v" header NOT followed by
//   "Ctrl+E" or "previous messages" within 6 lines.
// ---------------------------------------------------------------------------

public sealed record SplitResult(
    string OutputPath,
    int LineCount,
    string Timestamp,
    IReadOnlyList<string> People,
    string Subject);

public sealed record SplitStats(
    int TotalMegaFiles,
    int TotalSessionsWritten,
    bool DryRun);

public static class SplitMegaFiles
{
    private const long MaxFileSizeBytes = 500L * 1024 * 1024; // 500 MB

    private static readonly Regex TimestampPattern = new(
        @"⏺\s+(\d{1,2}:\d{2}\s+[AP]M)\s+\w+,\s+(\w+)\s+(\d{1,2}),\s+(\d{4})",
        RegexOptions.Compiled);

    private static readonly Regex SkipPromptPattern = new(
        @"^(\.\/|cd |ls |python|bash|git |cat |source |export |claude|./activate)",
        RegexOptions.Compiled);

    private static readonly Regex SafeNamePattern = new(
        @"[^\w\.\-]", RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<string, string> MonthMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["January"]="01",["February"]="02",["March"]="03",["April"]="04",
            ["May"]="05",["June"]="06",["July"]="07",["August"]="08",
            ["September"]="09",["October"]="10",["November"]="11",["December"]="12",
        };

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Split all mega-files found in sourceDir.
    /// Returns stats summary.
    /// </summary>
    public static SplitStats SplitDirectory(
        string sourceDir,
        string? outputDir  = null,
        int minSessions    = 2,
        bool dryRun        = false)
    {
        var dir   = Path.GetFullPath(sourceDir);
        var files = Directory.EnumerateFiles(dir, "*.txt").OrderBy(f => f).ToList();

        var megaFiles = new List<(string Path, int SessionCount)>();
        foreach (var f in files)
        {
            if (new FileInfo(f).Length > MaxFileSizeBytes) continue;
            var lines      = File.ReadAllLines(f);
            var boundaries = FindSessionBoundaries(lines);
            if (boundaries.Count >= minSessions)
                megaFiles.Add((f, boundaries.Count));
        }

        int totalWritten = 0;
        foreach (var (filePath, _) in megaFiles)
        {
            var written = SplitFile(filePath, outputDir, dryRun);
            totalWritten += written.Count;

            if (!dryRun && written.Count > 0)
            {
                var backup = Path.ChangeExtension(filePath, ".mega_backup");
                File.Move(filePath, backup, overwrite: true);
            }
        }

        return new SplitStats(megaFiles.Count, totalWritten, dryRun);
    }

    /// <summary>
    /// Split a single file into per-session files.
    /// Returns list of results (written or would-be-written).
    /// </summary>
    public static IReadOnlyList<SplitResult> SplitFile(
        string filePath,
        string? outputDir = null,
        bool dryRun       = false)
    {
        var info = new FileInfo(filePath);
        if (info.Length > MaxFileSizeBytes) return [];

        var lines      = File.ReadAllLines(filePath);
        var boundaryList = FindSessionBoundaries(lines).ToList();
        if (boundaryList.Count < 2) return [];

        boundaryList.Add(lines.Length); // sentinel
        var boundaries = boundaryList;

        var outDir  = outputDir is not null
            ? Path.GetFullPath(outputDir)
            : Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(outDir);

        var results = new List<SplitResult>();
        var srcStem = Regex.Replace(Path.GetFileNameWithoutExtension(filePath), @"[^\w-]", "_");
        if (srcStem.Length > 40) srcStem = srcStem[..40];

        for (int i = 0; i < boundaries.Count - 1; i++)
        {
            int start = boundaries[i];
            int end   = boundaries[i + 1];
            if (end - start < 10) continue;

            var chunk   = lines[start..end];
            var ts      = ExtractTimestamp(chunk);
            var people  = ExtractPeople(chunk);
            var subject = ExtractSubject(chunk);

            var tsPart     = ts ?? $"part{(i + 1):D2}";
            var peoplePart = people.Count > 0 ? string.Join("-", people.Take(3)) : "unknown";
            var rawName    = $"{srcStem}__{tsPart}_{peoplePart}_{subject}.txt";
            var safeName   = Regex.Replace(SafeNamePattern.Replace(rawName, "_"), "_+", "_");
            var outPath    = Path.Combine(outDir, safeName);

            if (!dryRun)
                File.WriteAllLines(outPath, chunk);

            results.Add(new SplitResult(outPath, chunk.Length, tsPart, people, subject));
        }

        return results;
    }

    // ── Session boundary detection ────────────────────────────────────────────

    internal static IReadOnlyList<int> FindSessionBoundaries(string[] lines)
    {
        var boundaries = new List<int>();
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Contains("Claude Code v") && IsTrueSessionStart(lines, i))
                boundaries.Add(i);
        return boundaries;
    }

    internal static bool IsTrueSessionStart(string[] lines, int idx)
    {
        var nearby = string.Concat(lines[idx..Math.Min(idx + 6, lines.Length)]);
        return !nearby.Contains("Ctrl+E") && !nearby.Contains("previous messages");
    }

    // ── Metadata extraction ───────────────────────────────────────────────────

    internal static string? ExtractTimestamp(string[] chunk)
    {
        foreach (var line in chunk.Take(50))
        {
            var m = TimestampPattern.Match(line);
            if (!m.Success) continue;

            var time  = m.Groups[1].Value;
            var month = m.Groups[2].Value;
            var day   = m.Groups[3].Value;
            var year  = m.Groups[4].Value;

            var mon      = MonthMap.TryGetValue(month, out var mv) ? mv : "00";
            var dayz     = day.PadLeft(2, '0');
            var timeSafe = time.Replace(":", "").Replace(" ", "");
            return $"{year}-{mon}-{dayz}_{timeSafe}";
        }
        return null;
    }

    internal static IReadOnlyList<string> ExtractPeople(string[] chunk)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var text  = string.Concat(chunk.Take(100));

        var knownPeople = LoadKnownPeople();
        foreach (var person in knownPeople)
            if (Regex.IsMatch(text, $@"\b{Regex.Escape(person)}\b", RegexOptions.IgnoreCase))
                found.Add(person);

        // Username hint from /Users/
        var dirMatch = Regex.Match(text, @"/Users/(\w+)/");
        if (dirMatch.Success)
        {
            var username = dirMatch.Groups[1].Value;
            var map = LoadUsernameMap();
            if (map.TryGetValue(username, out var name))
                found.Add(name);
        }

        return found.OrderBy(p => p).ToList();
    }

    internal static string ExtractSubject(string[] chunk)
    {
        foreach (var line in chunk)
        {
            if (!line.StartsWith("> ")) continue;
            var prompt = line[2..].Trim();
            if (prompt.Length <= 5 || SkipPromptPattern.IsMatch(prompt)) continue;

            var subject = Regex.Replace(prompt, @"[^\w\s-]", "");
            subject     = Regex.Replace(subject.Trim(), @"\s+", "-");
            return subject.Length > 60 ? subject[..60] : subject;
        }
        return "session";
    }

    // ── Known names config ────────────────────────────────────────────────────

    private static readonly string KnownNamesPath =
        Path.Combine(Constants.DefaultConfigDir, "known_names.json");

    private static readonly string[] FallbackKnownPeople =
        ["Alice", "Ben", "Riley", "Max", "Sam", "Devon", "Jordan"];

    private static IReadOnlyList<string> LoadKnownPeople()
    {
        if (!File.Exists(KnownNamesPath)) return FallbackKnownPeople;
        try
        {
            var raw = File.ReadAllText(KnownNamesPath);
            var doc = JsonNode.Parse(raw);
            if (doc is JsonArray arr)
                return arr.Select(n => n?.GetValue<string>() ?? "")
                          .Where(s => s.Length > 0).ToList();
            if (doc?["names"] is JsonArray names)
                return names.Select(n => n?.GetValue<string>() ?? "")
                            .Where(s => s.Length > 0).ToList();
        }
        catch { /* fall through */ }
        return FallbackKnownPeople;
    }

    private static IReadOnlyDictionary<string, string> LoadUsernameMap()
    {
        if (!File.Exists(KnownNamesPath)) return new Dictionary<string, string>();
        try
        {
            var raw = File.ReadAllText(KnownNamesPath);
            var doc = JsonNode.Parse(raw);
            var map = doc?["username_map"]?.AsObject();
            if (map is not null)
                return map.ToDictionary(kv => kv.Key, kv => kv.Value?.GetValue<string>() ?? "");
        }
        catch { /* fall through */ }
        return new Dictionary<string, string>();
    }
}

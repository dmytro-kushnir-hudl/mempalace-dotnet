using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mempalace;

// ---------------------------------------------------------------------------
// RoomDetector — port of room_detector_local.py
//
// Two detection strategies (no API, no internet):
//   1. Folder structure — walk top-level and one level deep, map to known rooms
//   2. Filename patterns — count keyword hits as fallback
//
// Writes mempalace.yaml on success.
// ---------------------------------------------------------------------------

public sealed record DetectedRoom(
    string Name,
    string Description,
    IReadOnlyList<string> Keywords);

public static class RoomDetector
{
    // ── Folder → room mapping (80+ patterns) ─────────────────────────────────

    private static readonly IReadOnlyDictionary<string, string> FolderRoomMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["frontend"]      = "frontend",  ["front-end"]    = "frontend",
            ["front_end"]     = "frontend",  ["client"]       = "frontend",
            ["ui"]            = "frontend",  ["views"]        = "frontend",
            ["components"]    = "frontend",  ["pages"]        = "frontend",
            ["backend"]       = "backend",   ["back-end"]     = "backend",
            ["back_end"]      = "backend",   ["server"]       = "backend",
            ["api"]           = "backend",   ["routes"]       = "backend",
            ["services"]      = "backend",   ["controllers"]  = "backend",
            ["models"]        = "backend",   ["database"]     = "backend",
            ["db"]            = "backend",
            ["docs"]          = "documentation", ["doc"]      = "documentation",
            ["documentation"] = "documentation", ["wiki"]     = "documentation",
            ["readme"]        = "documentation", ["notes"]    = "documentation",
            ["design"]        = "design",    ["designs"]      = "design",
            ["mockups"]       = "design",    ["wireframes"]   = "design",
            ["assets"]        = "design",    ["storyboard"]   = "design",
            ["costs"]         = "costs",     ["cost"]         = "costs",
            ["budget"]        = "costs",     ["finance"]      = "costs",
            ["financial"]     = "costs",     ["pricing"]      = "costs",
            ["invoices"]      = "costs",     ["accounting"]   = "costs",
            ["meetings"]      = "meetings",  ["meeting"]      = "meetings",
            ["calls"]         = "meetings",  ["meeting_notes"]= "meetings",
            ["standup"]       = "meetings",  ["minutes"]      = "meetings",
            ["team"]          = "team",      ["staff"]        = "team",
            ["hr"]            = "team",      ["hiring"]       = "team",
            ["employees"]     = "team",      ["people"]       = "team",
            ["research"]      = "research",  ["references"]   = "research",
            ["reading"]       = "research",  ["papers"]       = "research",
            ["planning"]      = "planning",  ["roadmap"]      = "planning",
            ["strategy"]      = "planning",  ["specs"]        = "planning",
            ["requirements"]  = "planning",
            ["tests"]         = "testing",   ["test"]         = "testing",
            ["testing"]       = "testing",   ["qa"]           = "testing",
            ["scripts"]       = "scripts",   ["tools"]        = "scripts",
            ["utils"]         = "scripts",
            ["config"]        = "configuration",  ["configs"]  = "configuration",
            ["settings"]      = "configuration",  ["infrastructure"] = "configuration",
            ["infra"]         = "configuration",  ["deploy"]   = "configuration",
        };

    // ── Main entry ────────────────────────────────────────────────────────────

    /// <summary>
    /// Detect rooms from folder structure, falling back to filename patterns.
    /// Writes mempalace.yaml to projectDir.
    /// Returns detected rooms.
    /// </summary>
    public static IReadOnlyList<DetectedRoom> DetectAndSave(
        string projectDir, string? wingOverride = null)
    {
        var dir         = Path.GetFullPath(projectDir);
        var projectName = wingOverride
            ?? Path.GetFileName(dir).ToLowerInvariant()
                    .Replace(' ', '_').Replace('-', '_');

        var rooms = DetectRoomsFromFolders(dir);
        if (rooms.Count <= 1)
            rooms = DetectRoomsFromFiles(dir);
        if (rooms.Count == 0)
            rooms = [new DetectedRoom("general", "All project files", [])];

        SaveConfig(dir, projectName, rooms);
        return rooms;
    }

    // ── Folder-based detection ────────────────────────────────────────────────

    public static IReadOnlyList<DetectedRoom> DetectRoomsFromFolders(string projectDir)
    {
        var foundRooms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> SafeDirs(string dir)
        {
            try { return Directory.EnumerateDirectories(dir); }
            catch { return []; }
        }

        // Top-level dirs
        foreach (var subdir in SafeDirs(projectDir))
        {
            var name = Path.GetFileName(subdir);
            if (Constants.SkipDirs.Contains(name)) continue;

            var key = name.ToLowerInvariant().Replace('-', '_');
            if (FolderRoomMap.TryGetValue(key, out var room))
            {
                if (!foundRooms.ContainsKey(room)) foundRooms[room] = name;
            }
            else if (name.Length > 2 && char.IsLetter(name[0]))
            {
                var clean = name.ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
                if (!foundRooms.ContainsKey(clean)) foundRooms[clean] = name;
            }
        }

        // One level deeper
        foreach (var subdir in SafeDirs(projectDir))
        {
            var name = Path.GetFileName(subdir);
            if (Constants.SkipDirs.Contains(name)) continue;

            foreach (var nested in SafeDirs(subdir))
            {
                var nname = Path.GetFileName(nested);
                if (Constants.SkipDirs.Contains(nname)) continue;

                var key = nname.ToLowerInvariant().Replace('-', '_');
                if (FolderRoomMap.TryGetValue(key, out var room)
                    && !foundRooms.ContainsKey(room))
                    foundRooms[room] = nname;
            }
        }

        var rooms = foundRooms
            .Select(kv => new DetectedRoom(
                kv.Key,
                $"Files from {kv.Value}/",
                [kv.Key, kv.Value.ToLowerInvariant()]))
            .ToList();

        if (!rooms.Any(r => r.Name == "general"))
            rooms.Add(new DetectedRoom("general", "Files that don't fit other rooms", []));

        return rooms;
    }

    // ── Filename-based fallback ───────────────────────────────────────────────

    public static IReadOnlyList<DetectedRoom> DetectRoomsFromFiles(string projectDir)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(projectDir, "*",
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible    = true,
                });
        }
        catch { return [new DetectedRoom("general", "All project files", [])]; }

        foreach (var file in files)
        {
            var dirName = Path.GetFileName(Path.GetDirectoryName(file) ?? "");
            if (Constants.SkipDirs.Contains(dirName)) continue;

            var nameLower = Path.GetFileName(file).ToLowerInvariant()
                                .Replace('-', '_').Replace(' ', '_');
            foreach (var (kw, room) in FolderRoomMap)
                if (nameLower.Contains(kw))
                    counts[room] = counts.GetValueOrDefault(room) + 1;
        }

        var rooms = counts
            .Where(kv => kv.Value >= 2)
            .OrderByDescending(kv => kv.Value)
            .Take(6)
            .Select(kv => new DetectedRoom(kv.Key, $"Files related to {kv.Key}", [kv.Key]))
            .ToList<DetectedRoom>();

        if (rooms.Count == 0)
            rooms.Add(new DetectedRoom("general", "All project files", []));

        return rooms;
    }

    // ── Save mempalace.yaml ───────────────────────────────────────────────────

    public static void SaveConfig(
        string projectDir, string wing, IReadOnlyList<DetectedRoom> rooms)
    {
        var data = new Dictionary<string, object>
        {
            ["wing"]  = wing,
            ["rooms"] = rooms.Select(r => new Dictionary<string, object>
            {
                ["name"]        = r.Name,
                ["description"] = r.Description,
                ["keywords"]    = r.Keywords.Count > 0
                    ? (object)r.Keywords
                    : new List<string> { r.Name },
            }).ToList(),
        };

        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .DisableAliases()
            .Build();

        var yaml    = serializer.Serialize(data);
        var cfgPath = Path.Combine(projectDir, "mempalace.yaml");
        File.WriteAllText(cfgPath, yaml);
    }
}

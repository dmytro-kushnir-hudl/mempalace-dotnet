using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mempalace;

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

public static class Constants
{
    public const int MaxNameLength = 128;
    public const int MaxContentLength = 100_000;
    public const int ChunkSize = 800;
    public const int ChunkOverlap = 100;
    public const int MinChunkSize = 50;
    public const int MaxFileSize = 10 * 1024 * 1024;  // 10 MB
    public const int LargeFileThreshold = 512 * 1024; // 512 KB — use streaming chunker above this
    public const string DefaultCollectionName = "mempalace_drawers";

    public static readonly IReadOnlySet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "__pycache__", ".venv", "venv", "env",
        "dist", "build", ".next", "coverage", ".mempalace", ".ruff_cache",
        ".mypy_cache", ".pytest_cache", ".cache", ".tox", ".nox", ".idea",
        ".vscode", ".ipynb_checkpoints", ".eggs", "htmlcov", "target",
        "bin", "obj"
    };

    public static readonly IReadOnlySet<string> ReadableExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".py", ".js", ".ts", ".jsx", ".tsx",
            ".yaml", ".yml", ".html", ".css", ".java", ".go", ".rs", ".rb",
            ".sh", ".sql", ".toml", ".cs", ".fs", ".kt", ".swift",
            ".cpp", ".c", ".h", ".hpp", ".csproj", ".fsproj", ".vbproj",
            ".sln", ".slnx", ".props", ".targets"
        };

    // Extensions skipped in file-mode mining (data / binary / subtitle formats)
    public static readonly IReadOnlySet<string> SkipExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".csv", ".tsv", ".srt", ".vtt",           // tabular / subtitle
            ".json", ".jsonl",                         // use --mode convos for these
            ".docx", ".doc", ".xls", ".xlsx", ".pptx", // office binary
            ".pdf", ".epub",                           // binary documents
            ".lock", ".sum",                           // lock files
        };

    public static readonly IReadOnlySet<string> SkipFilenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "mempalace.yaml", "mempalace.yml", "mempal.yaml", "mempal.yml",
        ".gitignore", "package-lock.json"
    };

    public static readonly IReadOnlyList<string> DefaultTopicWings = new[]
    {
        "emotions", "consciousness", "memory", "technical", "identity", "family", "creative"
    };

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> DefaultHallKeywords =
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["emotions"] = ["scared", "afraid", "worried", "happy", "sad", "love", "hate", "feel", "feeling"],
            ["consciousness"] = ["consciousness", "conscious", "aware", "real", "exist", "self", "mind"],
            ["memory"] = ["remember", "forgot", "memory", "recall", "past", "history"],
            ["technical"] = ["code", "bug", "function", "error", "api", "database", "deploy"],
            ["identity"] = ["identity", "who am i", "purpose", "values", "belief", "role"],
            ["family"] = ["family", "parent", "child", "sibling", "partner", "friend"],
            ["creative"] = ["create", "art", "music", "write", "design", "imagine", "story"]
        };

    public static string DefaultPalacePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".mempalace", "palace");

    public static string DefaultIdentityPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".mempalace", "identity.txt");

    public static string DefaultConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".mempalace");
}

// ---------------------------------------------------------------------------
// Room config (from mempalace.yaml)
// ---------------------------------------------------------------------------

public sealed record RoomConfig(string Name, string? Description, IReadOnlyList<string> Keywords)
{
    public static RoomConfig General { get; } = new("general", "Everything else", []);
}

// ---------------------------------------------------------------------------
// Project config (mempalace.yaml in project root)
// ---------------------------------------------------------------------------

public sealed class ProjectConfig
{
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "YamlDotNet reflection — YAML config is non-critical, fails gracefully")]
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "YamlDotNet reflection — YAML config is non-critical, fails gracefully")]
    private static readonly IDeserializer YamlDeserializer =
        new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

    public string Wing { get; init; } = "general";
    public IReadOnlyList<RoomConfig> Rooms { get; init; } = [RoomConfig.General];

    public static ProjectConfig? TryLoad(string projectDir)
    {
        foreach (var name in new[] { "mempalace.yaml", "mempalace.yml", "mempal.yaml", "mempal.yml" })
        {
            var path = Path.Combine(projectDir, name);
            if (!File.Exists(path)) continue;
            try
            {
                var raw = YamlDeserializer.Deserialize<RawProjectConfig>(File.ReadAllText(path));
                var rooms = (raw.Rooms ?? [])
                    .Select(r => new RoomConfig(
                        r.Name ?? "general",
                        r.Description,
                        r.Keywords ?? []))
                    .ToList();
                if (rooms.Count == 0) rooms.Add(RoomConfig.General);
                return new ProjectConfig { Wing = raw.Wing ?? Path.GetFileName(projectDir), Rooms = rooms };
            }
            catch
            {
                /* ignore malformed config */
            }
        }

        return null;
    }

    // Raw YAML shape
    private sealed class RawProjectConfig
    {
        public string? Wing { get; set; }
        public List<RawRoom>? Rooms { get; set; }
    }

    private sealed class RawRoom
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public List<string>? Keywords { get; set; }
    }
}

// ---------------------------------------------------------------------------
// Global mempalace config (~/.mempalace/config.json)
// ---------------------------------------------------------------------------

public sealed class MempalaceConfig
{
    private static readonly string ConfigFilePath =
        Path.Combine(Constants.DefaultConfigDir, "config.json");


    public string PalacePath { get; init; } = Constants.DefaultPalacePath;
    public string CollectionName { get; init; } = Constants.DefaultCollectionName;
    public string IdentityPath { get; init; } = Constants.DefaultIdentityPath;
    public IReadOnlyList<string> TopicWings { get; init; } = Constants.DefaultTopicWings;

    public static MempalaceConfig Load()
    {
        if (!File.Exists(ConfigFilePath))
            return new MempalaceConfig();
        try
        {
            var json = File.ReadAllText(ConfigFilePath);
            return JsonSerializer.Deserialize(json, MempalaceConfigContext.Default.MempalaceConfig)
                   ?? new MempalaceConfig();
        }
        catch
        {
            return new MempalaceConfig();
        }
    }
}

// ---------------------------------------------------------------------------
// Sanitization — matches Python sanitize_name / sanitize_content
// ---------------------------------------------------------------------------

public static partial class Sanitizer
{
    // ^[a-zA-Z0-9][a-zA-Z0-9_ .'-]{0,126}[a-zA-Z0-9]?$
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_ \.'\-]{0,126}[a-zA-Z0-9]?$")]
    private static partial Regex NameRegex();

    public static string SanitizeName(string value, string fieldName = "name")
    {
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException($"{fieldName} must not be empty.");
        if (value.Length > Constants.MaxNameLength)
            throw new ArgumentException($"{fieldName} exceeds {Constants.MaxNameLength} chars.");
        if (value.Contains('\0'))
            throw new ArgumentException($"{fieldName} contains null bytes.");
        if (value.Contains(".."))
            throw new ArgumentException($"{fieldName} contains path traversal sequence.");
        if (!NameRegex().IsMatch(value))
            throw new ArgumentException($"{fieldName} contains invalid characters.");
        return value;
    }

    public static string SanitizeContent(string value, int maxLength = Constants.MaxContentLength)
    {
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException("Content must not be empty.");
        if (value.Length > maxLength)
            throw new ArgumentException($"Content exceeds {maxLength} chars.");
        if (value.Contains('\0'))
            throw new ArgumentException("Content contains null bytes.");
        return value;
    }
}
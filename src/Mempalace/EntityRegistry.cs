using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mempalace;

// ---------------------------------------------------------------------------
// EntityRegistry — port of entity_registry.py
//
// Persistent personal entity registry. Knows Riley (person) vs ever (adverb).
// Three sources in priority order: onboarding > learned > wiki-researched.
//
// Stored at ~/.mempalace/entity_registry.json
// ---------------------------------------------------------------------------

public sealed record EntityLookupResult(
    string Type,          // "person" | "project" | "concept" | "unknown"
    double Confidence,
    string Source,        // "onboarding" | "learned" | "wiki" | "inferred" | "none"
    string Name,
    IReadOnlyList<string> Context,
    bool NeedsDisambiguation,
    string? DisambiguatedBy = null);

public sealed record WikiLookupResult(
    string InferredType,  // "person" | "place" | "concept" | "ambiguous" | "unknown"
    double Confidence,
    string? WikiSummary,
    string? WikiTitle,
    string? Note = null,
    bool Confirmed = false);

public sealed class EntityRegistry
{
    public static readonly string DefaultPath =
        Path.Combine(Constants.DefaultConfigDir, "entity_registry.json");

    // ── Common English words that double as names ─────────────────────────────

    private static readonly IReadOnlySet<string> CommonEnglishWords =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ever","grace","will","bill","mark","april","may","june","joy","hope",
            "faith","chance","chase","hunter","dash","flash","star","sky","river",
            "brook","lane","art","clay","gil","nat","max","rex","ray","jay","rose",
            "violet","lily","ivy","ash","reed","sage",
            "monday","tuesday","wednesday","thursday","friday","saturday","sunday",
            "january","february","march","july","august","september","october",
            "november","december",
        };

    // ── Context disambiguation patterns ───────────────────────────────────────

    private static readonly string[] PersonContextTemplates =
    [
        @"\b{0}\s+said\b", @"\b{0}\s+told\b", @"\b{0}\s+asked\b",
        @"\b{0}\s+laughed\b", @"\b{0}\s+smiled\b", @"\b{0}\s+was\b",
        @"\b{0}\s+is\b", @"\b{0}\s+called\b", @"\b{0}\s+texted\b",
        @"\bwith\s+{0}\b", @"\bsaw\s+{0}\b", @"\bcalled\s+{0}\b",
        @"\btook\s+{0}\b", @"\bpicked\s+up\s+{0}\b",
        @"\bdrop(?:ped)?\s+(?:off\s+)?{0}\b",
        @"\b{0}(?:'s|s')\b", @"\bhey\s+{0}\b", @"\bthanks?\s+{0}\b",
        @"^{0}[:\s]",
        @"\bmy\s+(?:son|daughter|kid|child|brother|sister|friend|partner|colleague|coworker)\s+{0}\b",
    ];

    private static readonly string[] ConceptContextTemplates =
    [
        @"\bhave\s+you\s+{0}\b", @"\bif\s+you\s+{0}\b",
        @"\b{0}\s+since\b", @"\b{0}\s+again\b", @"\bnot\s+{0}\b",
        @"\b{0}\s+more\b", @"\bwould\s+{0}\b", @"\bcould\s+{0}\b",
        @"\bwill\s+{0}\b",
        @"(?:the\s+)?{0}\s+(?:of|in|at|for|to)\b",
    ];

    // ── Wikipedia name/place indicators ──────────────────────────────────────

    private static readonly string[] NameIndicatorPhrases =
    [
        "given name","personal name","first name","forename",
        "masculine name","feminine name","boy's name","girl's name",
        "male name","female name","irish name","welsh name","scottish name",
        "gaelic name","hebrew name","arabic name","norse name","old english name",
        "is a name","as a name","name meaning","name derived from",
        "legendary irish","legendary welsh","legendary scottish",
    ];

    private static readonly string[] PlaceIndicatorPhrases =
    [
        "city in","town in","village in","municipality","capital of",
        "district of","county","province","region of","island of",
        "mountain in","river in",
    ];

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly Dictionary<string, object?> _data;
    private readonly string _path;

    private EntityRegistry(Dictionary<string, object?> data, string path)
    {
        _data = data;
        _path = path;
    }

    // ── Load / Save ───────────────────────────────────────────────────────────

    public static EntityRegistry Load(string? configDir = null)
    {
        var path = configDir is not null
            ? Path.Combine(configDir, "entity_registry.json")
            : DefaultPath;

        if (File.Exists(path))
        {
            try
            {
                var raw  = File.ReadAllText(path);
                var data = JsonSerializer.Deserialize<Dictionary<string, object?>>(raw,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (data is not null) return new EntityRegistry(data, path);
            }
            catch { /* fall through to empty */ }
        }

        return new EntityRegistry(Empty(), path);
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(_path,
            JsonSerializer.Serialize(_data,
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Dictionary<string, object?> Empty() => new()
    {
        ["version"]         = 1L,
        ["mode"]            = "personal",
        ["people"]          = new Dictionary<string, object?>(),
        ["projects"]        = new List<object?>(),
        ["ambiguous_flags"] = new List<object?>(),
        ["wiki_cache"]      = new Dictionary<string, object?>(),
    };

    // ── Properties ────────────────────────────────────────────────────────────

    public string Mode => _data.GetValueOrDefault("mode") as string ?? "personal";

    public IReadOnlyDictionary<string, object?> People =>
        (_data.GetValueOrDefault("people") as Dictionary<string, object?>
        ?? (_data.GetValueOrDefault("people") is JsonElement je
            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(je.GetRawText()) ?? []
            : []))!;

    public IReadOnlyList<string> Projects =>
        GetStringList("projects");

    public IReadOnlyList<string> AmbiguousFlags =>
        GetStringList("ambiguous_flags");

    // ── Seed from onboarding ─────────────────────────────────────────────────

    public void Seed(
        string mode,
        IEnumerable<(string Name, string Relationship, string Context)> people,
        IEnumerable<string> projects,
        IReadOnlyDictionary<string, string>? aliases = null)
    {
        _data["mode"]     = mode;
        _data["projects"] = projects.ToList<object?>();

        aliases ??= new Dictionary<string, string>();
        var reverseAliases = aliases.ToDictionary(kv => kv.Value, kv => kv.Key);

        var peopleDict = GetOrCreatePeopleDict();

        foreach (var (name, relationship, context) in people)
        {
            var n = name.Trim();
            if (string.IsNullOrEmpty(n)) continue;

            peopleDict[n] = new Dictionary<string, object?>
            {
                ["source"]       = "onboarding",
                ["contexts"]     = new List<object?> { context },
                ["aliases"]      = reverseAliases.TryGetValue(n, out var a)
                                   ? new List<object?> { a } : new List<object?>(),
                ["relationship"] = relationship,
                ["confidence"]   = 1.0,
            };

            if (reverseAliases.TryGetValue(n, out var alias))
            {
                peopleDict[alias] = new Dictionary<string, object?>
                {
                    ["source"]       = "onboarding",
                    ["contexts"]     = new List<object?> { context },
                    ["aliases"]      = new List<object?> { n },
                    ["relationship"] = relationship,
                    ["confidence"]   = 1.0,
                    ["canonical"]    = n,
                };
            }
        }

        _data["people"] = peopleDict;

        var ambiguous = peopleDict.Keys
            .Where(k => CommonEnglishWords.Contains(k))
            .Select(k => k.ToLowerInvariant())
            .Distinct()
            .ToList<object?>();
        _data["ambiguous_flags"] = ambiguous;

        Save();
    }

    // ── Lookup ────────────────────────────────────────────────────────────────

    public EntityLookupResult Lookup(string word, string context = "")
    {
        var peopleDict = GetOrCreatePeopleDict();

        // 1. Exact + alias match in people
        foreach (var (canonical, val) in peopleDict)
        {
            var info = AsDictionary(val);
            var aliases = GetStringListFromObj(info.GetValueOrDefault("aliases"));

            if (!string.Equals(word, canonical, StringComparison.OrdinalIgnoreCase)
                && !aliases.Any(a => string.Equals(word, a, StringComparison.OrdinalIgnoreCase)))
                continue;

            // Ambiguous word — check context
            if (AmbiguousFlags.Any(f => f.Equals(word, StringComparison.OrdinalIgnoreCase))
                && !string.IsNullOrEmpty(context))
            {
                var resolved = Disambiguate(word, context, info);
                if (resolved is not null) return resolved;
            }

            return new EntityLookupResult(
                "person",
                AsDouble(info.GetValueOrDefault("confidence"), 1.0),
                info.GetValueOrDefault("source") as string ?? "onboarding",
                canonical,
                GetStringListFromObj(info.GetValueOrDefault("contexts")),
                false);
        }

        // 2. Project match
        foreach (var proj in Projects)
            if (string.Equals(word, proj, StringComparison.OrdinalIgnoreCase))
                return new EntityLookupResult("project", 1.0, "onboarding", proj, [], false);

        // 3. Wiki cache (confirmed only)
        var cache = GetOrCreateWikiCache();
        foreach (var (cached, val) in cache)
        {
            if (!string.Equals(word, cached, StringComparison.OrdinalIgnoreCase)) continue;
            var entry = AsDictionary(val);
            if (entry.GetValueOrDefault("confirmed") is not true
                && entry.GetValueOrDefault("confirmed") is not JsonElement je2
                || (entry.GetValueOrDefault("confirmed") is JsonElement jeb
                    && !jeb.GetBoolean()))
            {
                // Check confirmed flag more robustly
                var confirmedRaw = entry.GetValueOrDefault("confirmed");
                bool confirmed = confirmedRaw is true
                    || (confirmedRaw is JsonElement jce && jce.GetBoolean());
                if (!confirmed) continue;
            }
            return new EntityLookupResult(
                entry.GetValueOrDefault("inferred_type") as string ?? "unknown",
                AsDouble(entry.GetValueOrDefault("confidence"), 0.0),
                "wiki", word, [], false);
        }

        return new EntityLookupResult("unknown", 0.0, "none", word, [], false);
    }

    // ── Wikipedia research ────────────────────────────────────────────────────

    public async Task<WikiLookupResult> ResearchAsync(
        string word, bool autoConfirm = false, CancellationToken ct = default)
    {
        var cache = GetOrCreateWikiCache();
        if (cache.TryGetValue(word, out var cached))
        {
            var c = AsDictionary(cached);
            return new WikiLookupResult(
                c.GetValueOrDefault("inferred_type") as string ?? "unknown",
                AsDouble(c.GetValueOrDefault("confidence"), 0.0),
                c.GetValueOrDefault("wiki_summary") as string,
                c.GetValueOrDefault("wiki_title") as string,
                c.GetValueOrDefault("note") as string,
                autoConfirm || (c.GetValueOrDefault("confirmed") is true));
        }

        var result = await WikipediaLookupAsync(word, ct);

        cache[word] = new Dictionary<string, object?>
        {
            ["inferred_type"] = result.InferredType,
            ["confidence"]    = result.Confidence,
            ["wiki_summary"]  = result.WikiSummary,
            ["wiki_title"]    = result.WikiTitle,
            ["note"]          = result.Note,
            ["word"]          = word,
            ["confirmed"]     = autoConfirm,
        };
        _data["wiki_cache"] = cache;
        Save();

        return result with { Confirmed = autoConfirm };
    }

    public void ConfirmResearch(
        string word, string entityType,
        string relationship = "", string context = "personal")
    {
        var cache = GetOrCreateWikiCache();
        if (cache.TryGetValue(word, out var val))
        {
            var entry = AsDictionaryMutable(val);
            entry["confirmed"]      = true;
            entry["confirmed_type"] = entityType;
            cache[word] = entry;
        }

        if (entityType == "person")
        {
            var pd2 = GetOrCreatePeopleDict();
            pd2[word] = new Dictionary<string, object?>
            {
                ["source"]       = "wiki",
                ["contexts"]     = new List<object?> { context },
                ["aliases"]      = new List<object?>(),
                ["relationship"] = relationship,
                ["confidence"]   = 0.90,
            };
            _data["people"] = pd2;

            if (CommonEnglishWords.Contains(word))
            {
                var flags = GetOrCreateStringList("ambiguous_flags");
                if (!flags.Contains(word.ToLowerInvariant()))
                    flags.Add(word.ToLowerInvariant());
                _data["ambiguous_flags"] = flags.Cast<object?>().ToList();
            }
        }

        Save();
    }

    // ── Learn from text ───────────────────────────────────────────────────────

    public IReadOnlyList<DetectedEntity> LearnFromText(
        string text, double minConfidence = 0.75)
    {
        var peopleDict = GetOrCreatePeopleDict();
        var projectsList = Projects;

        var detected = EntityDetector.DetectFromText(text);
        var newCandidates = new List<DetectedEntity>();

        foreach (var entity in detected.People)
        {
            if (entity.Confidence < minConfidence) continue;
            if (peopleDict.ContainsKey(entity.Name)) continue;
            if (projectsList.Any(p => p.Equals(entity.Name, StringComparison.OrdinalIgnoreCase)))
                continue;

            peopleDict[entity.Name] = new Dictionary<string, object?>
            {
                ["source"]       = "learned",
                ["contexts"]     = new List<object?> { Mode == "combo" ? "personal" : Mode },
                ["aliases"]      = new List<object?>(),
                ["relationship"] = "",
                ["confidence"]   = entity.Confidence,
                ["seen_count"]   = (long)entity.Frequency,
            };

            if (CommonEnglishWords.Contains(entity.Name))
            {
                var flags = GetOrCreateStringList("ambiguous_flags");
                var lower = entity.Name.ToLowerInvariant();
                if (!flags.Contains(lower)) flags.Add(lower);
                _data["ambiguous_flags"] = flags.Cast<object?>().ToList();
            }

            newCandidates.Add(entity);
        }

        if (newCandidates.Count > 0)
        {
            _data["people"] = peopleDict;
            Save();
        }

        return newCandidates;
    }

    // ── Query helpers ─────────────────────────────────────────────────────────

    public IReadOnlyList<string> ExtractPeopleFromQuery(string query)
    {
        var found      = new List<string>();
        var peopleDict = GetOrCreatePeopleDict();

        foreach (var (canonical, val) in peopleDict)
        {
            var info    = AsDictionary(val);
            var aliases = GetStringListFromObj(info.GetValueOrDefault("aliases"));

            foreach (var name in aliases.Prepend(canonical))
            {
                if (!Regex.IsMatch(query, $@"\b{Regex.Escape(name)}\b",
                    RegexOptions.IgnoreCase))
                    continue;

                if (AmbiguousFlags.Any(f => f.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    var result = Disambiguate(name, query, info);
                    if (result?.Type == "person" && !found.Contains(canonical))
                        found.Add(canonical);
                }
                else if (!found.Contains(canonical))
                {
                    found.Add(canonical);
                }
            }
        }

        return found;
    }

    public IReadOnlyList<string> ExtractUnknownCandidates(string query)
    {
        var capitals = Regex.Matches(query, @"\b[A-Z][a-z]{2,15}\b")
            .Select(m => m.Value)
            .Distinct()
            .ToList();

        return capitals
            .Where(w => !CommonEnglishWords.Contains(w)
                && Lookup(w).Type == "unknown")
            .ToList();
    }

    // ── Summary ───────────────────────────────────────────────────────────────

    public string Summary()
    {
        var peopleKeys = GetOrCreatePeopleDict().Keys.ToList();
        var preview    = string.Join(", ", peopleKeys.Take(8));
        if (peopleKeys.Count > 8) preview += "...";

        var projectsStr = Projects.Count > 0 ? string.Join(", ", Projects) : "(none)";
        var flagsStr    = AmbiguousFlags.Count > 0 ? string.Join(", ", AmbiguousFlags) : "(none)";

        return $"""
            Mode: {Mode}
            People: {peopleKeys.Count} ({preview})
            Projects: {projectsStr}
            Ambiguous flags: {flagsStr}
            Wiki cache: {GetOrCreateWikiCache().Count} entries
            """;
    }

    // ── Wikipedia lookup ──────────────────────────────────────────────────────

    private static readonly HttpClient _http = new();

    static EntityRegistry()
    {
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("MemPalace", "1.0"));
        _http.Timeout = TimeSpan.FromSeconds(5);
    }

    private static async Task<WikiLookupResult> WikipediaLookupAsync(
        string word, CancellationToken ct)
    {
        try
        {
            var url  = $"https://en.wikipedia.org/api/rest_v1/page/summary/{Uri.EscapeDataString(word)}";
            var resp = await _http.GetAsync(url, ct);

            if (!resp.IsSuccessStatusCode)
            {
                if ((int)resp.StatusCode == 404)
                    return new WikiLookupResult("person", 0.70, null, null,
                        "not found in Wikipedia — likely proper noun or unusual name");
                return new WikiLookupResult("unknown", 0.0, null, null);
            }

            var body    = await resp.Content.ReadAsStringAsync(ct);
            var doc     = JsonNode.Parse(body);
            var type    = doc?["type"]?.GetValue<string>() ?? "";
            var extract = (doc?["extract"]?.GetValue<string>() ?? "").ToLowerInvariant();
            var title   = doc?["title"]?.GetValue<string>() ?? word;

            if (type == "disambiguation")
            {
                var desc = (doc?["description"]?.GetValue<string>() ?? "").ToLowerInvariant();
                if (desc.Contains("name") || desc.Contains("given name"))
                    return new WikiLookupResult("person", 0.65,
                        extract[..Math.Min(200, extract.Length)], title,
                        "disambiguation page with name entries");
                return new WikiLookupResult("ambiguous", 0.4,
                    extract[..Math.Min(200, extract.Length)], title);
            }

            var summary = extract[..Math.Min(200, extract.Length)];
            var wl      = word.ToLowerInvariant();

            if (NameIndicatorPhrases.Any(p => extract.Contains(p)))
            {
                double conf = extract.Contains($"{wl} is a") || extract.Contains($"{wl} (name")
                    ? 0.90 : 0.80;
                return new WikiLookupResult("person", conf, summary, title);
            }

            if (PlaceIndicatorPhrases.Any(p => extract.Contains(p)))
                return new WikiLookupResult("place", 0.80, summary, title);

            return new WikiLookupResult("concept", 0.60, summary, title);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return new WikiLookupResult("unknown", 0.0, null, null);
        }
    }

    // ── Private disambiguation ────────────────────────────────────────────────

    private EntityLookupResult? Disambiguate(
        string word, string context, IReadOnlyDictionary<string, object?> personInfo)
    {
        var n = Regex.Escape(word.ToLowerInvariant());
        var ctx = context.ToLowerInvariant();

        int personScore = PersonContextTemplates
            .Count(t => Regex.IsMatch(ctx, string.Format(t, n),
                RegexOptions.IgnoreCase | RegexOptions.Multiline));

        int conceptScore = ConceptContextTemplates
            .Count(t => Regex.IsMatch(ctx, string.Format(t, n),
                RegexOptions.IgnoreCase));

        if (personScore > conceptScore)
            return new EntityLookupResult(
                "person",
                Math.Min(0.95, 0.7 + personScore * 0.1),
                personInfo.GetValueOrDefault("source") as string ?? "onboarding",
                word,
                GetStringListFromObj(personInfo.GetValueOrDefault("contexts")),
                false, "context_patterns");

        if (conceptScore > personScore)
            return new EntityLookupResult(
                "concept",
                Math.Min(0.90, 0.7 + conceptScore * 0.1),
                "context_disambiguated",
                word, [], false, "context_patterns");

        return null; // Truly ambiguous — fall through
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Dictionary<string, object?> GetOrCreatePeopleDict()
    {
        var raw = _data.GetValueOrDefault("people");
        if (raw is Dictionary<string, object?> d) return d;
        if (raw is JsonElement je)
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                je.GetRawText()) ?? [];
            _data["people"] = parsed;
            return parsed;
        }
        var empty = new Dictionary<string, object?>();
        _data["people"] = empty;
        return empty;
    }

    private Dictionary<string, object?> GetOrCreateWikiCache()
    {
        var raw = _data.GetValueOrDefault("wiki_cache");
        if (raw is Dictionary<string, object?> d) return d;
        if (raw is JsonElement je)
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                je.GetRawText()) ?? [];
            _data["wiki_cache"] = parsed;
            return parsed;
        }
        var empty = new Dictionary<string, object?>();
        _data["wiki_cache"] = empty;
        return empty;
    }

    private List<string> GetOrCreateStringList(string key)
    {
        var raw = _data.GetValueOrDefault(key);
        if (raw is List<string> l) return l;
        if (raw is List<object?> lo) return lo.OfType<string>().ToList();
        if (raw is JsonElement je)
            return je.EnumerateArray()
                .Select(e => e.GetString() ?? "")
                .Where(s => s.Length > 0)
                .ToList();
        return [];
    }

    private IReadOnlyList<string> GetStringList(string key) =>
        GetOrCreateStringList(key);

    private static IReadOnlyDictionary<string, object?> AsDictionary(object? val)
        => AsDictionaryMutable(val);

    private static Dictionary<string, object?> AsDictionaryMutable(object? val)
    {
        if (val is Dictionary<string, object?> d) return d;
        if (val is JsonElement je)
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(
                je.GetRawText()) ?? new Dictionary<string, object?>();
        return new Dictionary<string, object?>();
    }

    private static IReadOnlyList<string> GetStringListFromObj(object? val)
    {
        if (val is IEnumerable<string> ls) return ls.ToList();
        if (val is List<object?> lo) return lo.OfType<string>().ToList();
        if (val is JsonElement je && je.ValueKind == JsonValueKind.Array)
            return je.EnumerateArray()
                .Select(e => e.GetString() ?? "")
                .Where(s => s.Length > 0)
                .ToList();
        return [];
    }

    private static double AsDouble(object? val, double fallback)
    {
        if (val is double d) return d;
        if (val is JsonElement je) return je.GetDouble();
        if (val is long l) return l;
        return fallback;
    }
}

using System.Text.RegularExpressions;

namespace Mempalace;

// ---------------------------------------------------------------------------
// EntityDetector — port of entity_detector.py
//
// Two-pass approach:
//   Pass 1: scan text, extract capitalized-word candidates (freq >= 3)
//   Pass 2: score each candidate as person / project / uncertain
//
// No LLM required. Pure regex signal scoring.
// ---------------------------------------------------------------------------

public enum EntityType { Person, Project, Uncertain }

public sealed record DetectedEntity(
    string Name,
    EntityType Type,
    double Confidence,
    int Frequency,
    IReadOnlyList<string> Signals);

public sealed record DetectedEntities(
    IReadOnlyList<DetectedEntity> People,
    IReadOnlyList<DetectedEntity> Projects,
    IReadOnlyList<DetectedEntity> Uncertain);

public static class EntityDetector
{
    // ── Signal patterns ───────────────────────────────────────────────────────

    private static readonly string[] PersonVerbTemplates =
    [
        @"\b{0}\s+said\b", @"\b{0}\s+asked\b", @"\b{0}\s+told\b",
        @"\b{0}\s+replied\b", @"\b{0}\s+laughed\b", @"\b{0}\s+smiled\b",
        @"\b{0}\s+cried\b", @"\b{0}\s+felt\b", @"\b{0}\s+thinks?\b",
        @"\b{0}\s+wants?\b", @"\b{0}\s+loves?\b", @"\b{0}\s+hates?\b",
        @"\b{0}\s+knows?\b", @"\b{0}\s+decided\b", @"\b{0}\s+pushed\b",
        @"\b{0}\s+wrote\b", @"\bhey\s+{0}\b", @"\bthanks?\s+{0}\b",
        @"\bhi\s+{0}\b", @"\bdear\s+{0}\b",
    ];

    private static readonly string[] DialogueTemplates =
    [
        @"^>\s*{0}[:\s]", @"^{0}:\s", @"^\[{0}\]", @"""{0}\s+said",
    ];

    private static readonly string[] ProjectVerbTemplates =
    [
        @"\bbuilding\s+{0}\b", @"\bbuilt\s+{0}\b",
        @"\bship(?:ping|ped)?\s+{0}\b", @"\blaunch(?:ing|ed)?\s+{0}\b",
        @"\bdeploy(?:ing|ed)?\s+{0}\b", @"\binstall(?:ing|ed)?\s+{0}\b",
        @"\bthe\s+{0}\s+architecture\b", @"\bthe\s+{0}\s+pipeline\b",
        @"\bthe\s+{0}\s+system\b", @"\bthe\s+{0}\s+repo\b",
        @"\b{0}\s+v\d+\b", @"\b{0}\.py\b", @"\b{0}-core\b",
        @"\b{0}-local\b", @"\bimport\s+{0}\b", @"\bpip\s+install\s+{0}\b",
    ];

    private static readonly Regex[] PronounPatterns =
    [
        new(@"\bshe\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bher\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bhers\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bhe\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bhim\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bhis\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bthey\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bthem\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\btheir\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    private static readonly Regex CandidateWord =
        new(@"\b([A-Z][a-z]{1,19})\b", RegexOptions.Compiled);

    private static readonly Regex CandidateCamelCase =
        new(@"\b([A-Z][a-z]+[A-Z][a-zA-Z]+)\b", RegexOptions.Compiled);

    private static readonly Regex CandidatePhrase =
        new(@"\b([A-Z][a-z]+(?:\s+[A-Z][a-z]+)+)\b", RegexOptions.Compiled);

    // ── Stopwords ─────────────────────────────────────────────────────────────

    private static readonly HashSet<string> Stopwords = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "the","a","an","and","or","but","in","on","at","to","for","of","with","by",
        "from","as","is","was","are","were","be","been","being","have","has","had",
        "do","does","did","will","would","could","should","may","might","must","shall",
        "can","this","that","these","those","it","its","they","them","their","we","our",
        "you","your","i","my","me","he","she","his","her","who","what","when","where",
        "why","how","which","if","then","so","not","no","yes","ok","okay","just","very",
        "really","also","already","still","even","only","here","there","now","too","up",
        "out","about","like","use","get","got","make","made","take","put","come","go",
        "see","know","think","true","false","none","null","new","old","all","any","some",
        "return","print","def","class","import","from","step","usage","run","check",
        "find","add","set","list","args","dict","str","int","bool","path","file","type",
        "name","note","example","option","result","error","warning","info","every","each",
        "more","less","next","last","first","second","stack","layer","mode","test","stop",
        "start","copy","move","source","target","output","input","data","item","key",
        "value","returns","raises","yields","self","cls","kwargs","world","well","want",
        "topic","choose","social","cars","phones","healthcare","human","humans","people",
        "things","something","nothing","everything","anything","someone","everyone",
        "anyone","way","time","day","life","place","thing","part","kind","sort","case",
        "point","idea","fact","sense","question","answer","reason","number","version",
        "system","hey","hi","hello","thanks","thank","right","let","click","hit","press",
        "tap","drag","drop","open","close","save","load","launch","install","download",
        "upload","scroll","select","enter","submit","cancel","confirm","delete","paste",
        "type","write","read","search","show","hide","desktop","documents","downloads",
        "users","home","library","applications","preferences","settings","terminal",
        "actor","vector","remote","control","duration","fetch","agents","tools","others",
        "guards","ethics","regulation","learning","thinking","memory","language",
        "intelligence","technology","society","culture","future","history","science",
        "model","models","network","networks","training","inference",
    };

    // ── Prose-only extensions (fewer false positives) ─────────────────────────

    private static readonly HashSet<string> ProseExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".md", ".rst", ".csv" };

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Detect entity candidates from a list of file paths.
    /// Reads up to maxFiles files, first 5 KB each.
    /// </summary>
    public static DetectedEntities DetectFromFiles(
        IEnumerable<string> filePaths, int maxFiles = 10)
    {
        const int MaxBytesPerFile = 5_000;

        var allText  = new List<string>();
        var allLines = new List<string>();
        int read     = 0;

        foreach (var path in filePaths)
        {
            if (read >= maxFiles) break;
            try
            {
                using var fs = File.OpenRead(path);
                var buf = new byte[MaxBytesPerFile];
                int n   = fs.Read(buf, 0, MaxBytesPerFile);
                var content = System.Text.Encoding.UTF8.GetString(buf, 0, n);
                allText.Add(content);
                allLines.AddRange(content.Split('\n'));
                read++;
            }
            catch { /* skip unreadable */ }
        }

        return DetectFromText(string.Join('\n', allText), allLines);
    }

    /// <summary>Detect entities from plain text (already loaded).</summary>
    public static DetectedEntities DetectFromText(string text, IReadOnlyList<string>? lines = null)
    {
        lines ??= text.Split('\n');

        var candidates = ExtractCandidates(text);
        if (candidates.Count == 0)
            return new DetectedEntities([], [], []);

        var people    = new List<DetectedEntity>();
        var projects  = new List<DetectedEntity>();
        var uncertain = new List<DetectedEntity>();

        foreach (var (name, freq) in candidates.OrderByDescending(kv => kv.Value))
        {
            var scores = ScoreEntity(name, text, lines);
            var entity = ClassifyEntity(name, freq, scores);
            switch (entity.Type)
            {
                case EntityType.Person:    people.Add(entity);    break;
                case EntityType.Project:   projects.Add(entity);  break;
                default:                   uncertain.Add(entity);  break;
            }
        }

        return new DetectedEntities(
            people.OrderByDescending(e => e.Confidence).Take(15).ToList(),
            projects.OrderByDescending(e => e.Confidence).Take(10).ToList(),
            uncertain.OrderByDescending(e => e.Frequency).Take(8).ToList());
    }

    /// <summary>
    /// Collect prose-preferring file list from a directory for entity detection.
    /// Falls back to all readable files if fewer than 3 prose files found.
    /// </summary>
    public static IReadOnlyList<string> CollectFilesForDetection(
        string directory, int maxFiles = 10)
    {
        var prose = new List<string>();
        var all   = new List<string>();

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file);
            if (ProseExtensions.Contains(ext)) prose.Add(file);
            else if (Constants.ReadableExtensions.Contains(ext)) all.Add(file);
        }

        var combined = prose.Count >= 3 ? prose : [.. prose, .. all];
        return combined.Take(maxFiles).ToList();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static Dictionary<string, int> ExtractCandidates(string text)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (Match m in CandidateWord.Matches(text))
        {
            var w = m.Value;
            if (w.Length > 1 && !Stopwords.Contains(w.ToLowerInvariant()))
                counts[w] = counts.GetValueOrDefault(w) + 1;
        }

        foreach (Match m in CandidateCamelCase.Matches(text))
        {
            var w = m.Value;
            if (!Stopwords.Contains(w.ToLowerInvariant()))
                counts[w] = counts.GetValueOrDefault(w) + 1;
        }

        foreach (Match m in CandidatePhrase.Matches(text))
        {
            var phrase = m.Value;
            if (!phrase.Split(' ').Any(w => Stopwords.Contains(w.ToLowerInvariant())))
                counts[phrase] = counts.GetValueOrDefault(phrase) + 1;
        }

        return counts.Where(kv => kv.Value >= 3).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private static (int PersonScore, int ProjectScore,
        List<string> PersonSignals, List<string> ProjectSignals)
        ScoreEntity(string name, string text, IReadOnlyList<string> lines)
    {
        var n = Regex.Escape(name);
        int ps = 0, prs = 0;
        var psig  = new List<string>();
        var prsig = new List<string>();

        // Dialogue markers (strong — 3x each)
        foreach (var tpl in DialogueTemplates)
        {
            var rx = new Regex(string.Format(CultureInfo.InvariantCulture, tpl, n),
                RegexOptions.Multiline | RegexOptions.IgnoreCase);
            int m = rx.Count(text);
            if (m > 0) { ps += m * 3; psig.Add($"dialogue marker ({m}x)"); }
        }

        // Person verb patterns (2x each)
        foreach (var tpl in PersonVerbTemplates)
        {
            var rx = new Regex(string.Format(CultureInfo.InvariantCulture, tpl, n), RegexOptions.IgnoreCase);
            int m = rx.Count(text);
            if (m > 0) { ps += m * 2; psig.Add($"'{name} …' action ({m}x)"); }
        }

        // Pronoun proximity (2x each hit)
        var nameLower = name.ToLowerInvariant();
        var nameLines = lines
            .Select((l, i) => (l, i))
            .Where(t => t.l.Contains(nameLower, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.i)
            .ToList();

        int pronounHits = 0;
        foreach (var idx in nameLines)
        {
            var window = string.Join(' ', lines
                .Skip(Math.Max(0, idx - 2))
                .Take(5)).ToLowerInvariant();
            if (PronounPatterns.Any(rx => rx.IsMatch(window))) pronounHits++;
        }
        if (pronounHits > 0) { ps += pronounHits * 2; psig.Add($"pronoun nearby ({pronounHits}x)"); }

        // Direct address (4x each)
        var directRx = new Regex(
            $@"\bhey\s+{n}\b|\bthanks?\s+{n}\b|\bhi\s+{n}\b", RegexOptions.IgnoreCase);
        int direct = directRx.Count(text);
        if (direct > 0) { ps += direct * 4; psig.Add($"addressed directly ({direct}x)"); }

        // Project verb patterns (2x each)
        foreach (var tpl in ProjectVerbTemplates)
        {
            var rx = new Regex(string.Format(CultureInfo.InvariantCulture, tpl, n), RegexOptions.IgnoreCase);
            int m = rx.Count(text);
            if (m > 0) { prs += m * 2; prsig.Add($"project verb ({m}x)"); }
        }

        // Versioned/hyphenated (3x)
        var versionedRx = new Regex($@"\b{n}[-v]\w+", RegexOptions.IgnoreCase);
        int versioned = versionedRx.Count(text);
        if (versioned > 0) { prs += versioned * 3; prsig.Add($"versioned ({versioned}x)"); }

        // Code file reference (3x)
        var codeRefRx = new Regex(
            $@"\b{n}\.(py|js|ts|yaml|yml|json|sh)\b", RegexOptions.IgnoreCase);
        int codeRef = codeRefRx.Count(text);
        if (codeRef > 0) { prs += codeRef * 3; prsig.Add($"code file ref ({codeRef}x)"); }

        return (ps, prs, psig, prsig);
    }

    private static DetectedEntity ClassifyEntity(
        string name, int frequency,
        (int PersonScore, int ProjectScore, List<string> PersonSignals, List<string> ProjectSignals) s)
    {
        int ps  = s.PersonScore;
        int prs = s.ProjectScore;
        int total = ps + prs;

        if (total == 0)
        {
            return new DetectedEntity(name, EntityType.Uncertain,
                Math.Min(0.4, frequency / 50.0), frequency,
                [$"appears {frequency}x, no strong type signals"]);
        }

        double personRatio = (double)ps / total;

        // Require two distinct signal categories to confidently classify as person
        var sigCategories = new HashSet<string>();
        foreach (var sig in s.PersonSignals)
        {
            if (sig.Contains("dialogue")) sigCategories.Add("dialogue");
            else if (sig.Contains("action")) sigCategories.Add("action");
            else if (sig.Contains("pronoun")) sigCategories.Add("pronoun");
            else if (sig.Contains("addressed")) sigCategories.Add("addressed");
        }
        bool hasTwoTypes = sigCategories.Count >= 2;

        if (personRatio >= 0.7 && hasTwoTypes && ps >= 5)
            return new DetectedEntity(name, EntityType.Person,
                Math.Min(0.99, 0.5 + personRatio * 0.5), frequency,
                s.PersonSignals.Take(3).ToList());

        if (personRatio >= 0.7)
            return new DetectedEntity(name, EntityType.Uncertain, 0.4, frequency,
                [.. s.PersonSignals.Take(2), $"appears {frequency}x — pronoun-only"]);

        if (personRatio <= 0.3)
            return new DetectedEntity(name, EntityType.Project,
                Math.Min(0.99, 0.5 + (1 - personRatio) * 0.5), frequency,
                s.ProjectSignals.Take(3).ToList());

        return new DetectedEntity(name, EntityType.Uncertain, 0.5, frequency,
            [.. s.PersonSignals.Take(2), .. s.ProjectSignals.Take(1), "mixed signals"]);
    }
}

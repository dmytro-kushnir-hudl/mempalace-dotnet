using System.Text;
using System.Text.RegularExpressions;

namespace Mempalace;

// ---------------------------------------------------------------------------
// Dialect — port of dialect.py (AAAK Dialect encoder/decoder)
//
// AAAK is a lossy summarization format. Extracts entities, topics, key
// sentences, emotions, and flags from plain text into a compact symbolic
// representation. Any LLM reads it natively without decoding.
//
// FORMAT:
//   Header:  wing|room|date|source
//   Content: 0:ENTITIES|topic_keywords|"key quote"|emotions|FLAGS
//   Tunnel:  T:ZID<->ZID|label
//   Arc:     ARC:emotion->emotion->emotion
// ---------------------------------------------------------------------------

public sealed class Dialect
{
    // ── Emotion codes ─────────────────────────────────────────────────────────

    private static readonly Dictionary<string, string> EmotionCodes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["vulnerability"] = "vul", ["vulnerable"] = "vul",
            ["joy"] = "joy", ["joyful"] = "joy",
            ["fear"] = "fear", ["mild_fear"] = "fear",
            ["trust"] = "trust", ["trust_building"] = "trust",
            ["grief"] = "grief", ["raw_grief"] = "grief",
            ["wonder"] = "wonder", ["philosophical_wonder"] = "wonder",
            ["rage"] = "rage", ["anger"] = "rage",
            ["love"] = "love", ["devotion"] = "love",
            ["hope"] = "hope",
            ["despair"] = "despair", ["hopelessness"] = "despair",
            ["peace"] = "peace",
            ["relief"] = "relief",
            ["humor"] = "humor", ["dark_humor"] = "humor",
            ["tenderness"] = "tender",
            ["raw_honesty"] = "raw", ["brutal_honesty"] = "raw",
            ["self_doubt"] = "doubt",
            ["anxiety"] = "anx",
            ["exhaustion"] = "exhaust",
            ["conviction"] = "convict",
            ["quiet_passion"] = "passion",
            ["warmth"] = "warmth",
            ["curiosity"] = "curious",
            ["gratitude"] = "grat",
            ["frustration"] = "frust",
            ["confusion"] = "confuse",
            ["satisfaction"] = "satis",
            ["excitement"] = "excite",
            ["determination"] = "determ",
            ["surprise"] = "surprise",
        };

    private static readonly Dictionary<string, string> EmotionSignals =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["decided"] = "determ", ["prefer"] = "convict", ["worried"] = "anx",
            ["excited"] = "excite", ["frustrated"] = "frust", ["confused"] = "confuse",
            ["love"] = "love", ["hate"] = "rage", ["hope"] = "hope",
            ["fear"] = "fear", ["trust"] = "trust", ["happy"] = "joy",
            ["sad"] = "grief", ["surprised"] = "surprise", ["grateful"] = "grat",
            ["curious"] = "curious", ["wonder"] = "wonder", ["anxious"] = "anx",
            ["relieved"] = "relief", ["satisf"] = "satis",
            ["disappoint"] = "grief", ["concern"] = "anx",
        };

    private static readonly Dictionary<string, string> FlagSignals =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["decided"] = "DECISION", ["chose"] = "DECISION",
            ["switched"] = "DECISION", ["migrated"] = "DECISION",
            ["replaced"] = "DECISION", ["instead of"] = "DECISION",
            ["because"] = "DECISION",
            ["founded"] = "ORIGIN", ["created"] = "ORIGIN",
            ["started"] = "ORIGIN", ["born"] = "ORIGIN",
            ["launched"] = "ORIGIN", ["first time"] = "ORIGIN",
            ["core"] = "CORE", ["fundamental"] = "CORE",
            ["essential"] = "CORE", ["principle"] = "CORE",
            ["belief"] = "CORE", ["always"] = "CORE",
            ["never forget"] = "CORE",
            ["turning point"] = "PIVOT", ["changed everything"] = "PIVOT",
            ["realized"] = "PIVOT", ["breakthrough"] = "PIVOT",
            ["epiphany"] = "PIVOT",
            ["api"] = "TECHNICAL", ["database"] = "TECHNICAL",
            ["architecture"] = "TECHNICAL", ["deploy"] = "TECHNICAL",
            ["infrastructure"] = "TECHNICAL", ["algorithm"] = "TECHNICAL",
            ["framework"] = "TECHNICAL", ["server"] = "TECHNICAL",
            ["config"] = "TECHNICAL",
        };

    private static readonly HashSet<string> StopWords = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "the","a","an","is","are","was","were","be","been","being","have","has","had",
        "do","does","did","will","would","could","should","may","might","shall","can",
        "to","of","in","for","on","with","at","by","from","as","into","about",
        "between","through","during","before","after","above","below","up","down",
        "out","off","over","under","again","further","then","once","here","there",
        "when","where","why","how","all","each","every","both","few","more","most",
        "other","some","such","no","nor","not","only","own","same","so","than","too",
        "very","just","don","now","and","but","or","if","while","that","this","these",
        "those","it","its","i","we","you","he","she","they","me","him","her","us",
        "them","my","your","his","our","their","what","which","who","whom","also",
        "much","many","like","because","since","get","got","use","used","using",
        "make","made","thing","things","way","well","really","want","need",
    };

    // ── Compiled regex ────────────────────────────────────────────────────────

    private static readonly Regex WordRx =
        new(@"[a-zA-Z][a-zA-Z_-]{2,}", RegexOptions.Compiled);

    private static readonly Regex SentenceSplitRx =
        new(@"[.!?\n]+", RegexOptions.Compiled);

    private static readonly Regex CandidateNameRx =
        new(@"\b[A-Z][a-z]+\b", RegexOptions.Compiled);

    // ── Entity mappings ───────────────────────────────────────────────────────

    private readonly Dictionary<string, string> _entityCodes;
    private readonly IReadOnlyList<string> _skipNames;

    public Dialect(
        IReadOnlyDictionary<string, string>? entities = null,
        IReadOnlyList<string>? skipNames = null)
    {
        _entityCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (entities is not null)
            foreach (var (k, v) in entities)
                _entityCodes[k] = v;
        _skipNames = skipNames?.Select(n => n.ToLowerInvariant()).ToList()
            ?? (IReadOnlyList<string>)[];
    }

    // ── Entity encoding ───────────────────────────────────────────────────────

    public string? EncodeEntity(string name)
    {
        if (_skipNames.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase))) return null;
        if (_entityCodes.TryGetValue(name, out var code)) return code;
        // Prefix/substring match
        foreach (var (key, val) in _entityCodes)
            if (name.Contains(key, StringComparison.OrdinalIgnoreCase)) return val;
        // Auto-code: first 3 chars uppercase
        return name[..Math.Min(3, name.Length)].ToUpperInvariant();
    }

    public static string EncodeEmotions(IReadOnlyList<string> emotions)
    {
        var codes = new List<string>();
        foreach (var e in emotions)
        {
            var code = EmotionCodes.TryGetValue(e, out var c)
                ? c : e[..Math.Min(4, e.Length)];
            if (!codes.Contains(code)) codes.Add(code);
        }
        return string.Join('+', codes.Take(3));
    }

    // ── Plain text compression ────────────────────────────────────────────────

    /// <summary>
    /// Lossy summarization of plain text into AAAK format.
    /// Extracts entities, topics, key sentence, emotions, and flags.
    /// </summary>
    public string Compress(string text, IReadOnlyDictionary<string, string>? metadata = null)
    {
        metadata ??= new Dictionary<string, string>();

        var entities  = DetectEntitiesInText(text);
        var entityStr = entities.Count > 0 ? string.Join('+', entities.Take(3)) : "???";

        var topics   = ExtractTopics(text);
        var topicStr = topics.Count > 0 ? string.Join('_', topics.Take(3)) : "misc";

        var quote     = ExtractKeySentence(text);
        var quotePart = quote.Length > 0 ? $"\"{quote}\"" : "";

        var emotions  = DetectEmotions(text);
        var emotionStr = emotions.Count > 0 ? string.Join('+', emotions) : "";

        var flags    = DetectFlags(text);
        var flagStr  = flags.Count > 0 ? string.Join('+', flags) : "";

        var sb = new StringBuilder();

        // Header line
        var source = metadata.GetValueOrDefault("source_file", "");
        var wing   = metadata.GetValueOrDefault("wing", "");
        var room   = metadata.GetValueOrDefault("room", "");
        var date   = metadata.GetValueOrDefault("date", "");

        if (source.Length > 0 || wing.Length > 0)
        {
            var stem = source.Length > 0 ? Path.GetFileNameWithoutExtension(source) : "?";
            sb.AppendLine(CultureInfo.InvariantCulture, $"{(wing.Length > 0 ? wing : "?")}|{(room.Length > 0 ? room : "?")}|{(date.Length > 0 ? date : "?")}|{stem}");
        }

        // Content line
        var parts = new List<string> { $"0:{entityStr}", topicStr };
        if (quotePart.Length > 0) parts.Add(quotePart);
        if (emotionStr.Length > 0) parts.Add(emotionStr);
        if (flagStr.Length > 0) parts.Add(flagStr);
        sb.Append(string.Join('|', parts));

        return sb.ToString();
    }

    // ── Decoding ──────────────────────────────────────────────────────────────

    /// <summary>Parse an AAAK string back into a structured summary.</summary>
    public static AaakDecoded Decode(string dialectText)
    {
        var lines   = dialectText.Trim().Split('\n');
        var header  = new Dictionary<string, string>();
        var arc     = "";
        var zettels = new List<string>();
        var tunnels = new List<string>();

        foreach (var line in lines)
        {
            if (line.StartsWith("ARC:", StringComparison.Ordinal))
                arc = line[4..];
            else if (line.StartsWith("T:", StringComparison.Ordinal))
                tunnels.Add(line);
            else if (line.Contains('|') && line.Contains(':')
                     && line.Split('|')[0].Contains(':'))
                zettels.Add(line);
            else if (line.Contains('|'))
            {
                var parts = line.Split('|');
                header["file"]     = parts.Length > 0 ? parts[0] : "";
                header["entities"] = parts.Length > 1 ? parts[1] : "";
                header["date"]     = parts.Length > 2 ? parts[2] : "";
                header["title"]    = parts.Length > 3 ? parts[3] : "";
            }
        }

        return new AaakDecoded(header, arc, zettels, tunnels);
    }

    // ── Stats ─────────────────────────────────────────────────────────────────

    public static int CountTokens(string text)
    {
        var words = text.Split([' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return Math.Max(1, (int)(words.Length * 1.3));
    }

    public static CompressionStats GetCompressionStats(string original, string compressed) => new(
        CountTokens(original),
        CountTokens(compressed),
        Math.Round((double)CountTokens(original) / Math.Max(CountTokens(compressed), 1), 1),
        original.Length,
        compressed.Length);

    // ── Private helpers ───────────────────────────────────────────────────────

    private static List<string> DetectEmotions(string text)
    {
        var lower   = text.ToLowerInvariant();
        var found   = new List<string>();
        var seen    = new HashSet<string>();

        foreach (var (kw, code) in EmotionSignals)
            if (lower.Contains(kw) && seen.Add(code))
                found.Add(code);

        return found.Take(3).ToList();
    }

    private static List<string> DetectFlags(string text)
    {
        var lower = text.ToLowerInvariant();
        var found = new List<string>();
        var seen  = new HashSet<string>();

        foreach (var (kw, flag) in FlagSignals)
            if (lower.Contains(kw) && seen.Add(flag))
                found.Add(flag);

        return found.Take(3).ToList();
    }

    private static List<string> ExtractTopics(string text, int max = 3)
    {
        var freq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in WordRx.Matches(text))
        {
            var w = m.Value.ToLowerInvariant();
            if (StopWords.Contains(w) || w.Length < 3) continue;
            freq[w] = freq.GetValueOrDefault(w) + 1;
        }

        // Boost proper nouns and technical terms
        foreach (Match m in WordRx.Matches(text))
        {
            var orig  = m.Value;
            var lower = orig.ToLowerInvariant();
            if (StopWords.Contains(lower)) continue;
            if (char.IsUpper(orig[0]) && freq.ContainsKey(lower)) freq[lower] += 2;
            if ((orig.Contains('_') || orig.Contains('-')
                 || orig[1..].Any(char.IsUpper)) && freq.ContainsKey(lower))
                freq[lower] += 2;
        }

        return freq.OrderByDescending(kv => kv.Value)
            .Take(max).Select(kv => kv.Key).ToList();
    }

    private static string ExtractKeySentence(string text)
    {
        var sentences = SentenceSplitRx.Split(text)
            .Select(s => s.Trim())
            .Where(s => s.Length > 10)
            .ToList();

        if (sentences.Count == 0) return "";

        var decisionWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "decided","because","instead","prefer","switched","chose","realized",
            "important","key","critical","discovered","learned","conclusion",
            "solution","reason","why","breakthrough","insight",
        };

        var scored = sentences.Select(s =>
        {
            int score = 0;
            var sl = s.ToLowerInvariant();
            foreach (var w in decisionWords)
                if (sl.Contains(w)) score += 2;
            if (s.Length < 80) score++;
            if (s.Length < 40) score++;
            if (s.Length > 150) score -= 2;
            return (Score: score, Sentence: s);
        })
        .OrderByDescending(x => x.Score)
        .First().Sentence;

        return scored.Length > 55 ? scored[..52] + "..." : scored;
    }

    private List<string> DetectEntitiesInText(string text)
    {
        var found = new List<string>();

        // Check known entities first
        foreach (var (name, code) in _entityCodes)
            if (!name.Equals(name.ToLowerInvariant(), StringComparison.Ordinal)
                && text.Contains(name, StringComparison.OrdinalIgnoreCase)
                && !found.Contains(code))
                found.Add(code);

        if (found.Count > 0) return found;

        // Fallback: capitalized mid-sentence words
        var words = text.Split(' ');
        for (int i = 1; i < words.Length && found.Count < 3; i++)
        {
            var clean = Regex.Replace(words[i], @"[^a-zA-Z]", "");
            if (clean.Length >= 2 && char.IsUpper(clean[0])
                && clean[1..].All(char.IsLower)
                && !StopWords.Contains(clean.ToLowerInvariant()))
            {
                var code = clean[..Math.Min(3, clean.Length)].ToUpperInvariant();
                if (!found.Contains(code)) found.Add(code);
            }
        }

        return found;
    }
}

// ── Supporting records ────────────────────────────────────────────────────────

public sealed record AaakDecoded(
    IReadOnlyDictionary<string, string> Header,
    string Arc,
    IReadOnlyList<string> Zettels,
    IReadOnlyList<string> Tunnels);

public sealed record CompressionStats(
    int OriginalTokensEst,
    int SummaryTokensEst,
    double SizeRatio,
    int OriginalChars,
    int SummaryChars);

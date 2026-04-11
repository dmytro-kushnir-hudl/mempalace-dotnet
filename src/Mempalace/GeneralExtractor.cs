using System.Text.RegularExpressions;

namespace Mempalace;

// ---------------------------------------------------------------------------
// GeneralExtractor — port of general_extractor.py
//
// Extracts 5 memory types from plain text using pure keyword/regex heuristics.
// No LLM required. No external dependencies.
//
//   DECISION   — "we went with X because Y", choices made
//   PREFERENCE — "always use X", "never do Y", "I prefer Z"
//   MILESTONE  — breakthroughs, things that finally worked
//   PROBLEM    — what broke, what fixed it, root causes
//   EMOTIONAL  — feelings, vulnerability, relationships
// ---------------------------------------------------------------------------

public enum MemoryType { Decision, Preference, Milestone, Problem, Emotional }

public sealed record ExtractedMemory(string Content, MemoryType MemoryType, int ChunkIndex);

public static class GeneralExtractor
{
    // ── Marker sets ──────────────────────────────────────────────────────────

    private static readonly string[] DecisionMarkers =
    [
        @"\blet'?s (use|go with|try|pick|choose|switch to)\b",
        @"\bwe (should|decided|chose|went with|picked|settled on)\b",
        @"\bi'?m going (to|with)\b",
        @"\bbetter (to|than|approach|option|choice)\b",
        @"\binstead of\b", @"\brather than\b",
        @"\bthe reason (is|was|being)\b", @"\bbecause\b",
        @"\btrade-?off\b", @"\bpros and cons\b",
        @"\bover\b.*\bbecause\b", @"\barchitecture\b",
        @"\bapproach\b", @"\bstrategy\b", @"\bpattern\b",
        @"\bstack\b", @"\bframework\b", @"\binfrastructure\b",
        @"\bset (it |this )?to\b", @"\bconfigure\b", @"\bdefault\b",
    ];

    private static readonly string[] PreferenceMarkers =
    [
        @"\bi prefer\b", @"\balways use\b", @"\bnever use\b",
        @"\bdon'?t (ever |like to )?(use|do|mock|stub|import)\b",
        @"\bi like (to|when|how)\b", @"\bi hate (when|how|it when)\b",
        @"\bplease (always|never|don'?t)\b",
        @"\bmy (rule|preference|style|convention) is\b",
        @"\bwe (always|never)\b", @"\bfunctional\b.*\bstyle\b",
        @"\bimperative\b", @"\bsnake_?case\b", @"\bcamel_?case\b",
        @"\btabs\b.*\bspaces\b", @"\bspaces\b.*\btabs\b",
        @"\buse\b.*\binstead of\b",
    ];

    private static readonly string[] MilestoneMarkers =
    [
        @"\bit works\b", @"\bit worked\b", @"\bgot it working\b",
        @"\bfixed\b", @"\bsolved\b", @"\bbreakthrough\b",
        @"\bfigured (it )?out\b", @"\bnailed it\b", @"\bcracked (it|the)\b",
        @"\bfinally\b", @"\bfirst time\b", @"\bfirst ever\b",
        @"\bnever (done|been|had) before\b", @"\bdiscovered\b",
        @"\brealized\b", @"\bfound (out|that)\b", @"\bturns out\b",
        @"\bthe key (is|was|insight)\b", @"\bthe trick (is|was)\b",
        @"\bnow i (understand|see|get it)\b", @"\bbuilt\b",
        @"\bcreated\b", @"\bimplemented\b", @"\bshipped\b",
        @"\blaunched\b", @"\bdeployed\b", @"\breleased\b",
        @"\bprototype\b", @"\bproof of concept\b", @"\bdemo\b",
        @"\bversion \d", @"\bv\d+\.\d+",
        @"\d+x (compression|faster|slower|better|improvement|reduction)",
        @"\d+% (reduction|improvement|faster|better|smaller)",
    ];

    private static readonly string[] ProblemMarkers =
    [
        @"\b(bug|error|crash|fail|broke|broken|issue|problem)\b",
        @"\bdoesn'?t work\b", @"\bnot working\b", @"\bwon'?t\b.*\bwork\b",
        @"\bkeeps? (failing|crashing|breaking|erroring)\b",
        @"\broot cause\b", @"\bthe (problem|issue|bug) (is|was)\b",
        @"\bturns out\b.*\b(was|because|due to)\b",
        @"\bthe fix (is|was)\b", @"\bworkaround\b", @"\bthat'?s why\b",
        @"\bthe reason it\b", @"\bfixed (it |the |by )\b",
        @"\bsolution (is|was)\b", @"\bresolved\b", @"\bpatched\b",
        @"\bthe answer (is|was)\b", @"\b(had|need) to\b.*\binstead\b",
    ];

    private static readonly string[] EmotionMarkers =
    [
        @"\blove\b", @"\bscared\b", @"\bafraid\b", @"\bproud\b",
        @"\bhurt\b", @"\bhappy\b", @"\bsad\b", @"\bcry\b", @"\bcrying\b",
        @"\bmiss\b", @"\bsorry\b", @"\bgrateful\b", @"\bangry\b",
        @"\bworried\b", @"\blonely\b", @"\bbeautiful\b", @"\bamazing\b",
        @"\bwonderful\b", @"i feel", @"i'm scared", @"i love you",
        @"i'm sorry", @"i can't", @"i wish", @"i miss", @"i need",
        @"never told anyone", @"nobody knows", @"\*[^*]+\*",
    ];

    private static readonly IReadOnlyDictionary<MemoryType, string[]> AllMarkers =
        new Dictionary<MemoryType, string[]>
        {
            [MemoryType.Decision]   = DecisionMarkers,
            [MemoryType.Preference] = PreferenceMarkers,
            [MemoryType.Milestone]  = MilestoneMarkers,
            [MemoryType.Problem]    = ProblemMarkers,
            [MemoryType.Emotional]  = EmotionMarkers,
        };

    // Compiled marker regex cache (per type)
    private static readonly IReadOnlyDictionary<MemoryType, Regex[]> CompiledMarkers =
        AllMarkers.ToDictionary(
            kv => kv.Key,
            kv => kv.Value
                .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled))
                .ToArray());

    // ── Sentiment ─────────────────────────────────────────────────────────────

    private static readonly IReadOnlySet<string> PositiveWords = new HashSet<string>
    {
        "pride","proud","joy","happy","love","loving","beautiful","amazing","wonderful",
        "incredible","fantastic","brilliant","perfect","excited","thrilled","grateful",
        "warm","breakthrough","success","works","working","solved","fixed","nailed",
        "heart","hug","precious","adore",
    };

    private static readonly IReadOnlySet<string> NegativeWords = new HashSet<string>
    {
        "bug","error","crash","crashing","crashed","fail","failed","failing","failure",
        "broken","broke","breaking","breaks","issue","problem","wrong","stuck","blocked",
        "unable","impossible","missing","terrible","horrible","awful","worse","worst",
        "panic","disaster","mess",
    };

    // ── Code line patterns ────────────────────────────────────────────────────

    private static readonly Regex[] CodeLinePatterns =
    [
        new(@"^\s*[\$#]\s", RegexOptions.Compiled),
        new(@"^\s*(cd|source|echo|export|pip|npm|git|python|bash|curl|wget|mkdir|rm|cp|mv|ls|cat|grep|find|chmod|sudo|brew|docker)\s", RegexOptions.Compiled),
        new(@"^\s*```", RegexOptions.Compiled),
        new(@"^\s*(import|from|def|class|function|const|let|var|return)\s", RegexOptions.Compiled),
        new(@"^\s*[A-Z_]{2,}=", RegexOptions.Compiled),
        new(@"^\s*\|", RegexOptions.Compiled),
        new(@"^\s*[-]{2,}", RegexOptions.Compiled),
        new(@"^\s*[{}\[\]]\s*$", RegexOptions.Compiled),
        new(@"^\s*(if|for|while|try|except|elif|else:)\b", RegexOptions.Compiled),
        new(@"^\s*\w+\.\w+\(", RegexOptions.Compiled),
        new(@"^\s*\w+ = \w+\.\w+", RegexOptions.Compiled),
    ];

    // ── Resolution patterns ───────────────────────────────────────────────────

    private static readonly Regex[] ResolutionPatterns =
    [
        new(@"\bfixed\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bsolved\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bresolved\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bpatched\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bgot it working\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bit works\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bnailed it\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bfigured (it )?out\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bthe (fix|answer|solution)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    // ── Turn detection patterns ───────────────────────────────────────────────

    private static readonly Regex[] TurnPatterns =
    [
        new(@"^>\s", RegexOptions.Compiled),
        new(@"^(Human|User|Q)\s*:", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"^(Assistant|AI|A|Claude|ChatGPT)\s*:", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    ];

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Extract typed memories from any text. Pure heuristics, no LLM.
    /// Returns decisions, preferences, milestones, problems, and emotional moments.
    /// </summary>
    public static IReadOnlyList<ExtractedMemory> ExtractMemories(
        string text, double minConfidence = 0.3)
    {
        var segments = SplitIntoSegments(text);
        var memories = new List<ExtractedMemory>();

        foreach (var seg in segments)
        {
            if (seg.Trim().Length < 20) continue;

            var prose = ExtractProse(seg);
            var scores = new Dictionary<MemoryType, double>();

            foreach (var (type, regexes) in CompiledMarkers)
            {
                double score = ScoreMarkers(prose, regexes);
                if (score > 0) scores[type] = score;
            }

            if (scores.Count == 0) continue;

            // Length bonus
            int lengthBonus = seg.Length > 500 ? 2 : seg.Length > 200 ? 1 : 0;

            var bestType  = scores.MaxBy(kv => kv.Value).Key;
            double maxScore = scores[bestType] + lengthBonus;

            bestType  = Disambiguate(bestType, prose, scores);

            double confidence = Math.Min(1.0, maxScore / 5.0);
            if (confidence < minConfidence) continue;

            memories.Add(new ExtractedMemory(seg.Trim(), bestType, memories.Count));
        }

        return memories;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static double ScoreMarkers(string text, Regex[] regexes)
    {
        double score = 0;
        foreach (var rx in regexes)
            score += rx.Matches(text).Count;
        return score;
    }

    private static string GetSentiment(string text)
    {
        var words = new HashSet<string>(
            Regex.Matches(text.ToLowerInvariant(), @"\b\w+\b").Select(m => m.Value));
        int pos = words.Intersect(PositiveWords).Count();
        int neg = words.Intersect(NegativeWords).Count();
        return pos > neg ? "positive" : neg > pos ? "negative" : "neutral";
    }

    private static bool HasResolution(string text) =>
        ResolutionPatterns.Any(rx => rx.IsMatch(text));

    private static MemoryType Disambiguate(
        MemoryType type, string text, Dictionary<MemoryType, double> scores)
    {
        var sentiment = GetSentiment(text);

        if (type == MemoryType.Problem && HasResolution(text))
        {
            if (scores.GetValueOrDefault(MemoryType.Emotional) > 0 && sentiment == "positive")
                return MemoryType.Emotional;
            return MemoryType.Milestone;
        }

        if (type == MemoryType.Problem && sentiment == "positive")
        {
            if (scores.ContainsKey(MemoryType.Milestone)) return MemoryType.Milestone;
            if (scores.ContainsKey(MemoryType.Emotional)) return MemoryType.Emotional;
        }

        return type;
    }

    private static bool IsCodeLine(string line)
    {
        var s = line.Trim();
        if (string.IsNullOrEmpty(s)) return false;
        if (CodeLinePatterns.Any(rx => rx.IsMatch(s))) return true;
        var alphaCount = s.Count(char.IsLetter);
        return alphaCount / (double)Math.Max(s.Length, 1) < 0.4 && s.Length > 10;
    }

    private static string ExtractProse(string text)
    {
        var lines   = text.Split('\n');
        var prose   = new List<string>();
        bool inCode = false;

        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```")) { inCode = !inCode; continue; }
            if (inCode) continue;
            if (!IsCodeLine(line)) prose.Add(line);
        }

        var result = string.Join('\n', prose).Trim();
        return result.Length > 0 ? result : text;
    }

    private static IReadOnlyList<string> SplitIntoSegments(string text)
    {
        var lines = text.Split('\n');
        int turnCount = lines.Count(l => TurnPatterns.Any(rx => rx.IsMatch(l.Trim())));

        if (turnCount >= 3)
            return SplitByTurns(lines);

        var paras = text.Split(["\n\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        if (paras.Count <= 1 && lines.Length > 20)
        {
            var groups = new List<string>();
            for (int i = 0; i < lines.Length; i += 25)
            {
                var group = string.Join('\n', lines.Skip(i).Take(25)).Trim();
                if (group.Length > 0) groups.Add(group);
            }
            return groups;
        }

        return paras;
    }

    private static IReadOnlyList<string> SplitByTurns(string[] lines)
    {
        var segments = new List<string>();
        var current  = new List<string>();

        foreach (var line in lines)
        {
            bool isTurn = TurnPatterns.Any(rx => rx.IsMatch(line.Trim()));
            if (isTurn && current.Count > 0)
            {
                segments.Add(string.Join('\n', current));
                current.Clear();
            }
            current.Add(line);
        }

        if (current.Count > 0) segments.Add(string.Join('\n', current));
        return segments;
    }
}

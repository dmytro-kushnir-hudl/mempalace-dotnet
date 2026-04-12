using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Mempalace;

// ---------------------------------------------------------------------------
// Supported conversation formats
// ---------------------------------------------------------------------------

public enum ConvoFormat
{
    PlainText,
    ClaudeCodeJsonl,
    CodexJsonl,
    ClaudeAiJson,
    ChatGptJson,
    SlackJson,
    Unknown
}

public sealed record ConvoChunk(string Content, int ChunkIndex, string? MemoryType = null);

public sealed record ConvoMinerOptions(
    string? Wing = null,
    string Agent = "convo_miner",
    int Limit = 0,
    bool DryRun = false,
    string ExtractMode = "exchange",
    VectorBackend Backend = VectorBackend.Sqlite);

// ---------------------------------------------------------------------------
// ConvoMiner
// ---------------------------------------------------------------------------

public static class ConvoMiner
{
    private const int MinChunkSize = 30;

    public static readonly IReadOnlySet<string> ConvoExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".txt", ".md", ".json", ".jsonl" };

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> TopicKeywords =
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["technical"] =
            [
                "code", "python", "function", "bug", "error", "api", "database", "server", "deploy", "git", "test",
                "debug", "refactor"
            ],
            ["architecture"] =
            [
                "architecture", "design", "pattern", "structure", "schema", "interface", "module", "component",
                "service", "layer"
            ],
            ["planning"] =
            [
                "plan", "roadmap", "milestone", "deadline", "priority", "sprint", "backlog", "scope", "requirement",
                "spec"
            ],
            ["decisions"] =
            [
                "decided", "chose", "picked", "switched", "migrated", "replaced", "trade-off", "alternative", "option",
                "approach"
            ],
            ["problems"] =
                ["problem", "issue", "broken", "failed", "crash", "stuck", "workaround", "fix", "solved", "resolved"]
        };

    // -------------------------------------------------------------------------
    // Format detection
    // -------------------------------------------------------------------------

    public static ConvoFormat DetectFormat(string content, string filename)
    {
        if (filename.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            // Distinguish Claude Code JSONL from Codex CLI JSONL by session_meta marker
            return content.Contains("\"session_meta\"")
                ? ConvoFormat.CodexJsonl
                : ConvoFormat.ClaudeCodeJsonl;

        if (filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var trimmed = content.TrimStart();
            if (trimmed.StartsWith('['))
                // Slack exports are arrays of message objects with "type":"message"
                return content.Contains("\"type\":\"message\"") || content.Contains("\"type\": \"message\"")
                    ? ConvoFormat.SlackJson
                    : ConvoFormat.ClaudeAiJson;
            if (content.Contains("\"mapping\"")) return ConvoFormat.ChatGptJson;
        }

        return ConvoFormat.PlainText;
    }

    // -------------------------------------------------------------------------
    // Normalize to plain transcript ("> user\nai response\n\n---\n")
    // -------------------------------------------------------------------------

    public static string NormalizeToTranscript(string content, ConvoFormat format)
    {
        return format switch
        {
            ConvoFormat.ClaudeCodeJsonl => NormalizeJsonl(content),
            ConvoFormat.CodexJsonl => NormalizeCodexJsonl(content),
            ConvoFormat.ClaudeAiJson => NormalizeClaudeAi(content),
            ConvoFormat.ChatGptJson => NormalizeChatGpt(content),
            ConvoFormat.SlackJson => NormalizeSlack(content),
            _ => content
        };
    }

    private static string NormalizeCodexJsonl(string content)
    {
        // OpenAI Codex CLI sessions: event_msg entries with user_message/agent_message
        var sb = new StringBuilder();
        var hasSessionMeta = false;
        var messages = new List<(string Role, string Text)>();

        foreach (var line in content.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var obj = JsonNode.Parse(line);
                var entryType = obj?["type"]?.GetValue<string>() ?? "";
                if (entryType == "session_meta")
                {
                    hasSessionMeta = true;
                    continue;
                }

                if (entryType != "event_msg") continue;
                var payload = obj?["payload"];
                var payloadType = payload?["type"]?.GetValue<string>() ?? "";
                var msg = payload?["message"]?.GetValue<string>()?.Trim() ?? "";
                if (msg.Length == 0) continue;
                if (payloadType == "user_message") messages.Add(("user", msg));
                else if (payloadType == "agent_message") messages.Add(("assistant", msg));
            }
            catch
            {
                /* skip malformed */
            }
        }

        if (messages.Count < 2 || !hasSessionMeta) return content;

        foreach (var (role, text) in messages)
        {
            sb.AppendLine(role == "user" ? $"> {text}" : text);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string NormalizeSlack(string content)
    {
        // Slack channel export: [{"type":"message","user":"U123","text":"..."}]
        var sb = new StringBuilder();
        try
        {
            var arr = JsonNode.Parse(content)?.AsArray();
            if (arr is null) return content;
            var seenUsers = new Dictionary<string, string>();
            var lastRole = "";
            foreach (var item in arr)
            {
                if (item?["type"]?.GetValue<string>() != "message") continue;
                var userId = item["user"]?.GetValue<string>()
                             ?? item["username"]?.GetValue<string>() ?? "";
                var text = item["text"]?.GetValue<string>()?.Trim() ?? "";
                if (text.Length == 0 || userId.Length == 0) continue;
                if (!seenUsers.ContainsKey(userId))
                {
                    if (seenUsers.Count == 0) seenUsers[userId] = "user";
                    else seenUsers[userId] = lastRole == "user" ? "assistant" : "user";
                }

                lastRole = seenUsers[userId];
                sb.AppendLine(lastRole == "user" ? $"> {text}" : text);
                sb.AppendLine();
            }
        }
        catch
        {
            return content;
        }

        return sb.ToString();
    }

    private static string NormalizeJsonl(string content)
    {
        var sb = new StringBuilder();
        foreach (var line in content.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var obj = JsonNode.Parse(line);
                var role = obj?["role"]?.GetValue<string>() ?? "";
                var text = obj?["content"]?.GetValue<string>() ?? "";
                if (string.IsNullOrWhiteSpace(text)) continue;
                sb.AppendLine(role == "user" ? $"> {text}" : text);
                sb.AppendLine();
            }
            catch
            {
                /* skip malformed lines */
            }
        }

        return sb.ToString();
    }

    private static string NormalizeClaudeAi(string content)
    {
        var sb = new StringBuilder();
        try
        {
            var arr = JsonNode.Parse(content)?.AsArray();
            if (arr is null) return content;
            foreach (var conv in arr)
            {
                var turns = conv?["chat_messages"]?.AsArray();
                if (turns is null) continue;
                foreach (var turn in turns)
                {
                    var sender = turn?["sender"]?.GetValue<string>() ?? "";
                    var text = turn?["text"]?.GetValue<string>() ?? "";
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    sb.AppendLine(sender == "human" ? $"> {text}" : text);
                    sb.AppendLine();
                }

                sb.AppendLine("---");
            }
        }
        catch
        {
            return content;
        }

        return sb.ToString();
    }

    private static string NormalizeChatGpt(string content)
    {
        var sb = new StringBuilder();
        try
        {
            var root = JsonNode.Parse(content);
            var convs = root?["conversations"]?.AsArray()
                        ?? (root?.AsArray() is var a && a is not null ? a : null);
            if (convs is null) return content;
            foreach (var conv in convs)
            {
                // messages can be a dict (id → msg) or array
                var msgs = conv?["messages"];
                var msgList = msgs?.AsArray()
                              ?? msgs?.AsObject().Select(kv => kv.Value)
                              ?? [];
                foreach (var msg in msgList.OrderBy(_ => 0))
                {
                    var role = msg?["role"]?.GetValue<string>() ?? "";
                    var text = msg?["content"]?.GetValue<string>()
                               ?? msg?["content"]?[0]?["text"]?.GetValue<string>() ?? "";
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    sb.AppendLine(role == "user" ? $"> {text}" : text);
                    sb.AppendLine();
                }

                sb.AppendLine("---");
            }
        }
        catch
        {
            return content;
        }

        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // Chunking — mirrors Python chunk_exchanges
    // -------------------------------------------------------------------------

    public static IReadOnlyList<ConvoChunk> ChunkExchanges(string content)
    {
        var lines = content.Split('\n');
        var quoteLines = lines.Count(l => l.TrimStart().StartsWith('>'));
        return quoteLines >= 3
            ? ChunkByExchange(lines)
            : ChunkByParagraph(content);
    }

    private static List<ConvoChunk> ChunkByExchange(string[] lines)
    {
        var chunks = new List<ConvoChunk>();
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith('>'))
            {
                var userTurn = line.Trim();
                i++;
                var aiLines = new List<string>();
                while (i < lines.Length)
                {
                    var next = lines[i];
                    if (next.TrimStart().StartsWith('>') || next.Trim() == "---") break;
                    if (!string.IsNullOrWhiteSpace(next)) aiLines.Add(next.Trim());
                    i++;
                }

                var aiResponse = string.Join(" ", aiLines.Take(8));
                var text = aiResponse.Length > 0 ? $"{userTurn}\n{aiResponse}" : userTurn;
                if (text.Trim().Length > MinChunkSize)
                    chunks.Add(new ConvoChunk(text, chunks.Count));
            }
            else
            {
                i++;
            }
        }

        return chunks;
    }

    private static List<ConvoChunk> ChunkByParagraph(string content)
    {
        var chunks = new List<ConvoChunk>();
        var paras = content.Split("\n\n").Select(p => p.Trim()).Where(p => p.Length > MinChunkSize).ToList();

        if (paras.Count <= 1 && content.Count(c => c == '\n') > 20)
        {
            var lines = content.Split('\n');
            for (var i = 0; i < lines.Length; i += 25)
            {
                var group = string.Join("\n", lines.Skip(i).Take(25)).Trim();
                if (group.Length > MinChunkSize) chunks.Add(new ConvoChunk(group, chunks.Count));
            }

            return chunks;
        }

        foreach (var para in paras)
            chunks.Add(new ConvoChunk(para, chunks.Count));
        return chunks;
    }

    // -------------------------------------------------------------------------
    // Room detection from conversation content
    // -------------------------------------------------------------------------

    public static string DetectConvoRoom(string content)
    {
        var lower = content[..Math.Min(content.Length, 3000)].ToLowerInvariant();
        var scores = TopicKeywords
            .Select(kv => (Room: kv.Key, Score: kv.Value.Count(k => lower.Contains(k))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();
        return scores.Room ?? "general";
    }

    // -------------------------------------------------------------------------
    // MineConvosAsync — main entry point
    // -------------------------------------------------------------------------

    public static async Task MineConvosAsync(
        string sourceDir,
        string palacePath,
        IEmbeddingGenerator<ReadOnlyMemory<char>, Embedding<float>> embedder,
        ConvoMinerOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new ConvoMinerOptions();
        sourceDir = Path.GetFullPath(sourceDir);

        var wing = options.Wing
                   ?? Path.GetFileName(sourceDir).ToLowerInvariant().Replace(' ', '_').Replace('-', '_');

        var files = ScanConvos(sourceDir);
        if (options.Limit > 0) files = files.Take(options.Limit).ToList();

        Console.WriteLine($"ConvoMiner: {files.Count} files → wing={wing}");
        if (options.DryRun) Console.WriteLine("DRY RUN — nothing filed");

        using var session = PalaceSession.Open(palacePath, backend: options.Backend);
        int mined = 0, skipped = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            if (!options.DryRun && session.FileAlreadyMined(file))
            {
                skipped++;
                continue;
            }

            string content;
            try
            {
                content = await File.ReadAllTextAsync(file, ct);
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(content) || content.Length < MinChunkSize) continue;

            var format = DetectFormat(content, Path.GetFileName(file));
            var transcript = NormalizeToTranscript(content, format);
            var chunks = ChunkExchanges(transcript);
            if (chunks.Count == 0) continue;

            var room = DetectConvoRoom(transcript);

            if (options.DryRun)
            {
                Console.WriteLine($"  [dry-run] {Path.GetFileName(file)} → {room} ({chunks.Count} chunks)");
                continue;
            }

            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();
                var id = Miner.DrawerId(wing, room, file, chunk.ChunkIndex);
                await session.Collection.UpsertAsync(
                    [id],
                    [chunk.Content.AsMemory()],
                    embedder,
                    [
                        new Dictionary<string, object?>
                        {
                            ["wing"] = wing,
                            ["room"] = room,
                            ["source_file"] = file,
                            ["chunk_index"] = (long)chunk.ChunkIndex,
                            ["added_by"] = options.Agent,
                            ["filed_at"] = DateTime.UtcNow.ToString("O")
                        }
                    ],
                    ct);
            }

            mined++;
        }

        Console.WriteLine($"ConvoMiner: mined {mined} files ({skipped} unchanged)");
    }

    // -------------------------------------------------------------------------

    private static List<string> ScanConvos(string dir)
    {
        var files = new List<string>();
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
                if (Directory.Exists(entry))
                {
                    if (!Constants.SkipDirs.Contains(Path.GetFileName(entry)))
                        files.AddRange(ScanConvos(entry));
                }
                else
                {
                    var name = Path.GetFileName(entry);
                    if (name.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase)) continue;
                    if (ConvoExtensions.Contains(Path.GetExtension(entry)))
                        try
                        {
                            if (new FileInfo(entry).Length <= Constants.MaxFileSize) files.Add(entry);
                        }
                        catch
                        {
                            /* skip */
                        }
                }
        }
        catch
        {
            /* skip unreadable dirs */
        }

        return files;
    }
}
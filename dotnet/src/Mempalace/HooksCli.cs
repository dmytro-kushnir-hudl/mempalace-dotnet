using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mempalace;

// ---------------------------------------------------------------------------
// HooksCli — port of hooks_cli.py
//
// Handles Claude Code lifecycle hooks: session-start, stop, precompact.
// Reads JSON from stdin, writes JSON to stdout.
// State persisted in ~/.mempalace/hook_state/.
// ---------------------------------------------------------------------------

public static class HooksCli
{
    private const int SaveInterval = 15;

    private const string StopBlockReason =
        "AUTO-SAVE checkpoint. Save key topics, decisions, quotes, and code " +
        "from this session to your memory system. Organize into appropriate " +
        "categories. Use verbatim quotes where possible. Continue conversation " +
        "after saving.";

    private const string PrecompactBlockReason =
        "COMPACTION IMMINENT. Save ALL topics, decisions, quotes, code, and " +
        "important context from this session to your memory system. Be thorough " +
        "\u2014 after compaction, detailed context will be lost. Organize into " +
        "appropriate categories. Use verbatim quotes where possible. Save " +
        "everything, then allow compaction to proceed.";

    private static readonly string StateDir =
        Path.Combine(Constants.DefaultConfigDir, "hook_state");

    private static readonly HashSet<string> SupportedHarnesses =
        new(StringComparer.OrdinalIgnoreCase)
            { "claude-code", "codex" };

    // ── Main entry ────────────────────────────────────────────────────────────

    /// <summary>
    ///     Read JSON from stdin, dispatch to the appropriate hook handler,
    ///     write JSON result to stdout.
    /// </summary>
    public static async Task RunHookAsync(
        string hookName, string harness,
        TextReader? stdinOverride = null,
        TextWriter? stdoutOverride = null,
        CancellationToken ct = default)
    {
        var stdin = stdinOverride ?? Console.In;
        var stdout = stdoutOverride ?? Console.Out;

        if (!SupportedHarnesses.Contains(harness))
        {
            Console.Error.WriteLine($"Unknown harness: {harness}");
            return;
        }

        JsonObject data;
        try
        {
            var raw = await stdin.ReadToEndAsync(ct);
            data = JsonNode.Parse(raw)?.AsObject() ?? new JsonObject();
        }
        catch
        {
            Log("WARNING: Failed to parse stdin JSON, proceeding with empty data");
            data = new JsonObject();
        }

        var parsed = ParseHarnessInput(data, harness);

        switch (hookName.ToLowerInvariant())
        {
            case "session-start":
                Output(stdout, HookSessionStart(parsed));
                break;
            case "stop":
                Output(stdout, HookStop(parsed));
                break;
            case "precompact":
                Output(stdout, HookPrecompact(parsed));
                break;
            default:
                Console.Error.WriteLine($"Unknown hook: {hookName}");
                break;
        }
    }

    // ── Hook handlers ─────────────────────────────────────────────────────────

    private static JsonObject HookSessionStart((string SessionId, bool StopHookActive, string TranscriptPath) parsed)
    {
        Log($"SESSION START for session {parsed.SessionId}");
        Directory.CreateDirectory(StateDir);
        return new JsonObject(); // pass through
    }

    private static JsonObject HookStop((string SessionId, bool StopHookActive, string TranscriptPath) parsed)
    {
        // Infinite-loop prevention
        if (parsed.StopHookActive)
        {
            Output(Console.Out, new JsonObject());
            return new JsonObject();
        }

        var exchangeCount = CountHumanMessages(parsed.TranscriptPath);

        Directory.CreateDirectory(StateDir);
        var lastSaveFile = Path.Combine(StateDir, $"{parsed.SessionId}_last_save");

        var lastSave = 0;
        if (File.Exists(lastSaveFile))
            try
            {
                lastSave = int.Parse(File.ReadAllText(lastSaveFile).Trim(), CultureInfo.InvariantCulture);
            }
            catch
            {
                lastSave = 0;
            }

        var sinceLast = exchangeCount - lastSave;
        Log($"Session {parsed.SessionId}: {exchangeCount} exchanges, {sinceLast} since last save");

        if (sinceLast >= SaveInterval && exchangeCount > 0)
        {
            try
            {
                File.WriteAllText(lastSaveFile, exchangeCount.ToString(CultureInfo.InvariantCulture));
            }
            catch
            {
                /* best effort */
            }

            Log($"TRIGGERING SAVE at exchange {exchangeCount}");

            return new JsonObject
            {
                ["decision"] = "block",
                ["reason"] = StopBlockReason
            };
        }

        return new JsonObject();
    }

    private static JsonObject HookPrecompact((string SessionId, bool StopHookActive, string TranscriptPath) parsed)
    {
        Log($"PRE-COMPACT triggered for session {parsed.SessionId}");

        return new JsonObject
        {
            ["decision"] = "block",
            ["reason"] = PrecompactBlockReason
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (string SessionId, bool StopHookActive, string TranscriptPath)
        ParseHarnessInput(JsonObject data, string harness)
    {
        var sessionId = SanitizeSessionId(
            data["session_id"]?.GetValue<string>() ?? "unknown");
        var stopHookActive = data["stop_hook_active"]?.GetValue<bool>() ?? false;
        var transcriptPath = data["transcript_path"]?.GetValue<string>() ?? "";
        return (sessionId, stopHookActive, transcriptPath);
    }

    internal static int CountHumanMessages(string transcriptPath)
    {
        if (string.IsNullOrEmpty(transcriptPath) || !File.Exists(transcriptPath))
            return 0;

        var count = 0;
        try
        {
            foreach (var line in File.ReadLines(transcriptPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var obj = JsonNode.Parse(line);
                    var msg = obj?["message"];
                    if (msg?["role"]?.GetValue<string>() != "user") continue;

                    var content = msg["content"];
                    if (content is null) continue;

                    var text = content.GetValueKind() == JsonValueKind.String
                        ? content.GetValue<string>()
                        : string.Join(' ', content.AsArray()
                            .Select(b => b?["text"]?.GetValue<string>() ?? ""));

                    if (!text.Contains("<command-message>")) count++;
                }
                catch
                {
                    /* skip malformed */
                }
            }
        }
        catch
        {
            return 0;
        }

        return count;
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(StateDir);
            var logPath = Path.Combine(StateDir, "hook.log");
            var ts = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            File.AppendAllText(logPath, $"[{ts}] {message}{Environment.NewLine}");
        }
        catch
        {
            /* best effort */
        }
    }

    private static void Output(TextWriter writer, JsonObject data)
    {
        writer.WriteLine(data.ToJsonString(Json.Indented));
    }

    private static string SanitizeSessionId(string id)
    {
        var s = Regex.Replace(id, @"[^a-zA-Z0-9_-]", "");
        return s.Length > 0 ? s : "unknown";
    }
}
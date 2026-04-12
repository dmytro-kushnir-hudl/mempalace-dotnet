using System.CommandLine;
using Mempalace.Embeddings;
using Mempalace;

// ---------------------------------------------------------------------------
// Lazy embedder — created on first use, shared across commands
// ---------------------------------------------------------------------------

var _useInt8 = false;
DefaultEmbeddingProvider? _embedder = null;

async Task<DefaultEmbeddingProvider> GetEmbedder()
{
    if (_embedder is null)
    {
        Console.Error.WriteLine("Loading embedding model...");
        _embedder = await DefaultEmbeddingProvider.CreateAsync(useInt8: _useInt8);
    }

    return _embedder;
}

var config = MempalaceConfig.Load();

// ---------------------------------------------------------------------------
// Root command
// ---------------------------------------------------------------------------

var rootCmd = new RootCommand("mempalace — AI memory manager");

var palaceOpt = new Option<string>("--palace") { Description = "Path to palace directory", Recursive = true };
palaceOpt.DefaultValueFactory = _ => config.PalacePath;

var backendOpt = new Option<VectorBackend>("--backend")
    { Description = "Vector store backend: Chroma | Sqlite", Recursive = true };
backendOpt.DefaultValueFactory = _ => VectorBackend.Sqlite;

var int8Opt = new Option<bool>("--int8")
    { Description = "Use INT8 quantized model (~23 MB, ~3-4x faster inference)", Recursive = true };

rootCmd.Add(palaceOpt);
rootCmd.Add(backendOpt);
rootCmd.Add(int8Opt);

// ---------------------------------------------------------------------------
// mine
// ---------------------------------------------------------------------------

var mineCmd = new Command("mine", "Mine files or conversations into the palace");
var mineDirArg = new Argument<DirectoryInfo>("dir") { Description = "Directory to mine" };
var mineModeOpt = new Option<string>("--mode") { Description = "Mining mode: files | convos" };
var mineWingOpt = new Option<string?>("--wing") { Description = "Wing override" };
var mineAgentOpt = new Option<string>("--agent") { Description = "Agent name" };
var mineLimitOpt = new Option<int>("--limit") { Description = "Max files (0 = all)" };
var mineDryOpt = new Option<bool>("--dry-run") { Description = "Preview without writing" };
var mineParallelOpt = new Option<bool>("--parallel")
    { Description = "Pipeline I/O and embedding for higher throughput" };

mineModeOpt.DefaultValueFactory = _ => "files";
mineWingOpt.DefaultValueFactory = _ => null;
mineAgentOpt.DefaultValueFactory = _ => "mempalace";
mineLimitOpt.DefaultValueFactory = _ => 0;

mineCmd.Add(mineDirArg);
mineCmd.Add(mineModeOpt);
mineCmd.Add(mineWingOpt);
mineCmd.Add(mineAgentOpt);
mineCmd.Add(mineLimitOpt);
mineCmd.Add(mineDryOpt);
mineCmd.Add(mineParallelOpt);

mineCmd.SetAction(async (result, ct) =>
{
    var dir = result.GetValue(mineDirArg)!;
    var mode = result.GetValue(mineModeOpt) ?? "files";
    var palace = result.GetValue(palaceOpt)!;
    var wing = result.GetValue(mineWingOpt);
    var agent = result.GetValue(mineAgentOpt) ?? "mempalace";
    var limit = result.GetValue(mineLimitOpt);
    var dryRun = result.GetValue(mineDryOpt);
    var parallel = result.GetValue(mineParallelOpt);
    var backend = result.GetValue(backendOpt);
    var embedder = await GetEmbedder();
    if (mode == "convos")
        await ConvoMiner.MineConvosAsync(dir.FullName, palace, embedder,
            new ConvoMinerOptions(wing, agent, limit, dryRun, Backend: backend), ct);
    else
        await Miner.MineAsync(dir.FullName, palace, embedder,
            new MinerOptions(wing, agent, limit, dryRun, Backend: backend, Parallel: parallel), ct);
});

rootCmd.Add(mineCmd);

// ---------------------------------------------------------------------------
// search
// ---------------------------------------------------------------------------

var searchCmd = new Command("search", "Semantic search over the palace");
var searchQuery = new Argument<string>("query") { Description = "Search query" };
var searchWingOpt = new Option<string?>("--wing") { Description = "Filter by wing" };
var searchRoomOpt = new Option<string?>("--room") { Description = "Filter by room" };
var searchNOpt = new Option<int>("--n") { Description = "Number of results" };

searchWingOpt.DefaultValueFactory = _ => null;
searchRoomOpt.DefaultValueFactory = _ => null;
searchNOpt.DefaultValueFactory = _ => 5;

searchCmd.Add(searchQuery);
searchCmd.Add(searchWingOpt);
searchCmd.Add(searchRoomOpt);
searchCmd.Add(searchNOpt);

searchCmd.SetAction(async (result, ct) =>
{
    var query = result.GetValue(searchQuery)!;
    var palace = result.GetValue(palaceOpt)!;
    var wing = result.GetValue(searchWingOpt);
    var room = result.GetValue(searchRoomOpt);
    var n = result.GetValue(searchNOpt);
    var backend = result.GetValue(backendOpt);
    var embedder = await GetEmbedder();
    var response = await Searcher.SearchMemoriesAsync(query, palace, embedder, wing, room, n, backend: backend, ct: ct);

    Console.WriteLine($"\nQuery: \"{query}\"");
    if (wing is not null || room is not null)
        Console.WriteLine($"Filter: wing={wing ?? "*"} room={room ?? "*"}");
    Console.WriteLine();

    if (response.Results.Count == 0)
    {
        Console.WriteLine("No results.");
        return;
    }

    foreach (var (hit, i) in response.Results.Select((h, i) => (h, i + 1)))
    {
        Console.WriteLine($"[{i}] {hit.Wing}/{hit.Room}  sim={hit.Similarity:F3}  src={hit.SourceFile}");
        Console.WriteLine($"    {hit.Text[..Math.Min(200, hit.Text.Length)].Replace('\n', ' ')}");
        Console.WriteLine();
    }
});

rootCmd.Add(searchCmd);

// ---------------------------------------------------------------------------
// status
// ---------------------------------------------------------------------------

var statusCmd = new Command("status", "Show palace status");

statusCmd.SetAction((result, ct) =>
{
    var palace = result.GetValue(palaceOpt)!;
    var backend = result.GetValue(backendOpt);
    try
    {
        using var s = PalaceSession.Open(palace, backend: backend);
        var count = s.Collection.Count();
        var rows = s.Collection.Get(limit: 10_000, includeMetadatas: true, includeDocuments: false);
        var wings = rows
            .GroupBy(r => r.Metadata?.GetValueOrDefault("wing") as string ?? "unknown")
            .ToDictionary(g => g.Key, g => g.Count());
        Console.WriteLine($"Palace: {palace}  [{backend}]");
        Console.WriteLine($"Drawers: {count}");
        Console.WriteLine($"\nWings ({wings.Count}):");
        foreach (var (w, c) in wings.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"  {w,-30} {c,6} drawers");
    }
    catch
    {
        Console.WriteLine("No palace found. Run: mempalace mine <dir>");
    }

    return Task.CompletedTask;
});

rootCmd.Add(statusCmd);

// ---------------------------------------------------------------------------
// wake-up
// ---------------------------------------------------------------------------

var wakeCmd = new Command("wake-up", "Print L0 (identity) + L1 (essential story)");
var wakeWing = new Option<string?>("--wing") { Description = "Filter L1 by wing" };
wakeWing.DefaultValueFactory = _ => null;
wakeCmd.Add(wakeWing);

wakeCmd.SetAction((result, ct) =>
{
    var palace = result.GetValue(palaceOpt)!;
    var wing = result.GetValue(wakeWing);
    var backend = result.GetValue(backendOpt);
    var stack = new MemoryStack(palace, backend: backend);
    var output = stack.WakeUp(wing);
    Console.WriteLine(string.IsNullOrEmpty(output) ? "(no memory yet)" : output);
    return Task.CompletedTask;
});

rootCmd.Add(wakeCmd);

// ---------------------------------------------------------------------------
// mcp  — start stdio JSON-RPC MCP server
// ---------------------------------------------------------------------------

var mcpCmd = new Command("mcp", "Start MCP server (stdio JSON-RPC 2.0)");

mcpCmd.SetAction(async (result, ct) =>
{
    var palace = result.GetValue(palaceOpt)!;
    var backend = result.GetValue(backendOpt);
    var embedder = await GetEmbedder();
    var cfg = MempalaceConfig.Load();
    var ctx = new McpToolContext(
        palace,
        cfg.CollectionName,
        Path.Combine(Path.GetDirectoryName(palace) ?? palace, "knowledge_graph.sqlite3"),
        embedder,
        backend);

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };
    await McpServer.RunAsync(ctx, ct: cts.Token);
});

rootCmd.Add(mcpCmd);

// ---------------------------------------------------------------------------
// init  — auto-detect rooms and write mempalace.yaml
// ---------------------------------------------------------------------------

var initCmd = new Command("init", "Auto-detect rooms from folder structure and write mempalace.yaml");
var initDirArg = new Argument<DirectoryInfo>("dir") { Description = "Project directory to initialise" };
var initWingOpt = new Option<string?>("--wing") { Description = "Wing name override" };
initWingOpt.DefaultValueFactory = _ => null;

initCmd.Add(initDirArg);
initCmd.Add(initWingOpt);

initCmd.SetAction((result, ct) =>
{
    var dir = result.GetValue(initDirArg)!;
    var wing = result.GetValue(initWingOpt);
    var rooms = RoomDetector.DetectAndSave(dir.FullName, wing);
    Console.WriteLine($"Wing: {wing ?? Path.GetFileName(dir.FullName)}");
    Console.WriteLine($"Detected {rooms.Count} rooms:");
    foreach (var room in rooms)
        Console.WriteLine($"  {room.Name,-24} {room.Description}");
    Console.WriteLine($"\nConfig written to {Path.Combine(dir.FullName, "mempalace.yaml")}");
    Console.WriteLine($"Next: mempalace mine {dir.FullName}");
    return Task.CompletedTask;
});

rootCmd.Add(initCmd);

// ---------------------------------------------------------------------------
// split — split mega transcript files into per-session files
// ---------------------------------------------------------------------------

var splitCmd = new Command("split", "Split concatenated transcript files into per-session files");
var splitSrcArg = new Argument<DirectoryInfo>("source") { Description = "Directory containing .txt transcript files" };
var splitOutOpt = new Option<string?>("--output-dir") { Description = "Output directory (default: same as source)" };
var splitMinOpt = new Option<int>("--min-sessions") { Description = "Only split files with at least N sessions" };
var splitDryOpt = new Option<bool>("--dry-run") { Description = "Show what would happen without writing files" };

splitOutOpt.DefaultValueFactory = _ => null;
splitMinOpt.DefaultValueFactory = _ => 2;

splitCmd.Add(splitSrcArg);
splitCmd.Add(splitOutOpt);
splitCmd.Add(splitMinOpt);
splitCmd.Add(splitDryOpt);

splitCmd.SetAction((result, ct) =>
{
    var src = result.GetValue(splitSrcArg)!;
    var outDir = result.GetValue(splitOutOpt);
    var minSessions = result.GetValue(splitMinOpt);
    var dryRun = result.GetValue(splitDryOpt);
    var stats = SplitMegaFiles.SplitDirectory(src.FullName, outDir, minSessions, dryRun);
    if (dryRun)
        Console.WriteLine(
            $"DRY RUN — would create {stats.TotalSessionsWritten} files from {stats.TotalMegaFiles} mega-files");
    else
        Console.WriteLine($"Done — created {stats.TotalSessionsWritten} files from {stats.TotalMegaFiles} mega-files");
    return Task.CompletedTask;
});

rootCmd.Add(splitCmd);

// ---------------------------------------------------------------------------
// hook  — lifecycle hook runner (session-start | stop | precompact)
// ---------------------------------------------------------------------------

var hookCmd = new Command("hook", "Run a lifecycle hook (reads JSON from stdin, writes JSON to stdout)");
var hookNameArg = new Argument<string>("name") { Description = "Hook name: session-start | stop | precompact" };
var hookHarnessOpt = new Option<string>("--harness") { Description = "Harness: claude-code | codex" };
hookHarnessOpt.DefaultValueFactory = _ => "claude-code";

hookCmd.Add(hookNameArg);
hookCmd.Add(hookHarnessOpt);

hookCmd.SetAction(async (result, ct) =>
{
    var hookName = result.GetValue(hookNameArg)!;
    var harness = result.GetValue(hookHarnessOpt) ?? "claude-code";
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };
    await HooksCli.RunHookAsync(hookName, harness, ct: cts.Token);
});

rootCmd.Add(hookCmd);

// ---------------------------------------------------------------------------
// instructions — print usage instructions for a skill
// ---------------------------------------------------------------------------

var instrCmd = new Command("instructions", "Print usage instructions for a mempalace skill");
var instrNameArg = new Argument<string>("name") { Description = "Skill: init | search | mine | help | status" };

instrCmd.Add(instrNameArg);

instrCmd.SetAction((result, ct) =>
{
    var name = result.GetValue(instrNameArg)!;
    var text = name.ToLowerInvariant() switch
    {
        "init" => """
                  mempalace init <dir>
                    Auto-detects rooms from your folder structure.
                    Writes mempalace.yaml to the project root.
                    Run 'mempalace mine <dir>' after init.
                  """,
        "mine" => """
                  mempalace mine <dir> [--mode files|convos] [--wing NAME] [--dry-run]
                    Mine files into the palace. Use --mode convos for chat exports.
                    Supports: .txt .md .json .jsonl .py .ts .cs and more.
                  """,
        "search" => """
                    mempalace search <query> [--wing WING] [--room ROOM] [--n N]
                      Semantic search over palace memory.
                      Returns top N results (default 5) with similarity scores.
                    """,
        "status" => """
                    mempalace status [--palace PATH]
                      Shows palace drawer count and breakdown by wing.
                    """,
        "help" => """
                  mempalace <command> --help
                  Commands: init | mine | search | status | wake-up | mcp | split | hook | instructions
                  """,
        _ => $"Unknown skill: {name}. Available: init, mine, search, status, help"
    };
    Console.WriteLine(text);
    return Task.CompletedTask;
});

rootCmd.Add(instrCmd);

// ---------------------------------------------------------------------------
// Run
// ---------------------------------------------------------------------------

var parseResult = rootCmd.Parse(args);
_useInt8 = parseResult.GetValue(int8Opt);
var exitCode = await parseResult.InvokeAsync();
_embedder?.Dispose();
return exitCode;
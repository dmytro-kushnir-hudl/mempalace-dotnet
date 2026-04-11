using Microsoft.Extensions.AI;
using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chroma;
using Chroma.Embeddings;
using Mempalace;

// ---------------------------------------------------------------------------
// Lazy embedder — created on first use, shared across commands
// ---------------------------------------------------------------------------

DefaultEmbeddingProvider? _embedder = null;
async Task<DefaultEmbeddingProvider> GetEmbedder()
{
    if (_embedder is null)
    {
        Console.Error.WriteLine("Loading embedding model...");
        _embedder = await DefaultEmbeddingProvider.CreateAsync();
    }
    return _embedder;
}

var config = MempalaceConfig.Load();

// ---------------------------------------------------------------------------
// Root command
// ---------------------------------------------------------------------------

var rootCmd = new RootCommand("mempalace — AI memory manager");

var palaceOpt  = new Option<string>("--palace", () => config.PalacePath, "Path to palace directory");
var backendOpt = new Option<VectorBackend>("--backend", () => VectorBackend.Chroma, "Vector store backend: Chroma | Sqlite");
rootCmd.AddGlobalOption(palaceOpt);
rootCmd.AddGlobalOption(backendOpt);

// ---------------------------------------------------------------------------
// mine
// ---------------------------------------------------------------------------

var mineCmd  = new Command("mine", "Mine files or conversations into the palace");
var mineDirArg  = new Argument<DirectoryInfo>("dir", "Directory to mine");
var mineModeOpt = new Option<string>("--mode", () => "files", "Mining mode: files | convos");
var mineWingOpt = new Option<string?>("--wing", () => null, "Wing override");
var mineAgentOpt= new Option<string>("--agent", () => "mempalace", "Agent name");
var mineLimitOpt= new Option<int>("--limit", () => 0, "Max files (0 = all)");
var mineDryOpt  = new Option<bool>("--dry-run", "Preview without writing");

mineCmd.AddArgument(mineDirArg);
mineCmd.AddOption(mineModeOpt);
mineCmd.AddOption(mineWingOpt);
mineCmd.AddOption(mineAgentOpt);
mineCmd.AddOption(mineLimitOpt);
mineCmd.AddOption(mineDryOpt);

mineCmd.SetHandler(async (dir, mode, palace, wing, agent, limit, dryRun, backend) =>
{
    var embedder = await GetEmbedder();
    if (mode == "convos")
    {
        await ConvoMiner.MineConvosAsync(
            dir.FullName, palace, embedder,
            new ConvoMinerOptions(Wing: wing, Agent: agent, Limit: limit, DryRun: dryRun, Backend: backend));
    }
    else
    {
        await Miner.MineAsync(
            dir.FullName, palace, embedder,
            new MinerOptions(WingOverride: wing, Agent: agent, Limit: limit, DryRun: dryRun, Backend: backend));
    }
}, mineDirArg, mineModeOpt, palaceOpt, mineWingOpt, mineAgentOpt, mineLimitOpt, mineDryOpt, backendOpt);

rootCmd.AddCommand(mineCmd);

// ---------------------------------------------------------------------------
// search
// ---------------------------------------------------------------------------

var searchCmd  = new Command("search", "Semantic search over the palace");
var searchQuery = new Argument<string>("query", "Search query");
var searchWingOpt = new Option<string?>("--wing", () => null, "Filter by wing");
var searchRoomOpt = new Option<string?>("--room", () => null, "Filter by room");
var searchNOpt    = new Option<int>("--n", () => 5, "Number of results");

searchCmd.AddArgument(searchQuery);
searchCmd.AddOption(searchWingOpt);
searchCmd.AddOption(searchRoomOpt);
searchCmd.AddOption(searchNOpt);

searchCmd.SetHandler(async (query, palace, wing, room, n, backend) =>
{
    var embedder = await GetEmbedder();
    var response = await Searcher.SearchMemoriesAsync(query, palace, embedder, wing, room, n, backend: backend);

    Console.WriteLine($"\nQuery: \"{query}\"");
    if (wing is not null || room is not null)
        Console.WriteLine($"Filter: wing={wing ?? "*"} room={room ?? "*"}");
    Console.WriteLine();

    if (response.Results.Count == 0) { Console.WriteLine("No results."); return; }

    foreach (var (hit, i) in response.Results.Select((h, i) => (h, i + 1)))
    {
        Console.WriteLine($"[{i}] {hit.Wing}/{hit.Room}  sim={hit.Similarity:F3}  src={hit.SourceFile}");
        Console.WriteLine($"    {hit.Text[..Math.Min(200, hit.Text.Length)].Replace('\n', ' ')}");
        Console.WriteLine();
    }
}, searchQuery, palaceOpt, searchWingOpt, searchRoomOpt, searchNOpt, backendOpt);

rootCmd.AddCommand(searchCmd);

// ---------------------------------------------------------------------------
// status
// ---------------------------------------------------------------------------

var statusCmd = new Command("status", "Show palace status");
statusCmd.SetHandler((palace, backend) =>
{
    try
    {
        using var s = PalaceSession.Open(palace, backend: backend);
        var count = s.Collection.Count();
        var rows  = s.Collection.Get(limit: 10_000, includeMetadatas: true, includeDocuments: false);
        var wings = rows
            .GroupBy(r => r.Metadata?.GetValueOrDefault("wing") as string ?? "unknown")
            .ToDictionary(g => g.Key, g => g.Count());
        Console.WriteLine($"Palace: {palace}  [{backend}]");
        Console.WriteLine($"Drawers: {count}");
        Console.WriteLine($"\nWings ({wings.Count}):");
        foreach (var (w, c) in wings.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"  {w,-30} {c,6} drawers");
    }
    catch { Console.WriteLine("No palace found. Run: mempalace mine <dir>"); }
}, palaceOpt, backendOpt);

rootCmd.AddCommand(statusCmd);

// ---------------------------------------------------------------------------
// wake-up
// ---------------------------------------------------------------------------

var wakeCmd  = new Command("wake-up", "Print L0 (identity) + L1 (essential story)");
var wakeWing = new Option<string?>("--wing", () => null, "Filter L1 by wing");
wakeCmd.AddOption(wakeWing);

wakeCmd.SetHandler((palace, wing, backend) =>
{
    var stack  = new MemoryStack(palace, backend: backend);
    var output = stack.WakeUp(wing);
    Console.WriteLine(string.IsNullOrEmpty(output) ? "(no memory yet)" : output);
}, palaceOpt, wakeWing, backendOpt);

rootCmd.AddCommand(wakeCmd);

// ---------------------------------------------------------------------------
// mcp  — start stdio JSON-RPC MCP server
// ---------------------------------------------------------------------------

var mcpCmd = new Command("mcp", "Start MCP server (stdio JSON-RPC 2.0)");
mcpCmd.SetHandler(async (palace, backend) =>
{
    var embedder = await GetEmbedder();
    var cfg = MempalaceConfig.Load();
    var ctx = new McpToolContext(
        palace,
        cfg.CollectionName,
        Path.Combine(Path.GetDirectoryName(palace) ?? palace, "knowledge_graph.sqlite3"),
        embedder,
        backend);

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    await McpServer.RunAsync(ctx, cts.Token);
}, palaceOpt, backendOpt);

rootCmd.AddCommand(mcpCmd);

// ---------------------------------------------------------------------------
// init  — auto-detect rooms and write mempalace.yaml
// ---------------------------------------------------------------------------

var initCmd    = new Command("init", "Auto-detect rooms from folder structure and write mempalace.yaml");
var initDirArg = new Argument<DirectoryInfo>("dir", "Project directory to initialise");
var initWingOpt = new Option<string?>("--wing", () => null, "Wing name override");

initCmd.AddArgument(initDirArg);
initCmd.AddOption(initWingOpt);

initCmd.SetHandler((dir, wing) =>
{
    var rooms = RoomDetector.DetectAndSave(dir.FullName, wing);
    Console.WriteLine($"Wing: {wing ?? Path.GetFileName(dir.FullName)}");
    Console.WriteLine($"Detected {rooms.Count} rooms:");
    foreach (var room in rooms)
        Console.WriteLine($"  {room.Name,-24} {room.Description}");
    Console.WriteLine($"\nConfig written to {Path.Combine(dir.FullName, "mempalace.yaml")}");
    Console.WriteLine($"Next: mempalace mine {dir.FullName}");
}, initDirArg, initWingOpt);

rootCmd.AddCommand(initCmd);

// ---------------------------------------------------------------------------
// split — split mega transcript files into per-session files
// ---------------------------------------------------------------------------

var splitCmd       = new Command("split", "Split concatenated transcript files into per-session files");
var splitSrcArg    = new Argument<DirectoryInfo>("source", "Directory containing .txt transcript files");
var splitOutOpt    = new Option<string?>("--output-dir", () => null, "Output directory (default: same as source)");
var splitMinOpt    = new Option<int>("--min-sessions", () => 2, "Only split files with at least N sessions");
var splitDryOpt    = new Option<bool>("--dry-run", "Show what would happen without writing files");

splitCmd.AddArgument(splitSrcArg);
splitCmd.AddOption(splitOutOpt);
splitCmd.AddOption(splitMinOpt);
splitCmd.AddOption(splitDryOpt);

splitCmd.SetHandler((src, outDir, minSessions, dryRun) =>
{
    var stats = SplitMegaFiles.SplitDirectory(
        src.FullName, outDir, minSessions, dryRun);

    if (dryRun)
        Console.WriteLine($"DRY RUN — would create {stats.TotalSessionsWritten} files from {stats.TotalMegaFiles} mega-files");
    else
        Console.WriteLine($"Done — created {stats.TotalSessionsWritten} files from {stats.TotalMegaFiles} mega-files");
}, splitSrcArg, splitOutOpt, splitMinOpt, splitDryOpt);

rootCmd.AddCommand(splitCmd);

// ---------------------------------------------------------------------------
// hook  — lifecycle hook runner (session-start | stop | precompact)
// ---------------------------------------------------------------------------

var hookCmd     = new Command("hook", "Run a lifecycle hook (reads JSON from stdin, writes JSON to stdout)");
var hookNameArg = new Argument<string>("name", "Hook name: session-start | stop | precompact");
var hookHarnessOpt = new Option<string>("--harness", () => "claude-code", "Harness: claude-code | codex");

hookCmd.AddArgument(hookNameArg);
hookCmd.AddOption(hookHarnessOpt);

hookCmd.SetHandler(async (hookName, harness) =>
{
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    await HooksCli.RunHookAsync(hookName, harness, ct: cts.Token);
}, hookNameArg, hookHarnessOpt);

rootCmd.AddCommand(hookCmd);

// ---------------------------------------------------------------------------
// instructions — print usage instructions for a skill
// ---------------------------------------------------------------------------

var instrCmd    = new Command("instructions", "Print usage instructions for a mempalace skill");
var instrNameArg = new Argument<string>("name", "Skill: init | search | mine | help | status");

instrCmd.AddArgument(instrNameArg);

instrCmd.SetHandler((name) =>
{
    var text = name.ToLowerInvariant() switch
    {
        "init"   => """
            mempalace init <dir>
              Auto-detects rooms from your folder structure.
              Writes mempalace.yaml to the project root.
              Run 'mempalace mine <dir>' after init.
            """,
        "mine"   => """
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
        "help"   => """
            mempalace <command> --help
            Commands: init | mine | search | status | wake-up | mcp | split | hook | instructions
            """,
        _ => $"Unknown skill: {name}. Available: init, mine, search, status, help",
    };
    Console.WriteLine(text);
}, instrNameArg);

rootCmd.AddCommand(instrCmd);

// ---------------------------------------------------------------------------
// Run
// ---------------------------------------------------------------------------

var result = await rootCmd.InvokeAsync(args);
_embedder?.Dispose();
return result;

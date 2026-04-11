# mempalace-dotnet

**.NET 10 port of [mempalace](https://github.com/milla-jovovich/mempalace)** — a semantic memory system for AI agents. Mine projects and conversations into a searchable palace, then retrieve them via 22 MCP tools.

## Features

- **22 MCP tools** — search, add/delete drawers, knowledge graph, diary, AAAK compression, entity detection
- **Two vector backends** — ChromaDB (default) or SQLite via [sqlite-vec](https://github.com/asg017/sqlite-vec)
- **Auto-save hooks** — Claude Code / Codex lifecycle hooks (stop, precompact) with 15-message auto-save
- **Conversation mining** — ingests Claude Code JSONL, Codex JSONL, Claude.ai JSON, ChatGPT JSON, Slack JSON, plain text
- **Local ONNX embeddings** — `all-MiniLM-L6-v2` via Microsoft.ML.OnnxRuntime, no API key required
- **Claude Code plugin** — `mempalace-dotnet@mempalace-dotnet` with skill, commands, and hooks

## Quick start

```bash
# Clone
git clone https://github.com/dmytro-kushnir-hudl/mempalace-dotnet
cd mempalace-dotnet

# Build
dotnet build src/Mempalace.Cli -c Release

# Mine a project
./src/Mempalace.Cli/bin/Release/net10.0/mempalace-dotnet mine ~/my-project

# Search
./src/Mempalace.Cli/bin/Release/net10.0/mempalace-dotnet search "authentication decisions"

# Start MCP server
./src/Mempalace.Cli/bin/Release/net10.0/mempalace-dotnet mcp
```

## SQLite backend

Pass `--backend Sqlite` to any command to use the SQLite backend instead of ChromaDB:

```bash
mempalace-dotnet mine ~/project --backend Sqlite
mempalace-dotnet search "auth bug" --backend Sqlite
mempalace-dotnet mcp --backend Sqlite
```

The SQLite backend uses [sqlite-vec](https://github.com/asg017/sqlite-vec) for approximate nearest-neighbour search when the `vec0` extension is loadable, and falls back to brute-force cosine similarity otherwise. Set `SQLITE_VEC_PATH` to the `.dylib`/`.so` path if you want to use ANN.

## ChromaDB backend (FFI)

The ChromaDB backend uses a custom Rust C FFI client. The native bindings (`libchromadb_dotnet.dylib` / `.so`) are bundled with the Chroma.Client project.

The client is sourced from a fork of [chroma-core/chroma](https://github.com/chroma-core/chroma):

> **Fork:** [github.com/dmytro-kushnir-hudl/chroma](https://github.com/dmytro-kushnir-hudl/chroma)
>
> The fork adds a .NET C FFI layer (`clients/dotnet/`) on top of the Rust core. Point the `Chroma.Client.csproj` project reference at your local clone if you want to rebuild the native bindings from source.

The project reference in `src/Mempalace/Mempalace.csproj` assumes the fork is checked out alongside this repo:

```
~/src/
  mempalace-dotnet/   ← this repo
  chroma/             ← dmytro-kushnir-hudl/chroma fork
    clients/dotnet/
      src/Chroma.Client/
```

If your chroma fork is elsewhere, update the `<ProjectReference>` path in `src/Mempalace/Mempalace.csproj`.

## MCP server setup

Add to your `~/.claude/mcp.json`:

```json
{
  "mempalace": {
    "command": "/path/to/mempalace-dotnet",
    "args": ["mcp"]
  }
}
```

Or use the Claude Code plugin (see below).

## Claude Code plugin

Install from the local marketplace:

1. Add to `~/.claude/settings.json`:

```json
{
  "extraKnownMarketplaces": {
    "mempalace-dotnet": {
      "source": { "source": "directory", "path": "/path/to/mempalace-dotnet" }
    }
  }
}
```

2. Install:

```bash
claude plugin install mempalace-dotnet@mempalace-dotnet
```

The plugin wires up:
- **MCP server** via `dotnet run`
- **Stop hook** — auto-saves every 15 exchanges
- **PreCompact hook** — always blocks with a save-everything prompt
- **Skill** — `mempalace` skill for guided init/mine/search/status

## Commands

```
mempalace-dotnet mine <dir>       Mine files (--mode convos for chats, --backend Sqlite)
mempalace-dotnet search <query>   Semantic search (--wing --room --n)
mempalace-dotnet status           Palace overview
mempalace-dotnet wake-up          Print L0 identity + L1 essential story
mempalace-dotnet init <dir>       Auto-detect rooms, write mempalace.yaml
mempalace-dotnet split <dir>      Split mega transcript files into per-session files
mempalace-dotnet hook <name>      Run lifecycle hook (session-start | stop | precompact)
mempalace-dotnet instructions <cmd>  Print usage instructions
mempalace-dotnet mcp              Start stdio MCP server
```

## Architecture

```
src/Mempalace/
  Storage/
    IVectorCollection.cs        Backend-agnostic interface
    MetadataFilter.cs           Equality + AND filter DSL
    ChromaVectorCollection.cs   ChromaDB adapter (Rust FFI)
    SqliteVectorCollection.cs   SQLite adapter (sqlite-vec + cosine fallback)
  Palace.cs          PalaceSession — opens IVectorCollection by VectorBackend enum
  Miner.cs           Project file mining (.gitignore-aware)
  ConvoMiner.cs      Conversation ingestion (6 chat export formats)
  Searcher.cs        Semantic search
  Layers.cs          4-layer memory stack (identity / story / recall / deep search)
  McpTools.cs        22 MCP tool implementations
  McpServer.cs       stdio JSON-RPC 2.0 server
  KnowledgeGraph.cs  SQLite-backed temporal entity graph
  EntityRegistry.cs  Persistent entity registry + Wikipedia research
  EntityDetector.cs  Heuristic people/project detector
  RoomDetector.cs    Auto-detect rooms from folder structure
  HooksCli.cs        Lifecycle hook runner
  SplitMegaFiles.cs  Transcript session splitter
  Dialect.cs         AAAK lossy compression format
  GeneralExtractor.cs  Extract typed memories (decision/milestone/problem/preference)
```

## Tests

```bash
dotnet test        # 193 unit tests
./scripts/acceptance_test.sh --sqlite-only   # end-to-end acceptance
./scripts/acceptance_test.sh --chroma-only   # requires ChromaDB native lib
```

## License

MIT — see [LICENSE](LICENSE).

Original Python implementation: [milla-jovovich/mempalace](https://github.com/milla-jovovich/mempalace)

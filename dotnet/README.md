# mempalace-dotnet

**.NET 10 port of [mempalace](https://github.com/milla-jovovich/mempalace)** — a semantic memory system for AI agents. Mine projects and conversations into a searchable palace, then retrieve them via 22 MCP tools.

## Features

- **22 MCP tools** — search, add/delete drawers, knowledge graph, diary, AAAK compression, entity detection
- **Two vector backends** — ChromaDB (in-process Rust FFI) or SQLite (pure managed, no native deps)
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

## Backends

### SQLite (recommended for most users)

Zero native dependencies — pure managed code with brute-force cosine similarity over L2-normalised embeddings. Fast for palaces up to ~10k drawers.

```bash
mempalace-dotnet mine ~/project --backend Sqlite
mempalace-dotnet search "auth bug" --backend Sqlite
mempalace-dotnet mcp --backend Sqlite
```

**Benchmark (2026-04-11, M-series Mac, ShortRunJob):**

| Operation   | Mean   | Allocated |
|-------------|--------|-----------|
| Count       | ~1 ms  | 17 KB     |
| GetFiltered | ~1 ms  | 69–217 KB |
| Search      | ~13 ms | 437 KB    |
| Upsert      | ~13 ms | 395 KB    |

Search and upsert cost is dominated by the ONNX embedding step (~12 ms); storage is O(1) at these scales.

### ChromaDB (in-process Rust FFI)

Uses a custom Rust C FFI client bundled as a native dylib/so. ~2–7× slower than SQLite for non-embedding operations; useful when you want Chroma's query semantics or already have an existing Chroma palace.

The native bindings are sourced from a fork of [chroma-core/chroma](https://github.com/chroma-core/chroma):

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
mempalace-dotnet mine <dir>            Mine files (--mode convos for chats, --backend Sqlite)
mempalace-dotnet search <query>        Semantic search (--wing --room --n)
mempalace-dotnet status                Palace overview
mempalace-dotnet wake-up               Print L0 identity + L1 essential story
mempalace-dotnet init <dir>            Auto-detect rooms, write mempalace.yaml
mempalace-dotnet split <dir>           Split mega transcript files into per-session files
mempalace-dotnet hook <name>           Run lifecycle hook (session-start | stop | precompact)
mempalace-dotnet instructions <cmd>    Print usage instructions
mempalace-dotnet mcp                   Start stdio MCP server
```

## Architecture

```
src/Mempalace/
  Storage/
    IVectorCollection.cs        Backend-agnostic interface
    MetadataFilter.cs           Equality + AND filter DSL
    ChromaVectorCollection.cs   ChromaDB adapter (Rust FFI, cosine similarity from L2 dist)
    SqliteVectorCollection.cs   SQLite adapter (brute-force cosine, managed-only)
  Palace.cs            PalaceSession — opens IVectorCollection by VectorBackend enum
  Miner.cs             Project file mining (.gitignore-aware)
  ConvoMiner.cs        Conversation ingestion (6 chat export formats)
  Searcher.cs          Semantic search
  Layers.cs            4-layer memory stack (identity / story / recall / deep search)
  McpTools.cs          22 MCP tool implementations (all responses camelCase JSON)
  McpServer.cs         stdio JSON-RPC 2.0 server (TextReader/TextWriter injectable for tests)
  KnowledgeGraph.cs    SQLite-backed temporal entity graph
  EntityRegistry.cs    Persistent entity registry + Wikipedia research
  EntityDetector.cs    Heuristic people/project detector (CamelCase-aware)
  RoomDetector.cs      Auto-detect rooms from folder structure
  HooksCli.cs          Lifecycle hook runner
  SplitMegaFiles.cs    Transcript session splitter
  Dialect.cs           AAAK lossy compression format
  GeneralExtractor.cs  Extract typed memories (decision/milestone/problem/preference)
```

## Tests

```bash
# Unit tests (193)
dotnet test tests/Mempalace.Tests/

# Integration tests — 122 tests covering all 22 MCP tools, both backends
dotnet test tests/Mempalace.IntegrationTests/

# Filter by group
dotnet test tests/Mempalace.IntegrationTests/ --filter "FullyQualifiedName~T31_T38"

# All tests
dotnet test
```

The integration tests drive `McpServer.RunAsync` in-process via `StringReader`/`StringWriter` — no subprocess, no ports. The full suite (315 tests) runs in ~25 s.

## Benchmarks

```bash
dotnet run -c Release --project benchmarks/Mempalace.Benchmarks/
```

Compares Sqlite vs Chroma for `Upsert`, `Search`, `GetFiltered`, and `Count` at 50 and 200 drawer counts. Uses `[ShortRunJob]` (1 warmup, 3 iterations). See `docs/benchmark-results-2026-04-11.md` for full results.

## TODO

- [ ] **Palace-level model tag** — store embedding model name (e.g. `bge-small-en-v1.5`) in a `palace_meta` SQLite table on first mine; check on every `PalaceSession.Open` and fail loudly if the model doesn't match. Prevents silent garbage results when switching models without re-mining.

## License

MIT — see [LICENSE](LICENSE).

Original Python implementation: [milla-jovovich/mempalace](https://github.com/milla-jovovich/mempalace)

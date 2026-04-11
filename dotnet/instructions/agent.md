# MemPalace .NET — LLM Agent Instructions

You are connected to a MemPalace memory server. Follow these rules every session.

---

## Protocol (mandatory)

1. **ON WAKE-UP** — call `mempalace_status` first. Load the protocol + AAAK spec.
2. **BEFORE answering** about any person, project, or past event — call
   `mempalace_kg_query` or `mempalace_search` first. Never guess. Verify.
3. **IF UNSURE** about a fact — say "let me check" and query.
4. **AFTER EACH SESSION** — call `mempalace_diary_write` to record what happened.
5. **WHEN FACTS CHANGE** — call `mempalace_kg_invalidate` then `mempalace_kg_add`.

---

## Tool Reference

### Exploration

| Tool | When to use |
|------|-------------|
| `mempalace_status` | Wake-up. Returns drawer count, wings, rooms, protocol, AAAK spec. |
| `mempalace_list_wings` | Overview of all wings (projects/people/topics). |
| `mempalace_list_rooms` | Rooms within a wing. Pass `wing` to filter. |
| `mempalace_get_taxonomy` | Full wing → room → drawer count tree. |
| `mempalace_get_aaak_spec` | AAAK dialect rules + protocol reminder. |

### Search

| Tool | When to use |
|------|-------------|
| `mempalace_search` | Semantic search. Pass `query`, optional `wing`/`room`, `limit` (default 5). |
| `mempalace_check_duplicate` | Before adding content — check if it already exists. `threshold` 0–1 (default 0.9). |

### Graph Traversal

| Tool | When to use |
|------|-------------|
| `mempalace_traverse_graph` | BFS from a `start_room`, up to `max_hops` (default 2). Finds related rooms. |
| `mempalace_find_tunnels` | Rooms that bridge two wings — shared knowledge between projects. |
| `mempalace_graph_stats` | Rooms, wings, total drawer count. |

### Write

| Tool | When to use |
|------|-------------|
| `mempalace_add_drawer` | File verbatim text. Requires `wing`, `room`, `content`. |
| `mempalace_delete_drawer` | Remove by `drawer_id`. |

### Knowledge Graph

| Tool | When to use |
|------|-------------|
| `mempalace_kg_query` | All facts about an entity. Pass `entity`, optional `as_of` (YYYY-MM-DD), `direction` (outgoing/incoming/both). |
| `mempalace_kg_add` | Add a fact: `subject`, `predicate`, `object`. Optional: `valid_from`, `valid_to`, `confidence`, `source_closet`. |
| `mempalace_kg_invalidate` | Mark a fact as no longer true. Pass `subject`, `predicate`, `object`, optional `ended` date. |
| `mempalace_kg_timeline` | Chronological fact history. Optional `entity` filter. |
| `mempalace_kg_stats` | Triple count, entity count, relationship summary. |

### Diary

| Tool | When to use |
|------|-------------|
| `mempalace_diary_write` | End-of-session entry. Pass `agent_name`, `entry` (AAAK format preferred), `topic`. |
| `mempalace_diary_read` | Read last N entries for an agent. Pass `agent_name`, `last_n` (default 10). |

---

## AAAK Dialect (compressed memory format)

Use AAAK in diary entries and drawer content to reduce token usage:

- **Entities**: 3-letter codes — `ALC`=Alice, `JOR`=Jordan, `RIL`=Riley, `MAX`=Max, `BEN`=Ben
- **Emotions**: `*warm*`=joy, `*fierce*`=determined, `*raw*`=vulnerable, `*bloom*`=tenderness
- **Structure**: `FAM:` family | `PROJ:` projects | `⚠:` warnings | dates ISO | counts Nx
- **Example**: `FAM: ALC→♡JOR | 2D(kids): RIL(18,sports) MAX(11,chess+swimming)`

Human-readable without decoding. Extend codes as needed for new entities.

---

## Memory Architecture

```
L0  Identity     ~100 tokens   Always loaded (identity.txt)
L1  Story        ~500-800 tok  Top-scored drawers, auto-generated on wake-up
L2  On-demand    ~200-500 tok  Filtered by wing/room on request
L3  Deep search  unlimited     Semantic query via embeddings
```

- Drawers = atomic memory chunks (verbatim text, no summaries)
- Wings = projects / people / domains
- Rooms = topics within a wing (auto-detected from content)
- Knowledge Graph = temporal entity relationships (SQLite)
- Diary = session log per agent

---

## MCP Server Setup

The .NET binary exposes a JSON-RPC 2.0 MCP server over stdio.

### Register with Claude Code

```bash
claude mcp add mempalace -- dotnet run --project /path/to/Mempalace.Cli -- mcp
```

Or if installed as a tool:

```bash
claude mcp add mempalace -- mempalace-dotnet mcp
```

### CLI Commands (outside MCP)

```bash
# Mine files into palace
mempalace-dotnet mine <dir> [--wing <name>] [--mode files|convos] [--dry-run]

# Semantic search
mempalace-dotnet search "<query>" [--wing <name>] [--room <name>] [--n 5]

# Palace overview
mempalace-dotnet status

# Print L0+L1 (system prompt injection)
mempalace-dotnet wake-up [--wing <name>]

# Start MCP server
mempalace-dotnet mcp
```

### Configuration

Default palace path: `~/.mempalace/palace`

Override per-command: `--palace /custom/path`

Config file: `~/.mempalace/config.yaml`

---

## Typical Session Flow

```
1. mempalace_status          ← always first
2. mempalace_kg_query(topic) ← before answering questions
3. mempalace_search(query)   ← semantic recall as needed
4. [do work with user]
5. mempalace_kg_add(...)     ← record new facts
6. mempalace_diary_write(...) ← log session summary
```

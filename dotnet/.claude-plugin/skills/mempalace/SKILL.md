---
name: mempalace
description: MemPalace .NET — mine projects and conversations into a searchable memory palace. Use when asked about mempalace, memory palace, mining memories, searching memories, or palace setup.
allowed-tools: Bash, Read, Write, Edit, Glob, Grep
---

# MemPalace .NET

A searchable memory palace for AI — mine projects and conversations, then search them semantically.
Supports two vector backends: ChromaDB (default) and SQLite (via sqlite-vec).

## Prerequisites

Ensure the .NET port is built:

```bash
dotnet build /path/to/mempalace/dotnet/src/Mempalace.Cli
```

Or use the pre-built binary:

```bash
mempalace-dotnet --version
```

## Usage

MemPalace provides dynamic instructions via the CLI:

```bash
mempalace-dotnet instructions <command>
```

Where `<command>` is one of: `help`, `init`, `mine`, `search`, `status`.

Run the appropriate instructions command, then follow the returned instructions step by step.

## Backend selection

Pass `--backend Sqlite` to any command to use the SQLite backend instead of ChromaDB:

```bash
mempalace-dotnet mine <dir> --backend Sqlite
mempalace-dotnet search "query" --backend Sqlite
mempalace-dotnet mcp --backend Sqlite
```

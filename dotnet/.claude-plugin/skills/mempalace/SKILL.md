---
name: mempalace
description: MemPalace .NET — mine projects and conversations into a searchable memory palace. Use when asked about mempalace, memory palace, mining memories, searching memories, or palace setup.
allowed-tools: Bash, Read, Write, Edit, Glob, Grep
---

# MemPalace .NET

Semantic memory palace — mine projects/convos, search them, expose via 22 MCP tools.
Default model: **BGE-small-en-v1.5** (512 tokens, INT8, ~93 chunks/s on Apple Silicon P-cores).

## Quick check

```bash
mempalace-dotnet --version
mempalace-dotnet status
```

## Mine

```bash
# Source code / docs (skips csv, json, srt, docx, pdf by default)
mempalace-dotnet mine <dir>

# Force all file types (csv, json, srt, docx, pdf, etc.)
mempalace-dotnet mine <dir> --overdrive

# Claude Code / ChatGPT / Slack conversation exports
mempalace-dotnet mine <dir> --mode convos

# Specific wing, dry-run preview, limit files
mempalace-dotnet mine <dir> --wing myproject --dry-run --limit 10

# Custom palace path
mempalace-dotnet mine <dir> --palace ~/.mempalace/work-palace

# Switch model (MiniLM = 256 tok, BgeSmall = 512 tok — default)
mempalace-dotnet mine <dir> --model MiniLM
```

## Search

```bash
mempalace-dotnet search "query"
mempalace-dotnet search "query" --wing myproject --room technical --n 10
```

## Status

```bash
mempalace-dotnet status
mempalace-dotnet status --palace ~/.mempalace/work-palace
```

## MCP server

```bash
mempalace-dotnet mcp
mempalace-dotnet mcp --palace ~/.mempalace/palace
```

## Options (global)

| Flag | Default | Description |
|------|---------|-------------|
| `--palace` | `~/.mempalace/palace` | Palace directory |
| `--model` | `BgeSmall` | `MiniLM` (256 tok) or `BgeSmall` (512 tok) |
| `--int8` | `true` | INT8 quantized model (~3-4x faster) |
| `--backend` | `Sqlite` | `Sqlite` or `Chroma` |

## Mine flags

| Flag | Default | Description |
|------|---------|-------------|
| `--mode` | `files` | `files` or `convos` |
| `--overdrive` | `false` | Mine all file types (bypasses csv/json/srt/docx skip list) |
| `--wing` | auto | Override wing name |
| `--dry-run` | `false` | Preview without writing |
| `--limit` | `0` (all) | Max files |
| `--parallel` | `false` | Producer-consumer pipeline |

## Skipped by default (use --overdrive to include)

`.csv`, `.tsv`, `.json`, `.jsonl`, `.srt`, `.vtt`, `.docx`, `.doc`, `.xls`, `.xlsx`, `.pptx`, `.pdf`, `.epub`, `.lock`, `.sum`

> Note: `--mode convos` always includes `.json`/`.jsonl` — it has its own extension list.

## Init (auto-detect rooms)

```bash
mempalace-dotnet init <dir>
mempalace-dotnet init <dir> --wing myproject
```

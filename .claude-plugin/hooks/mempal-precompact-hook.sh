#!/bin/bash
# MemPalace .NET PreCompact Hook
INPUT=$(cat)

if command -v mempalace-dotnet &>/dev/null; then
    BIN="mempalace-dotnet"
else
    SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
    PLUGIN_SRC="$(readlink -f "${SCRIPT_DIR}/../.." 2>/dev/null || echo "${SCRIPT_DIR}/../..")"
    BIN="${PLUGIN_SRC}/src/Mempalace.Cli/bin/Release/net10.0/mempalace-dotnet"
fi

if [[ ! -x "$BIN" && "$BIN" != "mempalace-dotnet" ]]; then
    echo "{}"
    exit 0
fi

echo "$INPUT" | "$BIN" hook precompact --harness claude-code

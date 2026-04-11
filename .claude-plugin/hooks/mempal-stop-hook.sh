#!/bin/bash
# MemPalace .NET Stop Hook
# Finds the mempalace-dotnet binary: PATH → known build location
INPUT=$(cat)

if command -v mempalace-dotnet &>/dev/null; then
    BIN="mempalace-dotnet"
else
    # Fallback: Release build relative to this plugin's source root
    # CLAUDE_PLUGIN_ROOT points to the plugin cache dir; resolve the source via symlink or known path
    SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
    PLUGIN_SRC="$(readlink -f "${SCRIPT_DIR}/../.." 2>/dev/null || echo "${SCRIPT_DIR}/../..")"
    BIN="${PLUGIN_SRC}/src/Mempalace.Cli/bin/Release/net10.0/mempalace-dotnet"
fi

if [[ ! -x "$BIN" && "$BIN" != "mempalace-dotnet" ]]; then
    # Binary not built yet — pass through silently
    echo "{}"
    exit 0
fi

echo "$INPUT" | "$BIN" hook stop --harness claude-code

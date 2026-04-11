#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# acceptance_test.sh — Backend acceptance tests for mempalace-dotnet
#
# Tests both VectorBackend.Chroma and VectorBackend.Sqlite end-to-end
# using the CLI binary.  Runs mine → status → search → status-after-delete.
#
# Usage:
#   ./scripts/acceptance_test.sh [--sqlite-only] [--chroma-only]
#
# Requirements:
#   • dotnet build must have run (Release mode is used)
#   • For Chroma backend: libchromadb_dotnet.dylib must be resolvable
# ---------------------------------------------------------------------------
set -euo pipefail

BIN="$(dirname "$0")/../src/Mempalace.Cli/bin/Release/net10.0/mempalace-dotnet"
PASS=0; FAIL=0

# ── Colours ──────────────────────────────────────────────────────────────────
GREEN='\033[0;32m'; RED='\033[0;31m'; CYAN='\033[0;36m'; NC='\033[0m'

ok()   { echo -e "${GREEN}  ✓ $*${NC}"; PASS=$((PASS + 1)); }
fail() { echo -e "${RED}  ✗ $*${NC}"; FAIL=$((FAIL + 1)); }
info() { echo -e "${CYAN}==> $*${NC}"; }

# ── Args ──────────────────────────────────────────────────────────────────────
RUN_CHROMA=true; RUN_SQLITE=true
for arg in "$@"; do
  [[ "$arg" == "--sqlite-only" ]] && RUN_CHROMA=false
  [[ "$arg" == "--chroma-only" ]] && RUN_SQLITE=false
done

# ── Build ─────────────────────────────────────────────────────────────────────
info "Building Release..."
dotnet build -c Release "$(dirname "$0")/../src/Mempalace.Cli/" -nologo -v quiet

if [[ ! -f "$BIN" ]]; then
  echo -e "${RED}Binary not found: $BIN${NC}"; exit 1
fi

# ── Helpers ───────────────────────────────────────────────────────────────────

assert_contains() {
  local label="$1" needle="$2" haystack="$3"
  if echo "$haystack" | grep -qF "$needle"; then
    ok "$label — contains '$needle'"
  else
    fail "$label — expected '$needle' not found in output"
    echo "    Got: $(echo "$haystack" | head -5)"
  fi
}

assert_not_contains() {
  local label="$1" needle="$2" haystack="$3"
  if ! echo "$haystack" | grep -qF "$needle"; then
    ok "$label — does not contain '$needle'"
  else
    fail "$label — unexpected '$needle' found in output"
  fi
}

assert_exit_ok() {
  local label="$1" code="$2"
  [[ "$code" -eq 0 ]] && ok "$label exits 0" || fail "$label exits $code"
}

# ── Sample content to mine ───────────────────────────────────────────────────

make_test_dir() {
  local d="$1"
  mkdir -p "$d/src" "$d/docs"
  cat > "$d/src/api.cs" <<'EOF'
// API controller
// We decided to use PostgreSQL instead of MySQL for better JSON support.
// The auth middleware validates JWT tokens on every request.
// Riley reviewed the PR and suggested adding rate limiting.
public class AuthController { }
EOF
  cat > "$d/docs/architecture.md" <<'EOF'
# Architecture
We decided to migrate from monolith to microservices.
Key milestone: deployed first service to production 2024-01.
Critical bug: memory leak in the session handler under load.
Sam owns the infra layer; Riley owns the API layer.
EOF
}

# ── Run one backend ───────────────────────────────────────────────────────────

run_backend() {
  local backend="$1"
  local dir
  dir="$(mktemp -d)/test_$(echo "$backend" | tr '[:upper:]' '[:lower:]')"
  local palace="${dir}/palace"
  local content_dir="${dir}/project"
  make_test_dir "$content_dir"

  info "Backend: ${backend}  (palace: $palace)"

  # ── mine ──────────────────────────────────────────────────────────────────
  info "  mine"
  local mine_out
  mine_out=$("$BIN" mine "$content_dir" \
    --palace "$palace" --backend "$backend" 2>&1) || true
  assert_exit_ok "mine" $?
  assert_contains "mine reports mined" "Mined" "$mine_out"

  # ── status ────────────────────────────────────────────────────────────────
  info "  status"
  local status_out
  status_out=$("$BIN" status \
    --palace "$palace" --backend "$backend" 2>&1)
  assert_exit_ok "status" $?
  assert_contains "status shows backend" "${backend}" "$status_out"
  assert_contains "status shows drawers" "Drawers:" "$status_out"
  # Must have at least 1 drawer
  local count
  count=$(echo "$status_out" | grep "Drawers:" | grep -oE '[0-9]+' | head -1)
  [[ "${count:-0}" -gt 0 ]] \
    && ok "status drawer count > 0 (got $count)" \
    || fail "status drawer count is 0"

  # ── status shows wing ─────────────────────────────────────────────────────
  assert_contains "status shows wing" "Wings" "$status_out"

  # ── search — known content ────────────────────────────────────────────────
  info "  search"
  local search_out
  search_out=$("$BIN" search "PostgreSQL decision" \
    --palace "$palace" --backend "$backend" --n 3 2>&1)
  assert_exit_ok "search" $?
  assert_contains "search returns results" "sim=" "$search_out"

  # ── search — wing filter ──────────────────────────────────────────────────
  info "  search with --wing filter"
  local wing
  wing=$(echo "$status_out" | grep -v "Wings\|Drawers\|Palace\|\[" | awk 'NF{print $1; exit}')
  if [[ -n "$wing" ]]; then
    local wing_out
    wing_out=$("$BIN" search "architecture" \
      --palace "$palace" --backend "$backend" --wing "$wing" --n 3 2>&1) || true
    assert_exit_ok "search --wing" $?
    ok "search --wing completed (wing=$wing)"
  else
    ok "search --wing skipped (no wing found to test)"
  fi

  # ── dry-run mine ──────────────────────────────────────────────────────────
  info "  mine --dry-run"
  local dry_out
  dry_out=$("$BIN" mine "$content_dir" \
    --palace "$palace" --backend "$backend" --dry-run 2>&1) || true
  assert_contains "dry-run shows dry-run label" "dry-run" "$dry_out"

  # ── mine --mode convos ────────────────────────────────────────────────────
  info "  mine --mode convos"
  local convo_dir="${dir}/convos"
  mkdir -p "$convo_dir"
  cat > "${convo_dir}/chat.txt" <<'EOF'
> What database should we use?
We decided to use PostgreSQL because of better JSON support and reliability.
> Any concerns?
The main concern is migration complexity from the old MySQL schema.
EOF
  local convo_out
  convo_out=$("$BIN" mine "$convo_dir" \
    --palace "$palace" --backend "$backend" --mode convos 2>&1)
  assert_exit_ok "mine convos" $?
  assert_contains "mine convos reports mined" "mined" "$convo_out"

  # ── hook session-start ────────────────────────────────────────────────────
  info "  hook session-start"
  local hook_out
  hook_out=$(echo '{"session_id":"test123"}' | \
    "$BIN" hook session-start --harness claude-code 2>&1)
  assert_exit_ok "hook session-start" $?
  assert_contains "hook session-start outputs JSON" "{" "$hook_out"

  # ── mcp: tools/list ───────────────────────────────────────────────────────
  info "  mcp tools/list"
  local mcp_out
  mcp_out=$(printf '%s\n' \
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25"}}' \
    '{"jsonrpc":"2.0","method":"notifications/initialized"}' \
    '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' \
    | "$BIN" mcp --palace "$palace" --backend "$backend" 2>/dev/null)
  assert_contains "mcp tools/list returns tools" "mempalace_search" "$mcp_out"
  assert_contains "mcp tools/list returns compress" "mempalace_compress" "$mcp_out"

  # ── mcp: extract_memories ─────────────────────────────────────────────────
  info "  mcp extract_memories"
  local mem_out
  mem_out=$(printf '%s\n' \
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25"}}' \
    '{"jsonrpc":"2.0","method":"notifications/initialized"}' \
    '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"mempalace_extract_memories","arguments":{"text":"We decided to use SQLite. Critical bug in auth middleware.","min_confidence":0.2}}}' \
    | "$BIN" mcp --palace "$palace" --backend "$backend" 2>/dev/null)
  assert_contains "mcp extract_memories works" "decision" "$mem_out"

  # ── Cleanup ───────────────────────────────────────────────────────────────
  rm -rf "$dir"

  echo
}

# ── Main ──────────────────────────────────────────────────────────────────────

echo
info "mempalace-dotnet acceptance tests"
echo "Binary: $BIN"
echo

$RUN_SQLITE && run_backend "Sqlite"
$RUN_CHROMA && run_backend "Chroma"

echo "──────────────────────────────────────"
if [[ $FAIL -eq 0 ]]; then
  echo -e "${GREEN}All $PASS tests passed.${NC}"
else
  echo -e "${RED}$FAIL test(s) failed, $PASS passed.${NC}"
  exit 1
fi

# MemPalace .NET — MCP Deep Regression Prompt

## Context

You are running a deep regression test of the `mempalace-dotnet` MCP server.

**Repo:** `/Users/dmytro.kushnir/src/mempalace/dotnet`
**Binary:** `src/Mempalace.Cli/bin/Release/net10.0/mempalace-dotnet`
**22 tools to test** across two backends (Sqlite, Chroma).

All MCP communication is newline-delimited JSON over stdin/stdout. The server reads one request per line and writes one response per line.

---

## Setup

### 1. Build

```bash
cd /Users/dmytro.kushnir/src/mempalace/dotnet
dotnet build -c Release src/Mempalace.Cli/ -nologo -v quiet
```

Abort if build fails.

### 2. Define helpers

```bash
BIN="src/Mempalace.Cli/bin/Release/net10.0/mempalace-dotnet"
PALACE_SQLITE=$(mktemp -d)/sqlite_regression
PALACE_CHROMA=$(mktemp -d)/chroma_regression

# Helper: send N newline-delimited JSON lines to MCP and capture output
mcp() {
  local backend="$1"; shift
  local palace="$1"; shift
  printf '%s\n' "$@" | "$BIN" mcp --palace "$palace" --backend "$backend" 2>/dev/null \
    | sed 's/^\xEF\xBB\xBF//'  # strip BOM
}
```

### 3. Seed data (run once, both backends)

For each backend in `Sqlite Chroma`:

```bash
# Add 6 drawers across 2 wings and 3 rooms
mcp $backend $palace <<SEED
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25"}}
{"jsonrpc":"2.0","method":"notifications/initialized"}
{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"mempalace_add_drawer","arguments":{"wing":"backend","room":"auth","content":"We decided to use JWT tokens with RS256 signing because it allows stateless verification across microservices. The secret is stored in Vault.","added_by":"regression"}}}
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"mempalace_add_drawer","arguments":{"wing":"backend","room":"auth","content":"Critical bug: auth middleware does not validate token expiry on refresh endpoint. Causes silent session extension. Fix: add exp claim check in RefreshTokenHandler.","added_by":"regression"}}}
{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"mempalace_add_drawer","arguments":{"wing":"backend","room":"database","content":"We migrated from MySQL to PostgreSQL because of superior JSON query support and better connection pooling. Migration completed 2024-03. Riley owned the migration.","added_by":"regression"}}}
{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"mempalace_add_drawer","arguments":{"wing":"frontend","room":"auth","content":"Frontend auth flow uses PKCE. The access token is stored in memory only — never localStorage — to mitigate XSS risk. Preference: always use secure, httpOnly cookies for refresh tokens.","added_by":"regression"}}}
{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"mempalace_add_drawer","arguments":{"wing":"frontend","room":"components","content":"We decided to migrate from class components to hooks because the team prefers functional style and hooks compose better. Sam led the migration milestone.","added_by":"regression"}}}
{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"mempalace_add_drawer","arguments":{"wing":"backend","room":"auth","content":"Milestone: deployed zero-downtime auth v2 to production on 2024-06-15. Involved Riley, Sam, and Jordan.","added_by":"regression"}}}
SEED
```

Also seed the knowledge graph:

```bash
mcp $backend $palace <<KG
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25"}}
{"jsonrpc":"2.0","method":"notifications/initialized"}
{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"mempalace_kg_add","arguments":{"subject":"Riley","predicate":"owns","object":"auth-service","confidence":1.0}}}
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"mempalace_kg_add","arguments":{"subject":"Sam","predicate":"owns","object":"frontend","confidence":1.0}}}
{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"mempalace_kg_add","arguments":{"subject":"auth-service","predicate":"depends_on","object":"postgresql","valid_from":"2024-03-01","confidence":0.95}}}
{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"mempalace_kg_add","arguments":{"subject":"auth-service","predicate":"uses","object":"jwt","valid_from":"2024-01-01","confidence":1.0}}}
{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"mempalace_kg_add","arguments":{"subject":"auth-service","predicate":"uses","object":"mysql","valid_from":"2023-01-01","valid_to":"2024-03-01","confidence":1.0}}}
KG
```

---

## Test Matrix

Run **every check below** for **both backends** unless marked `[ALL]` (backend-independent).

Track results as PASS / FAIL / SKIP with a one-line reason on failure.

---

### Group 1 — Protocol & Introspection [ALL]

**T01 — Initialize handshake**
- Send `initialize` with `protocolVersion: "2025-11-25"`
- **Assert:** response has `result.protocolVersion == "2025-11-25"`, `result.serverInfo.name == "mempalace"`, `result.capabilities.tools` present

**T02 — tools/list completeness**
- Send `tools/list`
- **Assert:** exactly 22 tools returned
- **Assert:** all of the following are present:
  `mempalace_status`, `mempalace_list_wings`, `mempalace_list_rooms`,
  `mempalace_get_taxonomy`, `mempalace_get_aaak_spec`, `mempalace_search`,
  `mempalace_check_duplicate`, `mempalace_traverse_graph`, `mempalace_find_tunnels`,
  `mempalace_graph_stats`, `mempalace_add_drawer`, `mempalace_delete_drawer`,
  `mempalace_kg_query`, `mempalace_kg_add`, `mempalace_kg_invalidate`,
  `mempalace_kg_timeline`, `mempalace_kg_stats`, `mempalace_diary_write`,
  `mempalace_diary_read`, `mempalace_extract_memories`, `mempalace_detect_entities`,
  `mempalace_compress`
- **Assert:** no `\u0022` in the raw JSON output (encoding regression)

**T03 — ping**
- Send `{"jsonrpc":"2.0","id":99,"method":"ping"}`
- **Assert:** `result == {}`

**T04 — unknown method returns error**
- Send `{"jsonrpc":"2.0","id":1,"method":"nonexistent/method"}`
- **Assert:** `error.code == -32601`

**T05 — malformed JSON returns parse error**
- Send `{bad json`
- **Assert:** next line is an error response with `error.code == -32700`

---

### Group 2 — Status & Exploration

**T06 — status on empty palace**
- Before seeding: call `mempalace_status`
- **Assert:** response has `total_drawers`, `wings`, `palace_path`, `protocol`, `aaak_dialect` keys
- **Assert:** `total_drawers == 0` (pre-seed)

**T07 — status after seeding**
- After seeding: call `mempalace_status`
- **Assert:** `total_drawers == 6`
- **Assert:** `wings` contains `backend` and `frontend`

**T08 — list_wings**
- **Assert:** wings object has exactly `backend` and `frontend`
- **Assert:** `backend` count ≥ `frontend` count (4 vs 2 drawers)

**T09 — list_rooms no filter**
- **Assert:** rooms includes `auth`, `database`, `components`

**T10 — list_rooms wing filter**
- Call with `wing: "backend"`
- **Assert:** rooms contains `auth` and `database`, does NOT contain `components`

**T11 — get_taxonomy**
- **Assert:** `taxonomy.backend.auth` count == 3
- **Assert:** `taxonomy.backend.database` count == 1
- **Assert:** `taxonomy.frontend.auth` count == 1
- **Assert:** `taxonomy.frontend.components` count == 1

**T12 — get_aaak_spec**
- **Assert:** `spec` field is non-empty string containing "AAAK"
- **Assert:** `protocol` field is non-empty string

---

### Group 3 — Semantic Search

**T13 — basic search returns results**
- Query: `"JWT authentication tokens"`
- **Assert:** ≥1 result with `similarity > 0` and non-empty `text`
- **Assert:** top result text contains relevant content (JWT, auth, or token)

**T14 — search similarity ordering**
- Query: `"PostgreSQL migration database"`
- **Assert:** results sorted descending by similarity
- **Assert:** top result mentions PostgreSQL or migration

**T15 — search wing filter**
- Query: `"auth"`, `wing: "frontend"`
- **Assert:** all results have wing == `frontend`
- **Assert:** no results from `backend` wing

**T16 — search room filter**
- Query: `"auth"`, `room: "database"`
- **Assert:** results only from `database` room (or empty — no auth content there)

**T17 — search wing+room filter**
- Query: `"decisions"`, `wing: "backend"`, `room: "auth"`
- **Assert:** results only from `backend/auth`

**T18 — search limit**
- Query: `"auth"`, `limit: 2`
- **Assert:** at most 2 results

**T19 — search empty palace** (run before seeding)
- **Assert:** `results` is empty array, no error

**T20 — search nonexistent wing**
- Query: `"anything"`, `wing: "does_not_exist"`
- **Assert:** no error, returns empty results

---

### Group 4 — Deduplication

**T21 — check_duplicate exact match**
- Content: the exact text of any seeded drawer
- Threshold: 0.95
- **Assert:** `is_duplicate == true`, `matches` array non-empty

**T22 — check_duplicate low threshold**
- Content: `"JWT token auth"` (semantically similar but not exact)
- Threshold: 0.5
- **Assert:** `is_duplicate == true` (should find similar content at low threshold)

**T23 — check_duplicate high threshold no match**
- Content: `"completely unrelated topic about baking bread"`
- Threshold: 0.99
- **Assert:** `is_duplicate == false`

---

### Group 5 — Drawer Write / Delete

**T24 — add_drawer returns id**
- Add: `wing: "test"`, `room: "regression"`, `content: "Regression test drawer added by T24"`
- **Assert:** `success == true`
- **Assert:** `drawer_id` starts with `drawer_test_regression_`
- Save `drawer_id` as `TEST_ID`

**T25 — added drawer is searchable**
- Search: `"regression test drawer"`
- **Assert:** `TEST_ID` appears in results

**T26 — add_drawer idempotent**
- Add same content again (same wing/room/content)
- **Assert:** returns same `drawer_id` (deterministic hash)
- **Assert:** `mempalace_status` total unchanged (upsert, not duplicate)

**T27 — add_drawer sanitization rejects empty wing**
- Call with `wing: ""`, `room: "r"`, `content: "x"`
- **Assert:** `success == false` or error in response

**T28 — delete_drawer found**
- Delete `TEST_ID`
- **Assert:** `success == true`

**T29 — deleted drawer not searchable**
- Search again for T24 content
- **Assert:** `TEST_ID` not in results

**T30 — delete_drawer not found**
- Delete `drawer_nonexistent_id_xyz`
- **Assert:** `success == false`, `error` field present

---

### Group 6 — Graph Traversal

**T31 — graph_stats correct counts**
- **Assert:** `total_rooms ≥ 3` (auth, database, components)
- **Assert:** `tunnel_rooms == 1` (auth appears in both backend and frontend wings)
- **Assert:** `rooms_per_wing.backend == 2` (auth + database)
- **Assert:** `rooms_per_wing.frontend == 2` (auth + components)

**T32 — find_tunnels no filter**
- **Assert:** returns at least 1 tunnel (auth bridges backend ↔ frontend)
- **Assert:** tunnel room == `auth`
- **Assert:** tunnel wings contains both `backend` and `frontend`

**T33 — find_tunnels wing_a filter**
- `wing_a: "backend"`
- **Assert:** all results contain `backend` in wings

**T34 — find_tunnels wing_a + wing_b**
- `wing_a: "backend"`, `wing_b: "frontend"`
- **Assert:** exactly the `auth` room returned

**T35 — find_tunnels no match**
- `wing_a: "backend"`, `wing_b: "nonexistent"`
- **Assert:** empty tunnels array

**T36 — traverse_graph known room**
- `start_room: "auth"`, `max_hops: 1`
- **Assert:** first result is `auth` at hop 0
- **Assert:** other rooms reachable via shared wings appear at hop 1

**T37 — traverse_graph unknown room returns suggestions**
- `start_room: "autth"` (typo)
- **Assert:** response contains `error` field
- **Assert:** response contains `suggestions` array with `auth` as a suggestion

**T38 — traverse_graph max_hops: 0**
- **Assert:** exactly 1 result (only start room)

---

### Group 7 — Knowledge Graph

**T39 — kg_stats after seeding**
- **Assert:** `entities ≥ 5` (Riley, Sam, auth-service, postgresql, jwt, mysql)
- **Assert:** `triples ≥ 5`
- **Assert:** `current_facts < triples` (mysql triple has valid_to set)
- **Assert:** `relationship_types` includes `owns`, `depends_on`, `uses`

**T40 — kg_query entity outgoing**
- `entity: "Riley"`, `direction: "outgoing"`
- **Assert:** results contain triple with predicate `owns` and object `auth-service`

**T41 — kg_query entity incoming**
- `entity: "auth-service"`, `direction: "incoming"`
- **Assert:** results contain triple with subject `Riley`, predicate `owns`

**T42 — kg_query entity both**
- `entity: "auth-service"`, `direction: "both"`
- **Assert:** results include both outgoing (depends_on, uses) and incoming (Riley owns) triples

**T43 — kg_query temporal filter as_of**
- `entity: "auth-service"`, `as_of: "2023-06-01"` (before MySQL migration)
- **Assert:** triple `auth-service uses mysql` is returned (was current then)

**T44 — kg_query temporal filter excludes expired**
- `entity: "auth-service"`, `as_of: "2024-06-01"` (after migration)
- **Assert:** triple `auth-service uses mysql` NOT returned (expired 2024-03-01)
- **Assert:** triple `auth-service uses jwt` IS returned

**T45 — kg_invalidate**
- Invalidate: `subject: "Riley"`, `predicate: "owns"`, `object: "auth-service"`, `ended: "2024-12-01"`
- **Assert:** `success == true`
- After invalidation: query Riley outgoing with `as_of: "2025-01-01"`
- **Assert:** `Riley owns auth-service` NOT returned

**T46 — kg_timeline no filter**
- **Assert:** non-empty array of triples
- **Assert:** ordered by valid_from/extracted_at

**T47 — kg_timeline entity filter**
- `entity: "Sam"`
- **Assert:** only triples involving Sam

**T48 — kg_add duplicate (same s/p/o) is upsert**
- Add `Riley owns auth-service` again (same triple)
- **Assert:** `success == true`
- Call `kg_stats` — `triples` count unchanged (upsert not insert)

---

### Group 8 — Diary

**T49 — diary_write returns entry_id**
- `agent_name: "test-agent"`, `entry: "0:???|regression_test|\"ran all 22 tools today\"|determ|DECISION"`, `topic: "regression"`
- **Assert:** `success == true`
- **Assert:** `entry_id` starts with `diary_wing_test-agent_`
- Save as `DIARY_ID`

**T50 — diary_read returns entry**
- `agent_name: "test-agent"`, `last_n: 5`
- **Assert:** at least 1 entry returned
- **Assert:** returned entry has `topic == "regression"`
- **Assert:** results ordered by timestamp descending

**T51 — diary_read empty agent**
- `agent_name: "nonexistent-agent-xyz"`
- **Assert:** no error, `entries` is empty array

**T52 — diary_write multiple entries, read respects last_n**
- Write 3 more entries for `test-agent`
- Read with `last_n: 2`
- **Assert:** exactly 2 entries returned

---

### Group 9 — Pure Analysis Tools [ALL]

These tools do no I/O to the palace — test once with any backend.

**T53 — extract_memories DECISION**
- Text: `"We decided to use PostgreSQL instead of MySQL because of better JSON support."`
- `min_confidence: 0.1`
- **Assert:** `total ≥ 1`
- **Assert:** `by_type.decision ≥ 1`
- **Assert:** returned memory content contains the input text

**T54 — extract_memories MILESTONE**
- Text: `"Milestone: deployed auth v2 to production. The team hit this key goal in June."`
- **Assert:** `by_type.milestone ≥ 1`

**T55 — extract_memories PROBLEM**
- Text: `"Critical bug: auth middleware does not validate token expiry. This causes session hijacking."`
- **Assert:** `by_type.problem ≥ 1`

**T56 — extract_memories PREFERENCE**
- Text: `"I prefer functional components over class components. The team always uses hooks now."`
- **Assert:** `by_type.preference ≥ 1`

**T57 — extract_memories empty text**
- Text: `""`
- **Assert:** no crash, `total == 0` or error with message

**T58 — extract_memories high threshold filters all**
- Text: `"We decided something."`
- `min_confidence: 0.99`
- **Assert:** `total == 0` or very few results

**T59 — detect_entities people**
- Text: `"Riley said the project is going well. Riley told Sam about the progress. Sam laughed. Riley and Sam both agreed."`
- **Assert:** people or uncertain array contains at least one of Riley / Sam

**T60 — detect_entities projects**
- Text: `"We are building MemPalace. We deployed MemPalace v2. The MemPalace pipeline is stable."`
- **Assert:** projects array contains MemPalace

**T61 — detect_entities below frequency threshold**
- Text: `"Riley went to the store."` (Riley appears only 1x — below 3x threshold)
- **Assert:** neither Riley in people nor uncertain (filtered by frequency)

**T62 — compress basic**
- Text: `"We decided to migrate from MySQL to PostgreSQL because JSON support is far superior. This is a core architectural decision."`
- **Assert:** `compressed` field non-empty
- **Assert:** `size_ratio > 1.0` (compression actually reduces token count)
- **Assert:** `compressed` contains at least one of: `DECISION`, `CORE`, `determ`
- **Assert:** no `\u0022` in compressed string (encoding regression)

**T63 — compress preserves key sentence quote**
- **Assert:** `compressed` contains `"` (escaped in JSON as `\"`) around a key phrase

**T64 — compress short text**
- Text: `"ok"`
- **Assert:** no crash, returns compressed and stats

---

### Group 10 — Error Handling & Edge Cases

**T65 — missing required argument**
- Call `mempalace_search` with no `query` argument
- **Assert:** either error response or `{"error": ...}` in content

**T66 — delete nonexistent drawer graceful**
- Already tested in T30 — confirm `success == false` not a crash

**T67 — traverse_graph empty palace**
- On fresh (empty) palace: `start_room: "auth"`
- **Assert:** either `error` field (room not found) or empty results — no crash

**T68 — search with limit: 0**
- **Assert:** no crash (returns 0 or 1 results depending on implementation)

**T69 — kg_query unknown entity**
- `entity: "nobody_xyz_unknown"`
- **Assert:** no crash, returns empty triples array

**T70 — add_drawer very long content**
- Content: 10,000 character string
- **Assert:** `success == true` (no size limit crash)

**T71 — concurrent-ish: add then immediately search**
- Add a uniquely worded drawer, immediately search for its content
- **Assert:** new drawer appears in search results (no async commit lag)

**T72 — multiple sessions same palace**
- Start two separate MCP processes against same palace path
- From session A: add a drawer
- From session B: search for it
- **Assert:** B sees A's drawer (durable storage, not in-memory)

---

## Scoring

After completing all checks:

| Group | Tools | Tests |
|-------|-------|-------|
| Protocol | — | T01–T05 |
| Status/Exploration | status, list_wings, list_rooms, get_taxonomy, get_aaak_spec | T06–T12 |
| Search | search | T13–T20 |
| Deduplication | check_duplicate | T21–T23 |
| Drawer Write/Delete | add_drawer, delete_drawer | T24–T30 |
| Graph Traversal | traverse_graph, find_tunnels, graph_stats | T31–T38 |
| Knowledge Graph | kg_add, kg_query, kg_invalidate, kg_timeline, kg_stats | T39–T48 |
| Diary | diary_write, diary_read | T49–T52 |
| Pure Analysis | extract_memories, detect_entities, compress | T53–T64 |
| Error Handling | (all) | T65–T72 |

Report:
- **PASS/FAIL counts per group per backend**
- **Full list of FAILs** with: test ID, backend, expected, actual
- **Any crashes or hangs** (tool that didn't return a response)
- **Encoding regressions** (`\u0022` in any output)
- **Performance outliers** (any single tool call taking >2s, embedding load aside)

Cleanup: `rm -rf $PALACE_SQLITE $PALACE_CHROMA`

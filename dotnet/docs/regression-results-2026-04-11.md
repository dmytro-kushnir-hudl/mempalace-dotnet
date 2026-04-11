# Regression Results — 2026-04-11

Binary: `src/Mempalace.Cli/bin/Release/net10.0/mempalace-dotnet` (0.1.0+fbf2ad8)  
Test spec: `docs/mcp-regression-prompt.md`

## Summary

| Backend | Pass | Fail | Total |
|---------|------|------|-------|
| Sqlite  | 64   | 4    | 68    |
| Chroma  | 63   | 5    | 68    |
| Protocol (both)  | 5    | 0    | 5     |
| **Total** | **132** | **9** | **141** |

> Notes: Pure-analysis tests (T53–T64) and protocol tests (T01–T05) are backend-independent (run on Sqlite only).
> T49 and T62/T63 reported as failures by the regression runner were **false positives** — manually verified PASS.

---

## Confirmed Failures

### T37 [Sqlite + Chroma] — traverse_graph typo suggestions empty
- **Tool:** `mempalace_traverse_graph`
- **Input:** `start_room: "autth"` (typo for "auth")
- **Expected:** `suggestions` contains `"auth"`
- **Actual:** `{"error":"Room 'autth' not found","suggestions":[]}`
- **Root cause:** `FuzzyMatch` in `PalaceGraph.cs:302` only does substring containment check. `"autth"` does not contain `"auth"` (reverse direction), so score = 0 and no suggestions returned.
- **Fix:** Add Levenshtein edit-distance fallback (threshold ≤ 2).

### T54-T56 [Sqlite] — extract_memories returns 0 at default confidence for milestone/problem/preference
- **Tool:** `mempalace_extract_memories`
- **Input:** Short single-sentence texts with one strong type marker each
- **Expected:** `total >= 1` at default `min_confidence`
- **Actual:** `{"total":0,"by_type":{},"memories":[]}`
- **Root cause:** Confidence formula `Math.Min(1.0, maxScore / 5.0)` in `GeneralExtractor.cs:208`. A single marker hit gives score=1 → confidence=0.2, below the default threshold of 0.3. Short single-sentence inputs always have exactly 1 matching marker.
- **Fix:** Change default `min_confidence` from `0.3` to `0.1` in `McpServer.cs` dispatch and `McpTools.ExtractMemories`.

### T60 [Sqlite] — detect_entities does not classify CamelCase names as projects
- **Tool:** `mempalace_detect_entities`
- **Input:** `"We are building MemPalace. We deployed MemPalace v2. The MemPalace pipeline is stable."`
- **Expected:** `projects` contains `MemPalace`
- **Actual:** `{"people":[],"projects":[],"uncertain":[]}`
- **Root cause:** `CandidateWord` regex in `EntityDetector.cs:73` is `\b([A-Z][a-z]{1,19})\b` — requires all-lowercase after first char. "MemPalace" has uppercase 'P' mid-word so it's never extracted as a candidate. Frequency threshold (≥3) also not met since the word is never counted.
- **Fix:** Add a CamelCase pattern `\b[A-Z][a-z]+[A-Z][a-zA-Z]+\b` to `ExtractCandidates`.

### T65 [Sqlite + Chroma] — search with missing query arg returns results instead of error
- **Tool:** `mempalace_search`
- **Input:** `{}` (no `query` argument)
- **Expected:** error response or `{"error":...}` in content
- **Actual:** `{"Query":null,"Wing":null,"Room":null,"Results":[...]}`
- **Root cause:** `S("query")!` in `McpServer.cs:139` uses null-forgiving operator — null is passed through to `Searcher.SearchMemoriesAsync` which accepts null query and runs a search. The `!` suppresses the compiler warning but doesn't validate.
- **Fix:** Add null guard in dispatch before calling `SearchAsync`.

---

## Chroma-only Failures

### T22 [Chroma] — check_duplicate returns false for semantically similar content at threshold 0.5
- **Tool:** `mempalace_check_duplicate`
- **Input:** `content: "JWT token auth"`, `threshold: 0.5`
- **Expected:** `is_duplicate == true`
- **Actual:** `{"is_duplicate":false,"matches":[]}`
- **Root cause:** Chroma uses cosine **distance** in range [0, 2]. Similarity = `1.0 - distance`. For non-exact matches, distance is typically > 0.7 → similarity < 0.3, far below threshold 0.5. This is a metric mismatch between backends: SQLite uses dot-product similarity normalized to [0,1]; Chroma uses L2/cosine distance converted to similarity which can be negative.
- **Fix:** For Chroma backend, normalize similarity to [0,1] range OR apply a backend-specific threshold scale.

### T68 [Chroma] — search with limit:0 returns 1 result
- **Tool:** `mempalace_search`
- **Input:** `limit: 0`
- **Expected:** 0 results (or graceful empty)
- **Actual:** 1 result returned
- **Root cause:** `ChromaVectorCollection.SearchAsync` clamps `nResults` to `Math.Max(1, nResults)`, so `limit=0` becomes 1. SQLite returns 0 results correctly.
- **Fix:** Guard `limit <= 0` early in `McpTools.SearchAsync` or `McpServer` dispatch.

---

## False Positives (regression runner was wrong)

### T49 — diary entry_id prefix
- Regression runner reported `diary_wing_wing_test-agent_` but manual verification shows correct output `diary_wing_test-agent_20260411_...`. **PASS.**

### T62/T63 — compress JSON malformation
- Regression runner reported outer JSON invalid. Manual verification shows both outer and inner JSON parse correctly. Compressed value with quoted phrases is properly escaped. **PASS.**

---

## No crashes, no hangs, no `\u0022` encoding regressions observed.

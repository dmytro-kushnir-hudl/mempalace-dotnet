# Task: Pluggable Vector Store Abstraction for Mempalace .NET Port

## Context

You are working on `/Users/dmytro.kushnir/src/mempalace/dotnet` — a .NET 10 port of the
Python AI memory manager "mempalace". The codebase currently uses ChromaDB via a custom
Rust C FFI client (`Chroma.Client`) as its vector store. The goal is to introduce a thin
abstraction so ChromaDB can be swapped for sqlite-vec (SQLite + vector extension) without
touching any business logic.

## What ChromaDB currently does in the codebase (the full surface)

ChromaDB is accessed exclusively through `PalaceSession` in `Palace.cs`. All call sites
use `session.Collection` which is a `NativeCollection` (the Chroma C# type). The exact
operations used across the codebase are:

```
// Upsert — stores text chunk + pre-computed embedding + metadata dict
session.Collection.UpsertTextsAsync(ids, documents, embedder, metadatas, ct)

// Semantic search — embed query, find nearest neighbours, filter by metadata
session.Collection.QueryByTextAsync(queryTexts, embedder, nResults, where, include, ct)
// → returns QueryResult { Ids: string[][], Documents: string?[][], Metadatas: Dictionary[][], Distances: double?[][] }

// Metadata-filtered fetch (no vector, paginated)
session.Collection.Get(where, limit, offset, include)
// → returns GetResult { Ids: string[], Documents: string?[], Metadatas: Dictionary[]? }

// Fetch by IDs
session.Collection.Get(ids: string[], include)

// Count
session.Collection.Count()

// Delete by IDs
session.Collection.Delete(ids)

// Delete by where filter
session.Collection.Delete(where)
```

Metadata values are `Dictionary<string, object?>` where values are `bool`, `long`,
`double`, or `string`. The `where` filter is a `JsonNode?` using ChromaDB's filter DSL:
`{"wing": "tech"}` or `{"$and": [{"wing": "tech"}, {"room": "backend"}]}`.

Files that call `session.Collection` directly:
- `Miner.cs` — `UpsertTextsAsync` (AddDrawerAsync)
- `ConvoMiner.cs` — `UpsertTextsAsync`
- `Searcher.cs` — `QueryByTextAsync`
- `Layers.cs` — `Get` (paginated, for Layer1 + Layer2)
- `McpTools.cs` — `Get`, `QueryByTextAsync`, `UpsertTextsAsync`, `Delete`, `Count`
- `PalaceGraph.cs` — `Get` (reads all metadatas for graph traversal)

## The abstraction to build

### Step 1 — Define `IVectorCollection` interface

Create `src/Mempalace/Storage/IVectorCollection.cs`:

```csharp
using Microsoft.Extensions.AI;

namespace Mempalace.Storage;

/// <summary>
///     Backend-agnostic vector store collection.
///     One collection = one "palace" (all wings/rooms together).
/// </summary>
public interface IVectorCollection : IDisposable
{
    // ── Write ──────────────────────────────────────────────────────────────────

    /// <summary>Embed <paramref name="documents"/> and upsert with metadata.</summary>
    Task UpsertAsync(
        string[] ids,
        string[] documents,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        Dictionary<string, object?>[]? metadatas = null,
        CancellationToken ct = default);

    /// <summary>Delete records by IDs.</summary>
    void Delete(string[] ids);

    /// <summary>Delete records matching a metadata filter.</summary>
    void Delete(MetadataFilter filter);

    // ── Read ───────────────────────────────────────────────────────────────────

    /// <summary>Semantic search: embed query, return top-N nearest neighbours.</summary>
    Task<VectorSearchResult[]> SearchAsync(
        string query,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        int nResults = 5,
        MetadataFilter? filter = null,
        CancellationToken ct = default);

    /// <summary>Fetch records by metadata filter (no vector, paginated).</summary>
    VectorRecord[] Get(
        MetadataFilter? filter = null,
        string[]? ids = null,
        int limit = 100,
        int offset = 0,
        bool includeDocuments = true,
        bool includeMetadatas = true);

    int Count();
}

public sealed record VectorRecord(
    string Id,
    string? Document,
    Dictionary<string, object?>? Metadata);

public sealed record VectorSearchResult(
    string Id,
    string? Document,
    Dictionary<string, object?>? Metadata,
    double Similarity);  // 0..1, higher = more similar
```

### Step 2 — Define `MetadataFilter`

Create `src/Mempalace/Storage/MetadataFilter.cs`:

```csharp
namespace Mempalace.Storage;

/// <summary>Simple metadata filter — equality only, AND conjunction.</summary>
public sealed class MetadataFilter
{
    private readonly Dictionary<string, object?> _clauses = new();

    public static MetadataFilter Where(string key, object? value)
    {
        var f = new MetadataFilter();
        f._clauses[key] = value;
        return f;
    }

    public MetadataFilter And(string key, object? value)
    {
        _clauses[key] = value;
        return this;
    }

    public IReadOnlyDictionary<string, object?> Clauses => _clauses;
}
```

### Step 3 — ChromaDB adapter

Create `src/Mempalace/Storage/ChromaVectorCollection.cs` — wraps the existing
`NativeCollection` to implement `IVectorCollection`. This is a mechanical translation of
the current `session.Collection.*` calls.

Key mappings:
- `UpsertAsync` → `collection.UpsertTextsAsync`
- `SearchAsync` → `collection.QueryByTextAsync` → map `Distances[0][i]` to `Similarity = 1 - distance`
- `Get(filter)` → `collection.Get(where: FilterToJsonNode(filter), ...)`
- `Delete(filter)` → `collection.Delete(where: FilterToJsonNode(filter))`
- `FilterToJsonNode`: single clause → `{"key": value}`, multiple → `{"$and": [...]}`

### Step 4 — sqlite-vec adapter

Create `src/Mempalace/Storage/SqliteVectorCollection.cs`.

Add NuGet dependency: `sqlite-vec` (the .NET binding, or load the native extension manually).

**Schema** (created on first open):

```sql
CREATE TABLE IF NOT EXISTS drawers (
    id          TEXT PRIMARY KEY,
    wing        TEXT,
    room        TEXT,
    source_file TEXT,
    chunk_index INTEGER,
    added_by    TEXT,
    filed_at    TEXT,
    source_mtime REAL,
    document    TEXT,
    embedding   BLOB         -- raw float32 LE, emb_dim × 4 bytes
);
CREATE INDEX IF NOT EXISTS idx_drawers_wing ON drawers(wing);
CREATE INDEX IF NOT EXISTS idx_drawers_room ON drawers(room);
CREATE INDEX IF NOT EXISTS idx_drawers_source_file ON drawers(source_file);

-- sqlite-vec virtual table for ANN search
CREATE VIRTUAL TABLE IF NOT EXISTS drawers_vec USING vec0(
    id          TEXT PRIMARY KEY,
    embedding   float[384]
);
```

**Key implementation details:**

`UpsertAsync`:
1. Embed documents via `embedder.EmbedAsync(documents, ct)`
2. For each record: `INSERT OR REPLACE INTO drawers(...)` + `INSERT OR REPLACE INTO drawers_vec(id, embedding)`
3. Embedding stored as `float[]` → `byte[]` via `MemoryMarshal.Cast<float, byte>(...).ToArray()`

`SearchAsync`:
1. Embed query
2. `SELECT d.id, d.document, d.wing, d.room, d.source_file, v.distance FROM drawers_vec v JOIN drawers d ON d.id = v.id WHERE v.embedding MATCH ? AND k = ? [AND wing = ? AND room = ?] ORDER BY v.distance`
3. `distance` from sqlite-vec is already cosine distance (0..2 range) — convert to similarity: `1 - distance/2`
4. Pass embedding as raw bytes parameter

`Get(filter)`:
- Pure SQL: `SELECT id, document, wing, room, ... FROM drawers WHERE wing = ? [AND room = ?] LIMIT ? OFFSET ?`

`MetadataFilter` → SQL: map each clause to a `WHERE key = ?` fragment. Only `wing`, `room`, `source_file` are indexed columns; other metadata keys would need a JSON column or be skipped.

### Step 5 — Update `PalaceSession`

Replace the current implementation:

```csharp
public sealed class PalaceSession : IDisposable
{
    public IVectorCollection Collection { get; }

    private PalaceSession(IVectorCollection collection) { ... }

    public static PalaceSession Open(
        string palacePath,
        string collectionName = Constants.DefaultCollectionName,
        VectorBackend backend = VectorBackend.Chroma)  // or read from config
    {
        var collection = backend switch {
            VectorBackend.Chroma  => (IVectorCollection)new ChromaVectorCollection(palacePath, collectionName),
            VectorBackend.Sqlite  => new SqliteVectorCollection(
                Path.Combine(palacePath, "palace.sqlite3"), embeddingDim: 384),
            _ => throw new ArgumentOutOfRangeException()
        };
        return new PalaceSession(collection);
    }

    public bool FileAlreadyMined(string sourceFile, bool checkMtime = false)
    {
        // unchanged logic — uses Collection.Get(MetadataFilter.Where("source_file", sourceFile))
    }
}

public enum VectorBackend { Chroma, Sqlite }
```

### Step 6 — Update call sites

All files that call `session.Collection.*` need to be updated to the new interface:

| Old call | New call |
|---|---|
| `collection.UpsertTextsAsync(ids, docs, embedder, metas, ct)` | `await Collection.UpsertAsync(ids, docs, embedder, metas, ct)` |
| `collection.QueryByTextAsync(texts, embedder, n, where, include, ct)` | `await Collection.SearchAsync(query, embedder, n, filter, ct)` |
| `collection.Get(where, limit, offset, include)` | `Collection.Get(filter, limit: limit, offset: offset)` |
| `collection.Get(ids: [...])` | `Collection.Get(ids: [...])` |
| `collection.Count()` | `Collection.Count()` |
| `collection.Delete(ids: [...])` | `Collection.Delete(ids)` |
| `collection.Delete(where: ...)` | `Collection.Delete(filter)` |

The `where` `JsonNode?` currently built in `Searcher.BuildWhere` should be replaced with
`MetadataFilter`. The existing `BuildWhere` can be kept as a Chroma-internal helper inside
`ChromaVectorCollection`.

`PalaceGraph.cs` calls `Get` with large limits to read all metadata — translate to
`Collection.Get(limit: 10_000)`.

## What NOT to change

- `KnowledgeGraph.cs` — uses SQLite directly, unrelated to vector store
- `Dialect.cs`, `GeneralExtractor.cs`, `EntityDetector.cs` — pure logic, no storage
- `Config.cs`, `Constants.cs` — add `VectorBackend` enum here or in `PalaceSession`
- Test files — update fixtures to use `IVectorCollection` mock if needed

## Files to create

```
src/Mempalace/Storage/
  IVectorCollection.cs
  MetadataFilter.cs
  ChromaVectorCollection.cs
  SqliteVectorCollection.cs
```

## sqlite-vec NuGet / loading

The sqlite-vec .NET integration is not yet a first-class NuGet. Options:
1. **`sqlite-vec` loadable extension** — call `connection.LoadExtension("vec0")` using
   `Microsoft.Data.Sqlite` with `EnableExtensions = true`
2. **Bundle the native `.dylib`/`.so`** similar to how `libchromadb_dotnet.dylib` is bundled
3. **`SQLitePCL.raw`** with custom provider if extension loading is blocked

Check https://github.com/asg017/sqlite-vec for the current .NET integration story.
The key SQL syntax once loaded:
```sql
CREATE VIRTUAL TABLE v USING vec0(embedding float[384]);
SELECT rowid, distance FROM v WHERE embedding MATCH ? AND k = 5 ORDER BY distance;
```

## Tests to write

- `SqliteVectorCollectionTests.cs` — unit tests with an in-memory SQLite, covering
  Upsert/Search/Get/Delete/Count using `StubEmbeddingProvider` from the existing test stubs
- `PalaceSessionTests.cs` — open with `VectorBackend.Sqlite`, mine a few chunks, search
- Parametrize existing `NativeCollectionTests` patterns over both backends if feasible

## Build check

After changes:
```bash
cd /Users/dmytro.kushnir/src/mempalace/dotnet
dotnet build src/Mempalace/
dotnet test tests/Mempalace.Tests/
```

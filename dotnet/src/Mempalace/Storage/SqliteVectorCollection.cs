using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

namespace Mempalace.Storage;

// ---------------------------------------------------------------------------
// SqliteVectorCollection
//
// Stores palace drawers in SQLite.  When the sqlite-vec extension (vec0) is
// available it's used for ANN search; otherwise falls back to brute-force
// cosine similarity computed in C#.
//
// Schema:
//   drawers          — all scalar fields + document + raw embedding BLOB
//   drawers_vec      — virtual table via vec0 for fast ANN (optional)
// ---------------------------------------------------------------------------

public sealed class SqliteVectorCollection : IVectorCollection
{
    private readonly SqliteConnection _conn;
    private readonly int _embeddingDim;
    private bool _hasVec0;
    private bool _disposed;

    public SqliteVectorCollection(string dbPath, int embeddingDim = 384)
    {
        _embeddingDim = embeddingDim;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath) ?? ".");

        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode        = SqliteOpenMode.ReadWriteCreate,
        };
        _conn = new SqliteConnection(csb.ToString());
        _conn.Open();

        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=NORMAL;");
        Execute("PRAGMA cache_size=-32000;");   // 32 MB page cache
        Execute("PRAGMA temp_store=MEMORY;");
        Execute("PRAGMA foreign_keys=ON;");

        // Try to load sqlite-vec extension
        _hasVec0 = TryLoadVec0();

        CreateSchema();
    }

    // ── Schema ────────────────────────────────────────────────────────────────

    private void CreateSchema()
    {
        Execute($"""
            CREATE TABLE IF NOT EXISTS drawers (
                id            TEXT PRIMARY KEY,
                wing          TEXT,
                room          TEXT,
                source_file   TEXT,
                chunk_index   INTEGER,
                added_by      TEXT,
                filed_at      TEXT,
                source_mtime  REAL,
                document      TEXT,
                metadata_json TEXT,
                embedding     BLOB
            );
            CREATE INDEX IF NOT EXISTS idx_drawers_wing        ON drawers(wing);
            CREATE INDEX IF NOT EXISTS idx_drawers_room        ON drawers(room);
            CREATE INDEX IF NOT EXISTS idx_drawers_source_file ON drawers(source_file);
            """);

        if (_hasVec0)
        {
            try
            {
                Execute($"""
                    CREATE VIRTUAL TABLE IF NOT EXISTS drawers_vec USING vec0(
                        id        TEXT PRIMARY KEY,
                        embedding float[{_embeddingDim}]
                    );
                    """);
            }
            catch
            {
                // vec0 loaded but virtual table creation failed — disable
                _hasVec0 = false;
            }
        }
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    public async Task UpsertAsync(
        string[] ids,
        string[] documents,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        Dictionary<string, object?>[]? metadatas = null,
        CancellationToken ct = default)
    {
        var vecs = await embedder.GenerateAsync(documents, cancellationToken: ct)
            .ConfigureAwait(false);

        using var tx = _conn.BeginTransaction();

        for (int i = 0; i < ids.Length; i++)
        {
            ct.ThrowIfCancellationRequested();

            var id       = ids[i];
            var doc      = documents[i];
            var meta     = metadatas?[i];
            var emb      = vecs[i].Vector.ToArray();
            var embBytes = FloatsToBytes(emb);

            var wing        = meta?.GetValueOrDefault("wing")         as string ?? "";
            var room        = meta?.GetValueOrDefault("room")         as string ?? "";
            var sourceFile  = meta?.GetValueOrDefault("source_file")  as string ?? "";
            var chunkIndex  = meta?.GetValueOrDefault("chunk_index")  is long ci ? ci : 0L;
            var addedBy     = meta?.GetValueOrDefault("added_by")     as string ?? "";
            var filedAt     = meta?.GetValueOrDefault("filed_at")     as string ?? "";
            var sourceMtime = meta?.GetValueOrDefault("source_mtime") is double sm ? sm : 0.0;
            var metaJson    = meta is not null
                ? JsonSerializer.Serialize(meta) : null;

            ExecuteParams("""
                INSERT INTO drawers
                    (id, wing, room, source_file, chunk_index, added_by, filed_at,
                     source_mtime, document, metadata_json, embedding)
                VALUES
                    ($id,$wing,$room,$sf,$ci,$ab,$fa,$sm,$doc,$mj,$emb)
                ON CONFLICT(id) DO UPDATE SET
                    wing=excluded.wing, room=excluded.room,
                    source_file=excluded.source_file, chunk_index=excluded.chunk_index,
                    added_by=excluded.added_by, filed_at=excluded.filed_at,
                    source_mtime=excluded.source_mtime, document=excluded.document,
                    metadata_json=excluded.metadata_json, embedding=excluded.embedding;
                """,
                ("$id",   id),          ("$wing", wing),     ("$room", room),
                ("$sf",   sourceFile),  ("$ci",   chunkIndex),("$ab",  addedBy),
                ("$fa",   filedAt),     ("$sm",   sourceMtime),
                ("$doc",  doc),         ("$mj",   (object?)metaJson ?? DBNull.Value),
                ("$emb",  embBytes));

            if (_hasVec0)
                ExecuteParams("""
                    INSERT INTO drawers_vec(id, embedding)
                    VALUES ($id, $emb)
                    ON CONFLICT(id) DO UPDATE SET embedding=excluded.embedding;
                    """,
                    ("$id", id), ("$emb", embBytes));
        }

        tx.Commit();
    }

    public void Delete(string[] ids)
    {
        if (ids.Length == 0) return;
        // Parameterised IN clause via temp CTE
        using var tx = _conn.BeginTransaction();
        foreach (var id in ids)
        {
            ExecuteParams("DELETE FROM drawers     WHERE id=$id;", ("$id", id));
            if (_hasVec0)
                ExecuteParams("DELETE FROM drawers_vec WHERE id=$id;", ("$id", id));
        }
        tx.Commit();
    }

    public void Delete(MetadataFilter filter)
    {
        var (sql, parms) = BuildWhereClause(filter);

        // Collect IDs first, then delete from both tables
        var idsToDelete = new List<string>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT id FROM drawers WHERE {sql};";
            foreach (var (n, v) in parms) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read()) idsToDelete.Add(rdr.GetString(0));
        }

        Delete(idsToDelete.ToArray());
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public async Task<VectorSearchResult[]> SearchAsync(
        string query,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        int nResults = 5,
        MetadataFilter? filter = null,
        CancellationToken ct = default)
    {
        var vecs = await embedder.GenerateAsync([query], cancellationToken: ct);
        var queryEmb = vecs[0].Vector.ToArray();

        return _hasVec0
            ? SearchVec0(queryEmb, nResults, filter)
            : SearchBruteForce(queryEmb, nResults, filter);
    }

    private VectorSearchResult[] SearchVec0(float[] queryEmb, int nResults, MetadataFilter? filter)
    {
        var embBytes = FloatsToBytes(queryEmb);

        // Build JOIN query with optional metadata filter
        string whereExtra = "";
        List<(string Name, object? Value)> extraParms = [];

        if (filter is not null)
        {
            var (sql, parms) = BuildWhereClause(filter, tableAlias: "d");
            whereExtra = $" AND {sql}";
            extraParms = parms;
        }

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT d.id, d.document, d.metadata_json, v.distance
            FROM drawers_vec v
            JOIN drawers d ON d.id = v.id
            WHERE v.embedding MATCH $emb
              AND k = $k{whereExtra}
            ORDER BY v.distance;
            """;
        cmd.Parameters.AddWithValue("$emb", embBytes);
        cmd.Parameters.AddWithValue("$k",   nResults);
        foreach (var (n, v) in extraParms)
            cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);

        return ReadSearchResults(cmd);
    }

    private VectorSearchResult[] SearchBruteForce(
        float[] queryEmb, int nResults, MetadataFilter? filter)
    {
        // Load all embeddings + metadata, compute cosine similarity in C#
        string whereClause = "";
        List<(string Name, object? Value)> parms = [];

        if (filter is not null)
        {
            var (sql, ps) = BuildWhereClause(filter);
            whereClause = $" WHERE {sql}";
            parms = ps;
        }

        var candidates = new List<(string Id, string? Doc, Dictionary<string, object?>? Meta, float[] Emb)>();

        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT id, document, metadata_json, embedding FROM drawers{whereClause};";
            foreach (var (n, v) in parms) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);

            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                var id  = rdr.GetString(0);
                var doc = rdr.IsDBNull(1) ? null : rdr.GetString(1);
                var mj  = rdr.IsDBNull(2) ? null : rdr.GetString(2);
                var embBlob = (byte[])rdr.GetValue(3);
                var meta = mj is null ? null
                    : DeserializeMeta(mj);
                candidates.Add((id, doc, meta, BytesToFloats(embBlob)));
            }
        }

        return candidates
            .Select(c => (c.Id, c.Doc, c.Meta, Sim: CosineSimilarity(queryEmb, c.Emb)))
            .OrderByDescending(x => x.Sim)
            .Take(nResults)
            .Select(x => new VectorSearchResult(x.Id, x.Doc, x.Meta,
                Math.Round(x.Sim, 6)))
            .ToArray();
    }

    public VectorRecord[] Get(
        MetadataFilter? filter = null,
        string[]? ids = null,
        int limit = 100,
        int offset = 0,
        bool includeDocuments = true,
        bool includeMetadatas = true)
    {
        var docCol  = includeDocuments ? "document"      : "NULL";
        var metaCol = includeMetadatas ? "metadata_json" : "NULL";

        string whereClause;
        List<(string Name, object? Value)> parms;

        if (ids is { Length: > 0 })
        {
            // id IN (...)  — build positional params
            var placeholders = ids.Select((_, i) => $"$id{i}").ToList();
            whereClause = $" WHERE id IN ({string.Join(',', placeholders)})";
            parms = ids.Select((id, i) => ($"$id{i}", (object?)id)).ToList();
        }
        else if (filter is not null)
        {
            var (sql, ps) = BuildWhereClause(filter);
            whereClause = $" WHERE {sql}";
            parms = ps;
        }
        else
        {
            whereClause = "";
            parms = [];
        }

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, {docCol}, {metaCol}
            FROM drawers{whereClause}
            LIMIT $limit OFFSET $offset;
            """;
        foreach (var (n, v) in parms) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$limit",  limit);
        cmd.Parameters.AddWithValue("$offset", offset);

        var results = new List<VectorRecord>();
        using var rdr2 = cmd.ExecuteReader();
        while (rdr2.Read())
        {
            var id  = rdr2.GetString(0);
            var doc = rdr2.IsDBNull(1) ? null : rdr2.GetString(1);
            var mj  = rdr2.IsDBNull(2) ? null : rdr2.GetString(2);
            var meta = mj is null ? null
                : DeserializeMeta(mj);
            results.Add(new VectorRecord(id, doc, meta));
        }
        return results.ToArray();
    }

    public int Count()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM drawers;";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Load max(source_mtime) per source_file in one query — for fast already-mined checks.</summary>
    public Dictionary<string, double> LoadMinedMtimes()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT source_file, MAX(source_mtime) FROM drawers GROUP BY source_file;";
        using var r = cmd.ExecuteReader();
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        while (r.Read())
            result[r.GetString(0)] = r.GetDouble(1);
        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (string Sql, List<(string Name, object? Value)> Params)
        BuildWhereClause(MetadataFilter filter, string? tableAlias = null)
    {
        var prefix = tableAlias is null ? "" : tableAlias + ".";
        var parts  = new List<string>();
        var parms  = new List<(string, object?)>();
        int n = 0;

        foreach (var (key, value) in filter.Clauses)
        {
            // Map known metadata keys to named columns; others fall through
            var col = key switch
            {
                "wing"        => $"{prefix}wing",
                "room"        => $"{prefix}room",
                "source_file" => $"{prefix}source_file",
                "added_by"    => $"{prefix}added_by",
                _ => null,
            };

            if (col is not null)
            {
                var pname = $"$f{n++}";
                parts.Add($"{col} = {pname}");
                parms.Add((pname, value));
            }
        }

        return parts.Count > 0
            ? (string.Join(" AND ", parts), parms)
            : ("1=1", parms);  // no-op filter
    }

    private static VectorSearchResult[] ReadSearchResults(SqliteCommand cmd)
    {
        var results = new List<VectorSearchResult>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            var id       = rdr.GetString(0);
            var doc      = rdr.IsDBNull(1) ? null : rdr.GetString(1);
            var mj       = rdr.IsDBNull(2) ? null : rdr.GetString(2);
            var distance = rdr.GetDouble(3);
            var meta     = mj is null ? null
                : DeserializeMeta(mj);
            // sqlite-vec cosine distance is 0..2; normalise to similarity 0..1
            var sim = Math.Max(0, Math.Round(1.0 - distance / 2.0, 6));
            results.Add(new VectorSearchResult(id, doc, meta, sim));
        }
        return results.ToArray();
    }

    // ── Metadata deserialisation ──────────────────────────────────────────────

    /// <summary>
    /// System.Text.Json deserialises Dictionary&lt;string,object?&gt; values as
    /// JsonElement, not native types.  This unwraps them to string/long/double/bool/null
    /// so all call sites can use simple `as string`, `is long` etc. patterns.
    /// </summary>
    private static Dictionary<string, object?>? DeserializeMeta(string? json)
    {
        if (json is null) return null;
        var raw = JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
        if (raw is null) return null;
        var result = new Dictionary<string, object?>(raw.Count, StringComparer.Ordinal);
        foreach (var (k, v) in raw)
            result[k] = v is System.Text.Json.JsonElement je ? UnwrapJsonElement(je) : v;
        return result;
    }

    private static object? UnwrapJsonElement(System.Text.Json.JsonElement je) => je.ValueKind switch
    {
        System.Text.Json.JsonValueKind.String  => je.GetString(),
        System.Text.Json.JsonValueKind.Number  =>
            je.TryGetInt64(out var l) ? (object?)l : je.GetDouble(),
        System.Text.Json.JsonValueKind.True    => true,
        System.Text.Json.JsonValueKind.False   => false,
        System.Text.Json.JsonValueKind.Null    => null,
        _                                      => je.GetRawText(),
    };

    // ── Embedding BLOB serialisation ──────────────────────────────────────────

    internal static byte[] FloatsToBytes(float[] floats)
    {
        var bytes = new byte[floats.Length * sizeof(float)];
        MemoryMarshal.Cast<float, byte>(floats.AsSpan()).CopyTo(bytes);
        return bytes;
    }

    internal static float[] BytesToFloats(byte[] bytes)
    {
        var floats = new float[bytes.Length / sizeof(float)];
        MemoryMarshal.Cast<byte, float>(bytes.AsSpan()).CopyTo(floats);
        return floats;
    }

    // ── Cosine similarity (brute-force fallback) ──────────────────────────────

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;
        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot   += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        float denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom < 1e-9f ? 0f : dot / denom;
    }

    // ── sqlite-vec extension loading ──────────────────────────────────────────

    private bool TryLoadVec0()
    {
        try
        {
            _conn.EnableExtensions(true);
            // Try well-known locations: env var, adjacent to executable, system
            var candidates = new[]
            {
                Environment.GetEnvironmentVariable("SQLITE_VEC_PATH"),
                Path.Combine(AppContext.BaseDirectory, "vec0"),
                "vec0",
            }.Where(p => p is not null).Select(p => p!);

            foreach (var path in candidates)
            {
                try
                {
                    _conn.LoadExtension(path);
                    return true;
                }
                catch { /* try next */ }
            }
        }
        catch { /* extension loading not supported */ }
        return false;
    }

    // ── Low-level SQLite helpers ──────────────────────────────────────────────

    private void Execute(string sql)
    {
        // Execute potentially multi-statement DDL
        foreach (var stmt in sql.Split(';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(stmt)) continue;
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = stmt + ";";
            cmd.ExecuteNonQuery();
        }
    }

    private void ExecuteParams(string sql, params (string Name, object? Value)[] parms)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in parms)
            cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _conn.Dispose();
    }
}

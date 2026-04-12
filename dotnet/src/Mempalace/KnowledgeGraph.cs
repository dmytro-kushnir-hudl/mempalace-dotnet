using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Mempalace;

// ---------------------------------------------------------------------------
// Data types
// ---------------------------------------------------------------------------

public sealed record KgTriple(
    string Id,
    string Direction,
    string Subject,
    string Predicate,
    string Object,
    string? ValidFrom,
    string? ValidTo,
    double Confidence,
    string? SourceCloset,
    bool Current);

public sealed record KgStats(
    int Entities,
    int Triples,
    int CurrentFacts,
    int ExpiredFacts,
    IReadOnlyList<string> RelationshipTypes);

// ---------------------------------------------------------------------------
// KnowledgeGraph
// ---------------------------------------------------------------------------

public sealed class KnowledgeGraph : IDisposable
{
    private readonly SqliteConnection _conn;
    private bool _disposed;

    public static string DefaultPath =>
        Path.Combine(Constants.DefaultConfigDir, "knowledge_graph.sqlite3");

    public KnowledgeGraph(string? dbPath = null)
    {
        var path = dbPath ?? DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        _conn = new SqliteConnection($"Data Source={path}");
        _conn.Open();

        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA foreign_keys=ON;");
        CreateSchema();
    }

    // -------------------------------------------------------------------------
    // Schema
    // -------------------------------------------------------------------------

    private void CreateSchema()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS entities (
                id         TEXT PRIMARY KEY,
                name       TEXT NOT NULL,
                type       TEXT DEFAULT 'unknown',
                properties TEXT DEFAULT '{}',
                created_at TEXT DEFAULT (datetime('now'))
            );
            """);

        Execute("""
            CREATE TABLE IF NOT EXISTS triples (
                id           TEXT PRIMARY KEY,
                subject      TEXT NOT NULL,
                predicate    TEXT NOT NULL,
                object       TEXT NOT NULL,
                valid_from   TEXT,
                valid_to     TEXT,
                confidence   REAL DEFAULT 1.0,
                source_closet TEXT,
                source_file  TEXT,
                extracted_at TEXT DEFAULT (datetime('now')),
                FOREIGN KEY (subject) REFERENCES entities(id),
                FOREIGN KEY (object)  REFERENCES entities(id)
            );
            """);

        Execute("CREATE INDEX IF NOT EXISTS idx_triples_subject  ON triples(subject);");
        Execute("CREATE INDEX IF NOT EXISTS idx_triples_object   ON triples(object);");
        Execute("CREATE INDEX IF NOT EXISTS idx_triples_predicate ON triples(predicate);");
    }

    // -------------------------------------------------------------------------
    // Write operations
    // -------------------------------------------------------------------------

    /// <summary>Add or update an entity. Returns the entity ID.</summary>
    public string AddEntity(string name, string type = "unknown",
        Dictionary<string, object?>? properties = null)
    {
        var id    = NormaliseName(name);
        var props = JsonSerializer.Serialize(properties ?? new Dictionary<string, object?>());

        Execute("""
            INSERT INTO entities (id, name, type, properties)
            VALUES ($id, $name, $type, $props)
            ON CONFLICT(id) DO UPDATE SET
                name       = excluded.name,
                type       = excluded.type,
                properties = excluded.properties;
            """,
            ("$id", id), ("$name", name), ("$type", type), ("$props", props));

        return id;
    }

    /// <summary>
    ///     Add a relationship triple. Auto-creates subject/object entities if missing.
    ///     Returns the triple ID.
    /// </summary>
    public string AddTriple(
        string subject,
        string predicate,
        string obj,
        string? validFrom    = null,
        string? validTo      = null,
        double confidence    = 1.0,
        string? sourceCloset = null,
        string? sourceFile   = null)
    {
        var subId  = AddEntity(subject);
        var objId  = AddEntity(obj);
        var predId = NormaliseName(predicate);
        var id     = TripleId(subId, predId, objId);

        Execute("""
            INSERT INTO triples
                (id, subject, predicate, object, valid_from, valid_to,
                 confidence, source_closet, source_file)
            VALUES
                ($id, $sub, $pred, $obj, $from, $to, $conf, $closet, $file)
            ON CONFLICT(id) DO UPDATE SET
                valid_from    = excluded.valid_from,
                valid_to      = excluded.valid_to,
                confidence    = excluded.confidence,
                source_closet = excluded.source_closet,
                source_file   = excluded.source_file;
            """,
            ("$id",     id),
            ("$sub",    subId),
            ("$pred",   predId),
            ("$obj",    objId),
            ("$from",   (object?)validFrom   ?? DBNull.Value),
            ("$to",     (object?)validTo     ?? DBNull.Value),
            ("$conf",   confidence),
            ("$closet", (object?)sourceCloset ?? DBNull.Value),
            ("$file",   (object?)sourceFile   ?? DBNull.Value));

        return id;
    }

    /// <summary>Mark a triple as no longer valid (set valid_to).</summary>
    public void Invalidate(string subject, string predicate, string obj, string? ended = null)
    {
        var subId  = NormaliseName(subject);
        var predId = NormaliseName(predicate);
        var objId  = NormaliseName(obj);
        var end    = ended ?? DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        Execute("""
            UPDATE triples SET valid_to = $end
            WHERE subject = $sub AND predicate = $pred AND object = $obj
              AND (valid_to IS NULL OR valid_to > $end);
            """,
            ("$end", end), ("$sub", subId), ("$pred", predId), ("$obj", objId));
    }

    // -------------------------------------------------------------------------
    // Query operations
    // -------------------------------------------------------------------------

    /// <summary>
    ///     All triples for <paramref name="name"/>.
    ///     Pass <paramref name="asOf"/> (ISO date) for temporal filtering.
    ///     <paramref name="direction"/>: "outgoing" | "incoming" | "both".
    /// </summary>
    public IReadOnlyList<KgTriple> QueryEntity(
        string name,
        string? asOf      = null,
        string direction  = "outgoing")
    {
        var entityId = NormaliseName(name);
        var results  = new List<KgTriple>();

        if (direction is "outgoing" or "both")
            results.AddRange(QueryTriples("subject", entityId, asOf, "outgoing"));

        if (direction is "incoming" or "both")
            results.AddRange(QueryTriples("object", entityId, asOf, "incoming"));

        return results;
    }

    /// <summary>All triples with a given predicate, optionally filtered to a point in time.</summary>
    public IReadOnlyList<KgTriple> QueryRelationship(string predicate, string? asOf = null)
    {
        var predId = NormaliseName(predicate);
        var sql    = BuildTemporalQuery("predicate = $pred", asOf);

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$pred", predId);
        if (asOf is not null) AddAsOfParams(cmd, asOf);

        return ReadTriples(cmd, "outgoing");
    }

    /// <summary>Chronological timeline of facts, optionally scoped to an entity.</summary>
    public IReadOnlyList<KgTriple> Timeline(string? entityName = null)
    {
        string filter = entityName is not null
            ? "WHERE (subject = $id OR object = $id)"
            : "";

        var sql = $"""
            SELECT id, subject, predicate, object,
                   valid_from, valid_to, confidence, source_closet,
                   (valid_to IS NULL) AS current
            FROM triples
            {filter}
            ORDER BY COALESCE(valid_from, extracted_at)
            LIMIT 100;
            """;

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        if (entityName is not null)
            cmd.Parameters.AddWithValue("$id", NormaliseName(entityName));

        return ReadTriples(cmd, "outgoing");
    }

    // -------------------------------------------------------------------------
    // Stats
    // -------------------------------------------------------------------------

    public KgStats Stats()
    {
        int entities = ScalarInt("SELECT COUNT(*) FROM entities;");
        int triples  = ScalarInt("SELECT COUNT(*) FROM triples;");
        int current  = ScalarInt("SELECT COUNT(*) FROM triples WHERE valid_to IS NULL;");
        int expired  = triples - current;

        var relTypes = new List<string>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT predicate FROM triples ORDER BY predicate;";
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read()) relTypes.Add(rdr.GetString(0));

        return new KgStats(entities, triples, current, expired, relTypes);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private List<KgTriple> QueryTriples(
        string column, string value, string? asOf, string direction)
    {
        var sql = BuildTemporalQuery($"{column} = $val", asOf);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$val", value);
        if (asOf is not null) AddAsOfParams(cmd, asOf);
        return ReadTriples(cmd, direction);
    }

    private static string BuildTemporalQuery(string filter, string? asOf)
    {
        var temporalClause = asOf is not null
            ? "AND (valid_from IS NULL OR valid_from <= $asOf) " +
              "AND (valid_to   IS NULL OR valid_to   >= $asOf)"
            : "";

        return $"""
            SELECT id, subject, predicate, object,
                   valid_from, valid_to, confidence, source_closet,
                   (valid_to IS NULL) AS current
            FROM triples
            WHERE {filter} {temporalClause}
            ORDER BY COALESCE(valid_from, extracted_at);
            """;
    }

    private static void AddAsOfParams(SqliteCommand cmd, string asOf)
    {
        cmd.Parameters.AddWithValue("$asOf", asOf);
    }

    private static List<KgTriple> ReadTriples(SqliteCommand cmd, string direction)
    {
        var list = new List<KgTriple>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            list.Add(new KgTriple(
                Id:           rdr.GetString(0),
                Direction:    direction,
                Subject:      rdr.GetString(1),
                Predicate:    rdr.GetString(2),
                Object:       rdr.GetString(3),
                ValidFrom:    rdr.IsDBNull(4) ? null : rdr.GetString(4),
                ValidTo:      rdr.IsDBNull(5) ? null : rdr.GetString(5),
                Confidence:   rdr.GetDouble(6),
                SourceCloset: rdr.IsDBNull(7) ? null : rdr.GetString(7),
                Current:      rdr.GetInt32(8) == 1));
        }
        return list;
    }

    private int ScalarInt(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    // ── ID normalisation ──────────────────────────────────────────────────────

    public static string NormaliseName(string name) =>
        name.ToLowerInvariant()
            .Replace(' ', '_')
            .Replace("'", "")
            .Trim('_');

    private static string TripleId(string subId, string predId, string objId)
    {
        var input = Encoding.UTF8.GetBytes($"{subId}|{predId}|{objId}");
        var hash  = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant()[..12];
        return $"t_{subId}_{predId}_{objId}_{hash}";
    }

    // -------------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _conn.Dispose();
    }
}

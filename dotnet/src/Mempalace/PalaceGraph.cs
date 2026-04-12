using System.Text.Json.Nodes;
using Mempalace.Storage;

namespace Mempalace;

// ---------------------------------------------------------------------------
// PalaceGraph — port of palace_graph.py
//
// Builds a navigable graph from palace ChromaDB metadata.
//   Nodes = rooms (named ideas)
//   Edges = rooms that appear in multiple wings (tunnels)
//   Halls = corridor metadata (hall field on drawers)
//
// Richer than the original BFS in McpTools — includes halls, dates,
// connected_via, fuzzy room matching, and full graph stats.
// ---------------------------------------------------------------------------

public sealed record RoomNode(
    string Room,
    IReadOnlyList<string> Wings,
    IReadOnlyList<string> Halls,
    int Count,
    IReadOnlyList<string> RecentDates);

public sealed record GraphEdge(
    string Room,
    string WingA,
    string WingB,
    string Hall,
    int Count);

public sealed record TraversalNode(
    string Room,
    IReadOnlyList<string> Wings,
    IReadOnlyList<string> Halls,
    int Count,
    int Hop,
    IReadOnlyList<string>? ConnectedVia = null);

public sealed record TunnelResult(
    string Room,
    IReadOnlyList<string> Wings,
    IReadOnlyList<string> Halls,
    int Count,
    string RecentDate);

public sealed record PalaceGraphStats(
    int TotalRooms,
    int TunnelRooms,
    int TotalEdges,
    IReadOnlyDictionary<string, int> RoomsPerWing,
    IReadOnlyList<object> TopTunnels);

public static class PalaceGraph
{
    // ── Build ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Build node and edge sets from ChromaDB metadata.
    /// Skips "general" rooms (unfocused catch-all).
    /// </summary>
    public static (IReadOnlyDictionary<string, RoomNode> Nodes, IReadOnlyList<GraphEdge> Edges)
        Build(IVectorCollection col)
    {
        var allMetas = new List<Dictionary<string, object?>?>();
        int offset = 0;
        while (true)
        {
            var rows = col.Get(
                limit: 1_000,
                offset: offset,
                includeMetadatas: true,
                includeDocuments: false);

            if (rows.Length == 0) break;
            allMetas.AddRange(rows.Select(r => r.Metadata));
            offset += rows.Length;
            if (rows.Length < 1_000) break;
        }
        return BuildFromData(allMetas);
    }

    /// <summary>
    /// Build node and edge sets from raw metadata entries.
    /// Used internally and by tests to avoid needing a live ChromaDB.
    /// </summary>
    internal static (IReadOnlyDictionary<string, RoomNode> Nodes, IReadOnlyList<GraphEdge> Edges)
        BuildFromData(IEnumerable<Dictionary<string, object?>?> metadatas)
    {
        var roomData = new Dictionary<string, (
            HashSet<string> Wings,
            HashSet<string> Halls,
            HashSet<string> Dates,
            int Count)>();

        foreach (var meta in metadatas)
        {
            var room = meta?.GetValueOrDefault("room") as string ?? "";
            var wing = meta?.GetValueOrDefault("wing") as string ?? "";
            var hall = meta?.GetValueOrDefault("hall") as string ?? "";
            var date = meta?.GetValueOrDefault("date") as string ?? "";

            if (string.IsNullOrEmpty(room) || room == "general"
                || string.IsNullOrEmpty(wing))
                continue;

            if (!roomData.ContainsKey(room))
                roomData[room] = ([], [], [], 0);

            var r = roomData[room];
            r.Wings.Add(wing);
            if (!string.IsNullOrEmpty(hall)) r.Halls.Add(hall);
            if (!string.IsNullOrEmpty(date)) r.Dates.Add(date);
            roomData[room] = (r.Wings, r.Halls, r.Dates, r.Count + 1);
        }

        // Build nodes
        var nodes = roomData.ToDictionary(
            kv => kv.Key,
            kv => new RoomNode(
                Room: kv.Key,
                Wings: kv.Value.Wings.OrderBy(w => w).ToList(),
                Halls: kv.Value.Halls.OrderBy(h => h).ToList(),
                Count: kv.Value.Count,
                RecentDates: kv.Value.Dates.OrderDescending().Take(5).ToList()));

        // Build edges (rooms spanning multiple wings)
        var edges = new List<GraphEdge>();
        foreach (var (room, node) in nodes)
        {
            var wings = node.Wings;
            if (wings.Count < 2) continue;
            for (int i = 0; i < wings.Count; i++)
                for (int j = i + 1; j < wings.Count; j++)
                    foreach (var hall in node.Halls.DefaultIfEmpty(""))
                        edges.Add(new GraphEdge(room, wings[i], wings[j], hall, node.Count));
        }

        return (nodes, edges);
    }

    // ── Traverse ──────────────────────────────────────────────────────────────

    /// <summary>
    /// BFS from startRoom over shared wings. Richer than the basic McpTools BFS:
    /// returns halls metadata, connected_via wings, and capped at 50 results.
    /// Returns error dict if room not found (with fuzzy suggestions).
    /// </summary>
    public static JsonNode Traverse(
        IVectorCollection col, string startRoom, int maxHops = 2)
    {
        var (nodes, _) = Build(col);
        return TraverseNodes(nodes, startRoom, maxHops);
    }

    internal static JsonNode Traverse(
        IEnumerable<Dictionary<string, object?>?> metadatas, string startRoom, int maxHops = 2)
    {
        var (nodes, _) = BuildFromData(metadatas);
        return TraverseNodes(nodes, startRoom, maxHops);
    }

    private static JsonNode TraverseNodes(
        IReadOnlyDictionary<string, RoomNode> nodes, string startRoom, int maxHops)
    {
        if (!nodes.TryGetValue(startRoom, out var startNode))
        {
            var suggestions = FuzzyMatch(startRoom, nodes.Keys);
            return new JsonObject
            {
                ["error"]       = $"Room '{startRoom}' not found",
                ["suggestions"] = new JsonArray(suggestions.Select(s => JsonValue.Create(s)).ToArray()),
            };
        }

        var visited  = new HashSet<string> { startRoom };
        var results  = new List<TraversalNode>
        {
            new(startRoom, startNode.Wings, startNode.Halls, startNode.Count, 0),
        };
        var frontier = new Queue<(string Room, int Hop)>();
        frontier.Enqueue((startRoom, 0));

        while (frontier.Count > 0)
        {
            var (curRoom, depth) = frontier.Dequeue();
            if (depth >= maxHops) continue;

            var curWings = new HashSet<string>(nodes[curRoom].Wings);

            foreach (var (room, node) in nodes)
            {
                if (visited.Contains(room)) continue;
                var shared = curWings.Intersect(node.Wings).OrderBy(w => w).ToList();
                if (shared.Count == 0) continue;

                visited.Add(room);
                results.Add(new TraversalNode(
                    room, node.Wings, node.Halls, node.Count,
                    depth + 1, shared));

                if (depth + 1 < maxHops) frontier.Enqueue((room, depth + 1));
            }
        }

        results.Sort((a, b) =>
            a.Hop != b.Hop ? a.Hop.CompareTo(b.Hop) : b.Count.CompareTo(a.Count));

        return System.Text.Json.JsonSerializer.SerializeToNode(
            results.Take(50).Select(r => new
            {
                room          = r.Room,
                wings         = r.Wings,
                halls         = r.Halls,
                count         = r.Count,
                hop           = r.Hop,
                connected_via = r.ConnectedVia,
            }))!;
    }

    // ── Find tunnels ──────────────────────────────────────────────────────────

    /// <summary>
    /// Rooms that bridge two or more wings. Optionally filter to those including
    /// wingA and/or wingB. Returns halls, count, and most recent date.
    /// </summary>
    public static IReadOnlyList<TunnelResult> FindTunnels(
        IVectorCollection col, string? wingA = null, string? wingB = null)
    {
        var (nodes, _) = Build(col);
        return FindTunnelsFromNodes(nodes, wingA, wingB);
    }

    internal static IReadOnlyList<TunnelResult> FindTunnels(
        IEnumerable<Dictionary<string, object?>?> metadatas,
        string? wingA = null, string? wingB = null)
    {
        var (nodes, _) = BuildFromData(metadatas);
        return FindTunnelsFromNodes(nodes, wingA, wingB);
    }

    private static List<TunnelResult> FindTunnelsFromNodes(
        IReadOnlyDictionary<string, RoomNode> nodes, string? wingA, string? wingB)
    {
        return nodes.Values
            .Where(n => n.Wings.Count >= 2
                && (wingA is null || n.Wings.Contains(wingA))
                && (wingB is null || n.Wings.Contains(wingB)))
            .Select(n => new TunnelResult(
                n.Room, n.Wings, n.Halls, n.Count,
                n.RecentDates.Count > 0 ? n.RecentDates[0] : ""))
            .OrderByDescending(t => t.Count)
            .Take(50)
            .ToList();
    }

    // ── Stats ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Palace graph statistics: total/tunnel rooms, edges, rooms per wing, top tunnels.
    /// </summary>
    public static PalaceGraphStats GraphStats(IVectorCollection col)
    {
        var (nodes, edges) = Build(col);
        return GraphStatsFromNodes(nodes, edges);
    }

    internal static PalaceGraphStats GraphStats(IEnumerable<Dictionary<string, object?>?> metadatas)
    {
        var (nodes, edges) = BuildFromData(metadatas);
        return GraphStatsFromNodes(nodes, edges);
    }

    private static PalaceGraphStats GraphStatsFromNodes(
        IReadOnlyDictionary<string, RoomNode> nodes, IReadOnlyList<GraphEdge> edges)
    {
        int tunnelRooms = nodes.Values.Count(n => n.Wings.Count >= 2);
        var roomsPerWing = new Dictionary<string, int>();
        foreach (var node in nodes.Values)
            foreach (var wing in node.Wings)
                roomsPerWing[wing] = roomsPerWing.GetValueOrDefault(wing) + 1;

        var topTunnels = nodes.Values
            .Where(n => n.Wings.Count >= 2)
            .OrderByDescending(n => n.Wings.Count)
            .ThenByDescending(n => n.Count)
            .Take(10)
            .Select(n => (object)new { room = n.Room, wings = n.Wings, count = n.Count })
            .ToList();

        return new PalaceGraphStats(
            nodes.Count, tunnelRooms, edges.Count,
            roomsPerWing.OrderByDescending(kv => kv.Value)
                .ToDictionary(kv => kv.Key, kv => kv.Value),
            topTunnels);
    }

    // ── Fuzzy match ───────────────────────────────────────────────────────────

    private static List<string> FuzzyMatch(string query, IEnumerable<string> rooms)
    {
        var q = query.ToLowerInvariant();
        return rooms
            .Select(r =>
            {
                var rl = r.ToLowerInvariant();
                double score = rl.Contains(q) ? 1.0
                    : q.Contains(rl) ? 0.9
                    : q.Split('-').Any(w => w.Length >= 3 && rl.Contains(w)) ? 0.5
                    : EditDistance(q, rl) <= 2 ? 0.3
                    : 0.0;
                return (Room: r, Score: score);
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(5)
            .Select(x => x.Room)
            .ToList();
    }

    private static int EditDistance(string a, string b)
    {
        int m = a.Length, n = b.Length;
        var dp = new int[m + 1, n + 1];
        for (int i = 0; i <= m; i++) dp[i, 0] = i;
        for (int j = 0; j <= n; j++) dp[0, j] = j;
        for (int i = 1; i <= m; i++)
            for (int j = 1; j <= n; j++)
                dp[i, j] = a[i - 1] == b[j - 1]
                    ? dp[i - 1, j - 1]
                    : 1 + Math.Min(dp[i - 1, j - 1], Math.Min(dp[i - 1, j], dp[i, j - 1]));
        return dp[m, n];
    }
}

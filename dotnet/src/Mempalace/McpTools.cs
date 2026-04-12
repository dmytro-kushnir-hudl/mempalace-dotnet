using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Mempalace.Storage;
using Microsoft.Extensions.AI;

namespace Mempalace;

// ---------------------------------------------------------------------------
// MCP tool implementations — one static method per tool
// All return JsonNode (serialized to text for MCP content response)
// ---------------------------------------------------------------------------

public sealed class McpToolContext(
    string palacePath,
    string collectionName,
    string kgPath,
    IEmbeddingGenerator<ReadOnlyMemory<char>, Embedding<float>> embedder,
    VectorBackend backend = VectorBackend.Sqlite)
{
    public string PalacePath { get; } = palacePath;
    public string CollectionName { get; } = collectionName;
    public string KgPath { get; } = kgPath;
    public VectorBackend Backend { get; } = backend;
    public IEmbeddingGenerator<ReadOnlyMemory<char>, Embedding<float>> Embedder { get; } = embedder;

    public PalaceSession OpenPalace()
    {
        return PalaceSession.Open(PalacePath, CollectionName, Backend);
    }

    public KnowledgeGraph OpenKg()
    {
        return new KnowledgeGraph(KgPath);
    }
}

public static class McpTools
{
    // ── AAAK & Protocol spec ─────────────────────────────────────────────────

    public const string PalaceProtocol = """
                                         IMPORTANT — MemPalace Memory Protocol:
                                         1. ON WAKE-UP: Call mempalace_status to load palace overview + AAAK spec.
                                         2. BEFORE RESPONDING about any person, project, or past event: call mempalace_kg_query or mempalace_search FIRST. Never guess — verify.
                                         3. IF UNSURE about a fact: say "let me check" and query the palace.
                                         4. AFTER EACH SESSION: call mempalace_diary_write to record what happened.
                                         5. WHEN FACTS CHANGE: call mempalace_kg_invalidate + mempalace_kg_add.
                                         """;

    public const string AaakSpec = """
                                   AAAK — compressed memory dialect. Readable by humans and LLMs without decoding.
                                   ENTITIES: 3-letter codes. ALC=Alice, JOR=Jordan, RIL=Riley, MAX=Max, BEN=Ben.
                                   EMOTIONS: *markers*. *warm*=joy, *fierce*=determined, *raw*=vulnerable, *bloom*=tenderness.
                                   STRUCTURE: FAM: family | PROJ: projects | ⚠: warnings. Dates: ISO. Counts: Nx.
                                   EXAMPLE: FAM: ALC→♡JOR | 2D(kids): RIL(18,sports) MAX(11,chess+swimming)
                                   """;

    // All tool responses use camelCase for consistency.
    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly string WalDir = Path.Combine(Constants.DefaultConfigDir, "wal");
    private static readonly string WalFile = Path.Combine(WalDir, "write_log.jsonl");

    private static void WalLog(string op, object p, object? result = null)
    {
        try
        {
            Directory.CreateDirectory(WalDir);
            var entry = JsonSerializer.Serialize(new
            {
                timestamp = DateTime.UtcNow.ToString("O"),
                operation = op,
                @params = p,
                result
            });
            File.AppendAllText(WalFile, entry + "\n");
        }
        catch
        {
            /* WAL must never crash the tool */
        }
    }

    // ── Status & exploration ─────────────────────────────────────────────────

    public static JsonNode Status(McpToolContext ctx)
    {
        try
        {
            using var s = ctx.OpenPalace();
            var count = s.Collection.Count();
            var (wings, rooms) = GetWingsAndRooms(s.Collection);
            return JsonNode.Parse(JsonSerializer.Serialize(new
            {
                total_drawers = count,
                wings,
                rooms,
                palace_path = ctx.PalacePath,
                backend = ctx.Backend.ToString("G"),
                protocol = PalaceProtocol,
                aaak_dialect = AaakSpec
            }))!;
        }
        catch
        {
            return new JsonObject { ["error"] = "No palace found", ["hint"] = "Run: mempalace mine <dir>" };
        }
    }

    public static JsonNode ListWings(McpToolContext ctx)
    {
        try
        {
            using var s = ctx.OpenPalace();
            var (wings, _) = GetWingsAndRooms(s.Collection);
            return JsonNode.Parse(JsonSerializer.Serialize(new { wings }))!;
        }
        catch
        {
            return NoPalace();
        }
    }

    public static JsonNode ListRooms(McpToolContext ctx, string? wing = null)
    {
        try
        {
            using var s = ctx.OpenPalace();
            var filter = wing is not null ? MetadataFilter.Where("wing", wing) : null;
            var rows = s.Collection.Get(filter, limit: 10_000, includeMetadatas: true, includeDocuments: false);
            var rooms = rows
                .GroupBy(r => r.Metadata?.GetValueOrDefault("room") as string ?? "general")
                .ToDictionary(g => g.Key, g => g.Count());
            return JsonNode.Parse(JsonSerializer.Serialize(new { wing, rooms }))!;
        }
        catch
        {
            return NoPalace();
        }
    }

    public static JsonNode GetTaxonomy(McpToolContext ctx)
    {
        try
        {
            using var s = ctx.OpenPalace();
            var rows = s.Collection.Get(limit: 10_000, includeMetadatas: true, includeDocuments: false);
            var tree = new Dictionary<string, Dictionary<string, int>>();
            foreach (var row in rows)
            {
                var w = row.Metadata?.GetValueOrDefault("wing") as string ?? "unknown";
                var r = row.Metadata?.GetValueOrDefault("room") as string ?? "general";
                if (!tree.ContainsKey(w)) tree[w] = new Dictionary<string, int>();
                tree[w][r] = tree[w].GetValueOrDefault(r) + 1;
            }

            return JsonNode.Parse(JsonSerializer.Serialize(new { taxonomy = tree }))!;
        }
        catch
        {
            return NoPalace();
        }
    }

    public static JsonNode GetAaakSpec(McpToolContext _)
    {
        return new JsonObject { ["spec"] = AaakSpec, ["protocol"] = PalaceProtocol };
    }

    // ── Search ───────────────────────────────────────────────────────────────

    public static async Task<JsonNode> SearchAsync(
        McpToolContext ctx, string query, int limit = 5,
        string? wing = null, string? room = null, CancellationToken ct = default)
    {
        if (limit <= 0)
            return JsonNode.Parse(JsonSerializer.Serialize(new { query, wing, room, results = Array.Empty<object>() },
                CamelCase))!;
        try
        {
            var response = await Searcher.SearchMemoriesAsync(
                query, ctx.PalacePath, ctx.Embedder, wing, room, limit,
                ctx.CollectionName, ctx.Backend, ct);
            return JsonNode.Parse(JsonSerializer.Serialize(response, CamelCase))!;
        }
        catch (SearchError ex)
        {
            return new JsonObject { ["error"] = ex.Message };
        }
    }

    public static async Task<JsonNode> CheckDuplicateAsync(
        McpToolContext ctx, string content, double threshold = 0.9, CancellationToken ct = default)
    {
        try
        {
            using var s = ctx.OpenPalace();
            var results = await s.Collection.SearchAsync(content, ctx.Embedder, ct: ct);
            var dupes = results
                .Where(r => r.Similarity >= threshold)
                .Select(r => (object)new { id = r.Id, similarity = Math.Round(r.Similarity, 3) })
                .ToList();
            return JsonNode.Parse(JsonSerializer.Serialize(new { is_duplicate = dupes.Count > 0, matches = dupes }))!;
        }
        catch
        {
            return NoPalace();
        }
    }

    // ── Graph (BFS over rooms) ────────────────────────────────────────────────

    public static JsonNode TraverseGraph(McpToolContext ctx, string startRoom, int maxHops = 2)
    {
        try
        {
            using var s = ctx.OpenPalace();
            return PalaceGraph.Traverse(s.Collection, startRoom, maxHops);
        }
        catch
        {
            return NoPalace();
        }
    }

    public static JsonNode FindTunnels(McpToolContext ctx, string? wingA = null, string? wingB = null)
    {
        try
        {
            using var s = ctx.OpenPalace();
            var tunnels = PalaceGraph.FindTunnels(s.Collection, wingA, wingB);
            return JsonNode.Parse(JsonSerializer.Serialize(new { tunnels }))!;
        }
        catch
        {
            return NoPalace();
        }
    }

    public static JsonNode GraphStats(McpToolContext ctx)
    {
        try
        {
            using var s = ctx.OpenPalace();
            var stats = PalaceGraph.GraphStats(s.Collection);
            return JsonNode.Parse(JsonSerializer.Serialize(stats, CamelCase))!;
        }
        catch
        {
            return NoPalace();
        }
    }

    // ── General extractor ─────────────────────────────────────────────────────

    public static JsonNode ExtractMemories(McpToolContext ctx, string text, double minConfidence = 0.1)
    {
        try
        {
            var memories = GeneralExtractor.ExtractMemories(text, minConfidence);
            return JsonNode.Parse(JsonSerializer.Serialize(new
            {
                total = memories.Count,
                by_type = memories
                    .GroupBy(m => m.MemoryType.ToString("G").ToLowerInvariant())
                    .ToDictionary(g => g.Key, g => g.Count()),
                memories = memories.Select(m => new
                {
                    memory_type = m.MemoryType.ToString("G").ToLowerInvariant(),
                    chunk_index = m.ChunkIndex,
                    content = m.Content
                })
            }))!;
        }
        catch (Exception ex)
        {
            return new JsonObject { ["error"] = ex.Message };
        }
    }

    // ── Entity detector ───────────────────────────────────────────────────────

    public static JsonNode DetectEntities(McpToolContext ctx, string text)
    {
        try
        {
            var result = EntityDetector.DetectFromText(text);
            return JsonNode.Parse(JsonSerializer.Serialize(new
            {
                people = result.People,
                projects = result.Projects,
                uncertain = result.Uncertain
            }))!;
        }
        catch (Exception ex)
        {
            return new JsonObject { ["error"] = ex.Message };
        }
    }

    // ── AAAK Compress ─────────────────────────────────────────────────────────

    public static JsonNode Compress(McpToolContext ctx, string text,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        try
        {
            var dialect = new Dialect();
            var compressed = dialect.Compress(text, metadata);
            var stats = Dialect.GetCompressionStats(text, compressed);
            return JsonNode.Parse(JsonSerializer.Serialize(new
            {
                compressed,
                original_tokens_est = stats.OriginalTokensEst,
                summary_tokens_est = stats.SummaryTokensEst,
                size_ratio = stats.SizeRatio
            }))!;
        }
        catch (Exception ex)
        {
            return new JsonObject { ["error"] = ex.Message };
        }
    }

    // ── Write: drawers ────────────────────────────────────────────────────────

    public static async Task<JsonNode> AddDrawerAsync(
        McpToolContext ctx,
        string wing, string room, string content,
        string sourceFile = "", string addedBy = "mcp",
        CancellationToken ct = default)
    {
        try
        {
            Sanitizer.SanitizeName(wing, "wing");
            Sanitizer.SanitizeName(room, "room");
            Sanitizer.SanitizeContent(content);
        }
        catch (ArgumentException ex)
        {
            return new JsonObject { ["error"] = ex.Message };
        }

        WalLog("add_drawer", new { wing, room, content_length = content.Length, addedBy });

        try
        {
            using var s = ctx.OpenPalace();
            // Deterministic id (no source_file mtime for MCP-sourced content)
            var seed = Encoding.UTF8.GetBytes(wing + room + content[..Math.Min(64, content.Length)]);
            var hash = Convert.ToHexString(SHA256.HashData(seed)).ToLowerInvariant()[..24];
            var id = $"drawer_{wing}_{room}_{hash}";

            await s.Collection.UpsertAsync(
                [id],
                [content.AsMemory()],
                ctx.Embedder,
                [
                    new Dictionary<string, object?>
                    {
                        ["wing"] = wing,
                        ["room"] = room,
                        ["source_file"] = sourceFile,
                        ["chunk_index"] = 0L,
                        ["added_by"] = addedBy,
                        ["filed_at"] = DateTime.UtcNow.ToString("O")
                    }
                ],
                ct);

            WalLog("add_drawer_result", new { id }, new { success = true });
            return new JsonObject { ["success"] = true, ["drawer_id"] = id };
        }
        catch (Exception ex)
        {
            WalLog("add_drawer_result", new { }, new { success = false, error = ex.Message });
            return new JsonObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    public static JsonNode DeleteDrawer(McpToolContext ctx, string drawerId)
    {
        WalLog("delete_drawer", new { drawerId });
        try
        {
            using var s = ctx.OpenPalace();
            var existing = s.Collection.Get(ids: [drawerId], includeDocuments: false, includeMetadatas: false);
            if (existing.Length == 0)
                return new JsonObject { ["success"] = false, ["error"] = $"Drawer not found: {drawerId}" };
            s.Collection.Delete([drawerId]);
            WalLog("delete_drawer_result", new { drawerId }, new { success = true });
            return new JsonObject { ["success"] = true, ["drawer_id"] = drawerId };
        }
        catch (Exception ex)
        {
            return new JsonObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    // ── Knowledge graph ───────────────────────────────────────────────────────

    public static JsonNode KgQuery(McpToolContext ctx, string entity,
        string? asOf = null, string direction = "both")
    {
        try
        {
            using var kg = ctx.OpenKg();
            var triples = kg.QueryEntity(entity, asOf, direction);
            return JsonNode.Parse(JsonSerializer.Serialize(new { entity, triples }, CamelCase))!;
        }
        catch (Exception ex)
        {
            return new JsonObject { ["error"] = ex.Message };
        }
    }

    public static JsonNode KgAdd(McpToolContext ctx,
        string subject, string predicate, string obj,
        string? validFrom = null, string? validTo = null,
        double confidence = 1.0, string? sourceCloset = null)
    {
        WalLog("kg_add", new { subject, predicate, obj, validFrom, validTo });
        try
        {
            using var kg = ctx.OpenKg();
            var id = kg.AddTriple(subject, predicate, obj, validFrom, validTo, confidence, sourceCloset);
            return new JsonObject { ["success"] = true, ["triple_id"] = id };
        }
        catch (Exception ex)
        {
            return new JsonObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    public static JsonNode KgInvalidate(McpToolContext ctx,
        string subject, string predicate, string obj, string? ended = null)
    {
        WalLog("kg_invalidate", new { subject, predicate, obj, ended });
        try
        {
            using var kg = ctx.OpenKg();
            kg.Invalidate(subject, predicate, obj, ended);
            return new JsonObject { ["success"] = true };
        }
        catch (Exception ex)
        {
            return new JsonObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    public static JsonNode KgTimeline(McpToolContext ctx, string? entity = null)
    {
        try
        {
            using var kg = ctx.OpenKg();
            return JsonNode.Parse(JsonSerializer.Serialize(new { timeline = kg.Timeline(entity) }, CamelCase))!;
        }
        catch (Exception ex)
        {
            return new JsonObject { ["error"] = ex.Message };
        }
    }

    public static JsonNode KgStats(McpToolContext ctx)
    {
        try
        {
            using var kg = ctx.OpenKg();
            return JsonNode.Parse(JsonSerializer.Serialize(kg.Stats(), CamelCase))!;
        }
        catch (Exception ex)
        {
            return new JsonObject { ["error"] = ex.Message };
        }
    }

    // ── Diary ─────────────────────────────────────────────────────────────────

    public static async Task<JsonNode> DiaryWriteAsync(
        McpToolContext ctx, string agentName, string entry,
        string topic = "general", CancellationToken ct = default)
    {
        WalLog("diary_write", new { agentName, topic, entry_length = entry.Length });
        try
        {
            var wing = $"wing_{agentName.ToLowerInvariant().Replace(' ', '_')}";
            var now = DateTime.UtcNow;
            var seed = Encoding.UTF8.GetBytes(agentName + now.ToString("O", CultureInfo.InvariantCulture));
            var hash = Convert.ToHexString(SHA256.HashData(seed)).ToLowerInvariant()[..16];
            var id = $"diary_{wing}_{now:yyyyMMdd_HHmmss}_{hash}";

            using var s = ctx.OpenPalace();
            await s.Collection.UpsertAsync(
                [id],
                [entry.AsMemory()],
                ctx.Embedder,
                [
                    new Dictionary<string, object?>
                    {
                        ["wing"] = wing,
                        ["room"] = "diary",
                        ["hall"] = "hall_diary",
                        ["topic"] = topic,
                        ["type"] = "diary_entry",
                        ["agent"] = agentName,
                        ["filed_at"] = now.ToString("O", CultureInfo.InvariantCulture),
                        ["date"] = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        ["source_file"] = "",
                        ["chunk_index"] = 0L,
                        ["added_by"] = "mcp"
                    }
                ],
                ct);

            return new JsonObject { ["success"] = true, ["entry_id"] = id };
        }
        catch (Exception ex)
        {
            return new JsonObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    public static JsonNode DiaryRead(McpToolContext ctx, string agentName, int lastN = 10)
    {
        try
        {
            var wing = $"wing_{agentName.ToLowerInvariant().Replace(' ', '_')}";
            var filter = MetadataFilter.Where("wing", wing).And("room", "diary");
            using var s = ctx.OpenPalace();
            var rows = s.Collection.Get(filter, limit: 10_000,
                includeDocuments: true, includeMetadatas: true);

            var entries = rows
                .Select(r => new
                {
                    date = r.Metadata?.GetValueOrDefault("date") as string ?? "",
                    timestamp = r.Metadata?.GetValueOrDefault("filed_at") as string ?? "",
                    topic = r.Metadata?.GetValueOrDefault("topic") as string ?? "",
                    content = r.Document ?? ""
                })
                .OrderByDescending(e => e.timestamp)
                .Take(lastN)
                .ToList();

            return JsonNode.Parse(JsonSerializer.Serialize(new { agent = agentName, entries, total = rows.Length }))!;
        }
        catch
        {
            return NoPalace();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (Dictionary<string, int> wings, Dictionary<string, int> rooms)
        GetWingsAndRooms(IVectorCollection col)
    {
        var wings = new Dictionary<string, int>();
        var rooms = new Dictionary<string, int>();
        try
        {
            var rows = col.Get(limit: 10_000, includeMetadatas: true, includeDocuments: false);
            foreach (var row in rows)
            {
                var w = row.Metadata?.GetValueOrDefault("wing") as string ?? "unknown";
                var r = row.Metadata?.GetValueOrDefault("room") as string ?? "general";
                wings[w] = wings.GetValueOrDefault(w) + 1;
                rooms[r] = rooms.GetValueOrDefault(r) + 1;
            }
        }
        catch
        {
            /* empty */
        }

        return (wings, rooms);
    }

    private static JsonObject NoPalace()
    {
        return new JsonObject { ["error"] = "No palace found", ["hint"] = "Run: mempalace mine <dir>" };
    }
}
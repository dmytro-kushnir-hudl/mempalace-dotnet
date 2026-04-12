using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Mempalace;

/// <summary>
///     Minimal stdio JSON-RPC 2.0 server implementing the Model Context Protocol.
///     Reads newline-delimited JSON requests from stdin, writes responses to stdout.
///     All MCP tool logic delegates to <see cref="McpTools" />.
/// </summary>
public static class McpServer
{
    private static readonly string[] SupportedVersions =
        ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions RelaxedJsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static async Task RunAsync(McpToolContext ctx,
        TextReader? reader = null,
        TextWriter? writer = null,
        CancellationToken ct = default)
    {
        bool ownReader = reader is null, ownWriter = writer is null;
        reader ??= new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        writer ??= new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };

        Console.Error.WriteLine("[mempalace] MCP server started");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (line is null) break;
                line = line.TrimStart('\uFEFF');
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonNode? request;
                try
                {
                    request = JsonNode.Parse(line);
                }
                catch
                {
                    await WriteError(writer, null, -32700, "Parse error");
                    continue;
                }

                var response = await HandleAsync(ctx, request!, ct);
                if (response is not null)
                    await writer.WriteLineAsync(response.ToJsonString(RelaxedJsonOpts));
            }
        }
        finally
        {
            if (ownReader) reader.Dispose();
            if (ownWriter)
            {
                await writer.FlushAsync(ct);
                writer.Dispose();
            }
        }
    }

    // -------------------------------------------------------------------------

    private static async Task<JsonNode?> HandleAsync(
        McpToolContext ctx, JsonNode request, CancellationToken ct)
    {
        var method = request["method"]?.GetValue<string>() ?? "";
        var id = request["id"];
        var params_ = request["params"];

        return method switch
        {
            "initialize" => Initialize(id, params_),
            "notifications/initialized" => null, // notification — no response
            "ping" => Ok(id, new JsonObject()),
            "tools/list" => ToolsList(id),
            "tools/call" => await ToolsCall(ctx, id, params_, ct),
            _ => Error(id, -32601, $"Method not found: {method}")
        };
    }

    private static JsonObject Initialize(JsonNode? id, JsonNode? params_)
    {
        var clientVer = params_?["protocolVersion"]?.GetValue<string>() ?? SupportedVersions[^1];
        var negotiated = Array.IndexOf(SupportedVersions, clientVer) >= 0
            ? clientVer
            : SupportedVersions[0];

        return Ok(id, new JsonObject
        {
            ["protocolVersion"] = negotiated,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = "mempalace", ["version"] = "0.1.0" }
        });
    }

    private static JsonObject ToolsList(JsonNode? id)
    {
        return Ok(id, new JsonObject
        {
            ["tools"] = JsonNode.Parse(JsonSerializer.Serialize(ToolDefinitions()))!
        });
    }

    private static async Task<JsonNode> ToolsCall(
        McpToolContext ctx, JsonNode? id, JsonNode? params_, CancellationToken ct)
    {
        var name = params_?["name"]?.GetValue<string>();
        var args = params_?["arguments"]?.AsObject() ?? new JsonObject();

        if (name is null) return Error(id, -32602, "Missing tool name");

        JsonNode result;
        try
        {
            result = await DispatchAsync(ctx, name, args, ct);
        }
        catch (Exception ex)
        {
            result = new JsonObject { ["error"] = ex.Message };
        }

        return Ok(id, new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = result.ToJsonString(RelaxedJsonOpts)
                }
            }
        });
    }

    // -------------------------------------------------------------------------
    // Tool dispatch
    // -------------------------------------------------------------------------

    private static async Task<JsonNode> DispatchAsync(
        McpToolContext ctx, string name, JsonObject args, CancellationToken ct)
    {
        string? S(string key)
        {
            return args[key]?.GetValue<string>();
        }

        int I(string key, int def)
        {
            return args[key] is { } v ? (int)v.GetValue<double>() : def;
        }

        double D(string key, double def)
        {
            return args[key] is { } v ? v.GetValue<double>() : def;
        }

        return name switch
        {
            "mempalace_status" => McpTools.Status(ctx),
            "mempalace_list_wings" => McpTools.ListWings(ctx),
            "mempalace_list_rooms" => McpTools.ListRooms(ctx, S("wing")),
            "mempalace_get_taxonomy" => McpTools.GetTaxonomy(ctx),
            "mempalace_get_aaak_spec" => McpTools.GetAaakSpec(ctx),
            "mempalace_search" => S("query") is { } q
                ? await McpTools.SearchAsync(ctx, q, I("limit", 5), S("wing"), S("room"), ct)
                : new JsonObject { ["error"] = "Missing required argument: query" },
            "mempalace_check_duplicate" => await McpTools.CheckDuplicateAsync(ctx, S("content")!, D("threshold", 0.9),
                ct),
            "mempalace_traverse_graph" => McpTools.TraverseGraph(ctx, S("start_room")!, I("max_hops", 2)),
            "mempalace_find_tunnels" => McpTools.FindTunnels(ctx, S("wing_a"), S("wing_b")),
            "mempalace_graph_stats" => McpTools.GraphStats(ctx),
            "mempalace_add_drawer" => await McpTools.AddDrawerAsync(ctx, S("wing")!, S("room")!, S("content")!,
                S("source_file") ?? "", S("added_by") ?? "mcp", ct),
            "mempalace_delete_drawer" => McpTools.DeleteDrawer(ctx, S("drawer_id")!),
            "mempalace_kg_query" => McpTools.KgQuery(ctx, S("entity")!, S("as_of"), S("direction") ?? "both"),
            "mempalace_kg_add" => McpTools.KgAdd(ctx, S("subject")!, S("predicate")!, S("object")!, S("valid_from"),
                S("valid_to"), D("confidence", 1.0), S("source_closet")),
            "mempalace_kg_invalidate" => McpTools.KgInvalidate(ctx, S("subject")!, S("predicate")!, S("object")!,
                S("ended")),
            "mempalace_kg_timeline" => McpTools.KgTimeline(ctx, S("entity")),
            "mempalace_kg_stats" => McpTools.KgStats(ctx),
            "mempalace_diary_write" => await McpTools.DiaryWriteAsync(ctx, S("agent_name")!, S("entry")!,
                S("topic") ?? "general", ct),
            "mempalace_diary_read" => McpTools.DiaryRead(ctx, S("agent_name")!, I("last_n", 10)),
            "mempalace_extract_memories" => McpTools.ExtractMemories(ctx, S("text")!, D("min_confidence", 0.1)),
            "mempalace_detect_entities" => McpTools.DetectEntities(ctx, S("text")!),
            "mempalace_compress" => McpTools.Compress(ctx, S("text")!),
            _ => new JsonObject { ["error"] = $"Unknown tool: {name}" }
        };
    }

    // -------------------------------------------------------------------------
    // Tool definitions (schema for tools/list)
    // -------------------------------------------------------------------------

    private static IEnumerable<object> ToolDefinitions()
    {
        return
        [
            Tool("mempalace_status", "Palace overview — total drawers, wing and room counts", new { }),
            Tool("mempalace_list_wings", "List all wings with drawer counts", new { }),
            Tool("mempalace_list_rooms", "List rooms within a wing",
                new { wing = Str("Wing to filter (optional)") }),
            Tool("mempalace_get_taxonomy", "Full taxonomy: wing → room → drawer count", new { }),
            Tool("mempalace_get_aaak_spec", "Get the AAAK dialect specification", new { }),
            Tool("mempalace_search", "Semantic search with optional wing/room filter",
                new
                {
                    query = Req(Str("Search query")), limit = Int("Max results (default 5)"), wing = Str("Wing filter"),
                    room = Str("Room filter")
                }),
            Tool("mempalace_check_duplicate", "Check if content already exists in the palace",
                new
                {
                    content = Req(Str("Content to check")), threshold = Num("Similarity threshold 0–1 (default 0.9)")
                }),
            Tool("mempalace_traverse_graph", "BFS room traversal from a starting room",
                new { start_room = Req(Str("Room to start from")), max_hops = Int("Max hops (default 2)") }),
            Tool("mempalace_find_tunnels", "Find rooms bridging two wings",
                new { wing_a = Str("First wing"), wing_b = Str("Second wing") }),
            Tool("mempalace_graph_stats", "Palace graph overview: rooms, wings, totals", new { }),
            Tool("mempalace_add_drawer", "File verbatim content into a wing/room",
                new
                {
                    wing = Req(Str("Wing")), room = Req(Str("Room")), content = Req(Str("Verbatim content")),
                    source_file = Str("Source file (optional)"), added_by = Str("Added by (default: mcp)")
                }),
            Tool("mempalace_delete_drawer", "Delete a drawer by ID",
                new { drawer_id = Req(Str("Drawer ID")) }),
            Tool("mempalace_kg_query", "Query the knowledge graph for an entity's relationships",
                new
                {
                    entity = Req(Str("Entity name")), as_of = Str("Date filter YYYY-MM-DD"),
                    direction = Str("outgoing | incoming | both")
                }),
            Tool("mempalace_kg_add", "Add a fact to the knowledge graph",
                new
                {
                    subject = Req(Str("Subject entity")), predicate = Req(Str("Predicate")),
                    @object = Req(Str("Object entity")), valid_from = Str("Valid from YYYY-MM-DD"),
                    valid_to = Str("Valid to YYYY-MM-DD"), confidence = Num("Confidence 0–1"),
                    source_closet = Str("Source drawer ID")
                }),
            Tool("mempalace_kg_invalidate", "Mark a knowledge graph fact as no longer true",
                new
                {
                    subject = Req(Str("Subject")), predicate = Req(Str("Predicate")), @object = Req(Str("Object")),
                    ended = Str("End date YYYY-MM-DD")
                }),
            Tool("mempalace_kg_timeline", "Chronological timeline of knowledge graph facts",
                new { entity = Str("Entity to filter (optional)") }),
            Tool("mempalace_kg_stats", "Knowledge graph statistics", new { }),
            Tool("mempalace_diary_write", "Write a diary entry in AAAK format",
                new
                {
                    agent_name = Req(Str("Agent name")), entry = Req(Str("Diary entry (AAAK format)")),
                    topic = Str("Topic tag")
                }),
            Tool("mempalace_diary_read", "Read recent diary entries",
                new { agent_name = Req(Str("Agent name")), last_n = Int("Number of entries (default 10)") }),
            Tool("mempalace_extract_memories",
                "Extract typed memories (decisions/milestones/problems/preferences/emotional) from text using heuristics — no LLM required",
                new
                {
                    text = Req(Str("Text to extract from")),
                    min_confidence = Num("Minimum confidence threshold 0–1 (default 0.3)")
                }),
            Tool("mempalace_detect_entities", "Detect people and projects mentioned in text",
                new { text = Req(Str("Text to scan for entities")) }),
            Tool("mempalace_compress",
                "Compress text to AAAK dialect (lossy summary — entities, topics, key quote, emotions, flags)",
                new { text = Req(Str("Text to compress")) })
        ];
    }

    // ── Schema helpers ────────────────────────────────────────────────────────

    private static object Tool(string name, string desc, object props)
    {
        return new
        {
            name, description = desc,
            inputSchema = new { type = "object", properties = props }
        };
    }

    private static object Str(string desc)
    {
        return new { type = "string", description = desc };
    }

    private static object Int(string desc)
    {
        return new { type = "integer", description = desc };
    }

    private static object Num(string desc)
    {
        return new { type = "number", description = desc };
    }

    private static object Req(object schema)
    {
        return schema;
        // required marking is separate in JSON Schema
    }

    // ── JSON-RPC helpers ──────────────────────────────────────────────────────

    private static JsonObject Ok(JsonNode? id, JsonNode result)
    {
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result
        };
    }

    private static JsonObject Error(JsonNode? id, int code, string message)
    {
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
        };
    }

    private static async Task WriteError(TextWriter w, JsonNode? id, int code, string msg)
    {
        await w.WriteLineAsync(Error(id, code, msg).ToJsonString(RelaxedJsonOpts));
    }
}
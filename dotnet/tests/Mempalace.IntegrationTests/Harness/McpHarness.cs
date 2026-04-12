using Mempalace.Embeddings;
using Microsoft.Extensions.AI;

namespace Mempalace.IntegrationTests.Harness;

// ── McpSession ─────────────────────────────────────────────────────────────────

/// <summary>
///     Holds all responses from a single MCP session keyed by request id (as string).
///     Tool results are pre-parsed from the inner content[0].text JSON.
/// </summary>
public sealed class McpSession(Dictionary<string, JsonNode> raw)
{
    /// <summary>All responses stored with null id (e.g. parse errors).</summary>
    public IEnumerable<JsonNode> NullIdResponses =>
        raw.Where(kv => kv.Key.StartsWith("__null_")).Select(kv => kv.Value);

    /// <summary>Full JSON-RPC response for a given numeric id.</summary>
    public JsonNode Response(int id)
    {
        return raw[id.ToString()];
    }

    /// <summary>Inner tool result (content[0].text parsed as JSON) for tools/call id.</summary>
    public JsonNode Result(int id)
    {
        var text = raw[id.ToString()]["result"]?["content"]?[0]?["text"]?.GetValue<string>()
                   ?? throw new InvalidOperationException($"No content text for id {id}");
        return JsonNode.Parse(text)!;
    }

    /// <summary>True if the response has an error field at the top level.</summary>
    public bool IsError(int id)
    {
        return raw[id.ToString()]["error"] is not null;
    }

    /// <summary>True if the inner tool result contains an "error" key.</summary>
    public bool ToolError(int id)
    {
        return Result(id)["error"] is not null;
    }

    public bool HasId(int id)
    {
        return raw.ContainsKey(id.ToString());
    }
}

// ── McpHarness ─────────────────────────────────────────────────────────────────

/// <summary>
///     Drives McpServer.RunAsync in-process via StringReader/StringWriter.
///     All messages are sent at once; the server runs to EOF and returns.
/// </summary>
public static class McpHarness
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // ── Session runner ─────────────────────────────────────────────────────────

    public static async Task<McpSession> RunAsync(
        McpToolContext ctx, IEnumerable<string> lines)
    {
        var input = new StringReader(string.Join("\n", lines));
        var output = new StringBuilder();
        using var writer = new StringWriter(output);

        await McpServer.RunAsync(ctx, input, writer);

        var raw = new Dictionary<string, JsonNode>();
        var nullSeq = 0;
        foreach (var line in output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimStart('\uFEFF').Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            try
            {
                var node = JsonNode.Parse(trimmed);
                if (node is null) continue;
                var idNode = node["id"];
                if (idNode is not null)
                    raw[idNode.ToJsonString().Trim('"')] = node;
                else
                    raw[$"__null_{nullSeq++}__"] = node; // parse errors / null-id responses
            }
            catch
            {
                /* skip malformed lines */
            }
        }

        return new McpSession(raw);
    }

    // ── Message builders ───────────────────────────────────────────────────────

    public static string Initialize(int id = 1)
    {
        return JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id, method = "initialize",
            @params = new { protocolVersion = "2025-11-25" }
        }, Opts);
    }

    public static string Initialized()
    {
        return """{"jsonrpc":"2.0","method":"notifications/initialized"}""";
    }

    public static string ToolsList(int id)
    {
        return JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method = "tools/list" }, Opts);
    }

    public static string Ping(int id)
    {
        return JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method = "ping" }, Opts);
    }

    public static string UnknownMethod(int id)
    {
        return JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method = "nonexistent/method" }, Opts);
    }

    public static string MalformedJson()
    {
        return "{bad json";
    }

    public static string Call(int id, string tool, object args)
    {
        var argsNode = JsonNode.Parse(JsonSerializer.Serialize(args, Opts))!;
        var msg = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = tool,
                ["arguments"] = argsNode
            }
        };
        return msg.ToJsonString();
    }

    // ── Common session helpers ─────────────────────────────────────────────────

    /// <summary>Returns [initialize, notifications/initialized] header lines.</summary>
    public static IEnumerable<string> SessionHeader(int initId = 1)
    {
        return [Initialize(initId), Initialized()];
    }

    /// <summary>Builds a full session: header + body lines, runs it, returns session.</summary>
    public static Task<McpSession> SessionAsync(McpToolContext ctx, params string[] body)
    {
        return RunAsync(ctx, [.. SessionHeader(), .. body]);
    }
}

// ── EmbedderFixture ────────────────────────────────────────────────────────────

/// <summary>
///     Shared embedder — loaded once per test collection to amortize the ~90 MB ONNX model load.
/// </summary>
public sealed class EmbedderFixture : IAsyncLifetime
{
    public DefaultEmbeddingProvider Embedder { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Embedder = await DefaultEmbeddingProvider.CreateAsync();
    }

    public ValueTask DisposeAsync()
    {
        Embedder.Dispose();
        return ValueTask.CompletedTask;
    }
}

// ── PalaceFactory ──────────────────────────────────────────────────────────────

/// <summary>
///     Creates isolated temp palace directories for each test.
///     Call <see cref="CreateContext" /> to get a fresh McpToolContext.
///     Dispose removes the temp directory.
/// </summary>
public sealed class PalaceFactory(IEmbeddingGenerator<string, Embedding<float>> embedder) : IDisposable
{
    private readonly List<string> _dirs = [];

    public void Dispose()
    {
        foreach (var d in _dirs)
            try
            {
                Directory.Delete(d, true);
            }
            catch
            {
            }
    }

    public (McpToolContext ctx, string palacePath) CreateContext(
        VectorBackend backend = VectorBackend.Sqlite)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mp_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        var ctx = new McpToolContext(
            dir,
            Constants.DefaultCollectionName,
            Path.Combine(dir, "kg.db"),
            embedder,
            backend);
        return (ctx, dir);
    }
}

// ── Seed helpers ───────────────────────────────────────────────────────────────

public static class Seed
{
    public static IEnumerable<string> Drawers(int startId = 2)
    {
        return
        [
            McpHarness.Call(startId, "mempalace_add_drawer",
                new
                {
                    wing = "backend", room = "auth",
                    content =
                        "We decided to use JWT tokens with RS256 signing because it allows stateless verification across microservices. The secret is stored in Vault.",
                    added_by = "regression"
                }),
            McpHarness.Call(startId + 1, "mempalace_add_drawer",
                new
                {
                    wing = "backend", room = "auth",
                    content =
                        "Critical bug: auth middleware does not validate token expiry on refresh endpoint. Causes silent session extension. Fix: add exp claim check in RefreshTokenHandler.",
                    added_by = "regression"
                }),
            McpHarness.Call(startId + 2, "mempalace_add_drawer",
                new
                {
                    wing = "backend", room = "database",
                    content =
                        "We migrated from MySQL to PostgreSQL because of superior JSON query support and better connection pooling. Migration completed 2024-03. Riley owned the migration.",
                    added_by = "regression"
                }),
            McpHarness.Call(startId + 3, "mempalace_add_drawer",
                new
                {
                    wing = "frontend", room = "auth",
                    content =
                        "Frontend auth flow uses PKCE. The access token is stored in memory only — never localStorage — to mitigate XSS risk. Preference: always use secure, httpOnly cookies for refresh tokens.",
                    added_by = "regression"
                }),
            McpHarness.Call(startId + 4, "mempalace_add_drawer",
                new
                {
                    wing = "frontend", room = "components",
                    content =
                        "We decided to migrate from class components to hooks because the team prefers functional style and hooks compose better. Sam led the migration milestone.",
                    added_by = "regression"
                }),
            McpHarness.Call(startId + 5, "mempalace_add_drawer",
                new
                {
                    wing = "backend", room = "auth",
                    content =
                        "Milestone: deployed zero-downtime auth v2 to production on 2024-06-15. Involved Riley, Sam, and Jordan.",
                    added_by = "regression"
                })
        ];
    }

    public static IEnumerable<string> KgTriples(int startId = 10)
    {
        return
        [
            McpHarness.Call(startId, "mempalace_kg_add",
                new { subject = "Riley", predicate = "owns", @object = "auth-service", confidence = 1.0 }),
            McpHarness.Call(startId + 1, "mempalace_kg_add",
                new { subject = "Sam", predicate = "owns", @object = "frontend", confidence = 1.0 }),
            McpHarness.Call(startId + 2, "mempalace_kg_add",
                new
                {
                    subject = "auth-service", predicate = "depends_on", @object = "postgresql",
                    valid_from = "2024-03-01", confidence = 0.95
                }),
            McpHarness.Call(startId + 3, "mempalace_kg_add",
                new
                {
                    subject = "auth-service", predicate = "uses", @object = "jwt", valid_from = "2024-01-01",
                    confidence = 1.0
                }),
            McpHarness.Call(startId + 4, "mempalace_kg_add",
                new
                {
                    subject = "auth-service", predicate = "uses", @object = "mysql", valid_from = "2023-01-01",
                    valid_to = "2024-03-01", confidence = 1.0
                })
        ];
    }

    /// <summary>Runs seeding session; returns the first drawer_id added.</summary>
    public static async Task<string> ApplyAsync(McpToolContext ctx)
    {
        var session = await McpHarness.RunAsync(ctx, [
            .. McpHarness.SessionHeader(),
            .. Drawers(),
            .. KgTriples()
        ]);
        return session.Result(2)["drawer_id"]!.GetValue<string>();
    }
}
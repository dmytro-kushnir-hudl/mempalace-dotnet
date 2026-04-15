using System.Text.Json.Serialization;

namespace Mempalace;

/// <summary>Source-gen context for camelCase MCP responses — avoids reflection at runtime.</summary>
[JsonSerializable(typeof(SearchResponse))]
[JsonSerializable(typeof(SearchResult))]
[JsonSerializable(typeof(KgTriple))]
[JsonSerializable(typeof(KgStats))]
[JsonSerializable(typeof(TunnelResult))]
[JsonSerializable(typeof(DetectedEntities))]
[JsonSerializable(typeof(DetectedEntity))]
[JsonSerializable(typeof(List<KgTriple>))]
[JsonSerializable(typeof(List<TunnelResult>))]
[JsonSerializable(typeof(Dictionary<string, int>))]
[JsonSerializable(typeof(Dictionary<string, Dictionary<string, int>>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class MempalaceJsonContext : JsonSerializerContext { }

/// <summary>Source-gen context for config deserialization — case-insensitive.</summary>
[JsonSerializable(typeof(MempalaceConfig))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class MempalaceConfigContext : JsonSerializerContext { }

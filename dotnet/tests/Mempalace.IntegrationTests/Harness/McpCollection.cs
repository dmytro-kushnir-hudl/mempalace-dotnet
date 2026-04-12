namespace Mempalace.IntegrationTests.Harness;

/// <summary>
///     xunit collection that shares the EmbedderFixture (one ONNX model load)
///     across all integration test classes.
/// </summary>
[CollectionDefinition("MCP")]
public sealed class McpCollection : ICollectionFixture<EmbedderFixture>;
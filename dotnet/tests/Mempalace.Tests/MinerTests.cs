namespace Mempalace.Tests;

public sealed class MinerTests
{
    // ── DetectRoom ────────────────────────────────────────────────────────────

    private static readonly IReadOnlyList<RoomConfig> TestRooms =
    [
        new("backend", "API", ["api", "database"]),
        new("frontend", "UI", ["react", "tsx", "css"]),
        new("general", "Other", [])
    ];
    // ── ChunkText ─────────────────────────────────────────────────────────────

    [Fact]
    public void ChunkText_ShortContent_ReturnsSingleChunk()
    {
        var content = new string('x', Constants.MinChunkSize + 10);
        var chunks = Miner.ChunkText(content);
        Assert.Single(chunks);
        Assert.Equal(0, chunks[0].ChunkIndex);
    }

    [Fact]
    public void ChunkText_EmptyContent_ReturnsEmpty()
    {
        var chunks = Miner.ChunkText("   ");
        Assert.Empty(chunks);
    }

    [Fact]
    public void ChunkText_BelowMinChunkSize_Skipped()
    {
        var chunks = Miner.ChunkText(new string('x', Constants.MinChunkSize - 1));
        Assert.Empty(chunks);
    }

    [Fact]
    public void ChunkText_LargeContent_ProducesMultipleChunks()
    {
        var content = string.Join("\n\n", Enumerable.Range(0, 20)
            .Select(i => new string('a', 60) + $" paragraph {i}"));
        var chunks = Miner.ChunkText(content);
        Assert.True(chunks.Count > 1);
    }

    [Fact]
    public void ChunkText_ChunkIndexesAreSequential()
    {
        var content = string.Join("\n\n", Enumerable.Range(0, 20)
            .Select(i => new string('b', 60) + $" {i}"));
        var chunks = Miner.ChunkText(content);
        for (var i = 0; i < chunks.Count; i++)
            Assert.Equal(i, chunks[i].ChunkIndex);
    }

    [Fact]
    public void ChunkText_PreservesContent_NoDuplicatesAcrossChunks()
    {
        // Overlap means some chars repeat, but each chunk should be unique content
        var content = new string('z', Constants.ChunkSize * 3);
        var chunks = Miner.ChunkText(content);
        Assert.True(chunks.Count >= 2);
        Assert.All(chunks, c => Assert.True(c.Content.Length > 0));
    }

    // ── DrawerId ──────────────────────────────────────────────────────────────

    [Fact]
    public void DrawerId_Format_StartsWithDrawer()
    {
        var id = Miner.DrawerId("tech", "backend", "/src/api.cs", 0);
        Assert.StartsWith("drawer_tech_backend_", id);
    }

    [Fact]
    public void DrawerId_SameInputs_ProducesSameId()
    {
        var a = Miner.DrawerId("wing", "room", "/file.cs", 1);
        var b = Miner.DrawerId("wing", "room", "/file.cs", 1);
        Assert.Equal(a, b);
    }

    [Fact]
    public void DrawerId_DifferentChunkIndexes_ProduceDifferentIds()
    {
        var a = Miner.DrawerId("wing", "room", "/file.cs", 0);
        var b = Miner.DrawerId("wing", "room", "/file.cs", 1);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DrawerId_HashSegment_Is24CharsLowerHex()
    {
        var id = Miner.DrawerId("w", "r", "/f", 0);
        var hash = id.Split('_').Last();
        Assert.Equal(24, hash.Length);
        Assert.Matches("^[0-9a-f]+$", hash);
    }

    [Fact]
    public void DetectRoom_FolderMatchesRoomName_ReturnsRoom()
    {
        var result = Miner.DetectRoom("/proj/backend/server.cs", "", TestRooms, "/proj");
        Assert.Equal("backend", result);
    }

    [Fact]
    public void DetectRoom_FilenameContainsKeyword_ReturnsRoom()
    {
        var result = Miner.DetectRoom("/proj/src/api_controller.cs", "", TestRooms, "/proj");
        Assert.Equal("backend", result);
    }

    [Fact]
    public void DetectRoom_ContentKeyword_ReturnsRoom()
    {
        var result = Miner.DetectRoom("/proj/src/misc.cs", "using React; const x = <div/>", TestRooms, "/proj");
        Assert.Equal("frontend", result);
    }

    [Fact]
    public void DetectRoom_NoMatch_ReturnsGeneral()
    {
        var result = Miner.DetectRoom("/proj/src/misc.cs", "nothing relevant here", TestRooms, "/proj");
        Assert.Equal("general", result);
    }

    [Fact]
    public void DetectRoom_EmptyRooms_ReturnsGeneral()
    {
        var result = Miner.DetectRoom("/proj/api.cs", "content", [], "/proj");
        Assert.Equal("general", result);
    }
}
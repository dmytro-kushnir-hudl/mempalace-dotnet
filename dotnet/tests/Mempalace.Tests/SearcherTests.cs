using Mempalace.Storage;

namespace Mempalace.Tests;

public sealed class SearcherTests
{
    // ── BuildFilter (replaces BuildWhere) ─────────────────────────────────────

    [Fact]
    public void BuildFilter_BothNull_ReturnsNull()
    {
        Assert.Null(Searcher.BuildFilter(null, null));
    }

    [Fact]
    public void BuildFilter_WingOnly_SingleClause()
    {
        var f = Searcher.BuildFilter("tech", null);
        Assert.NotNull(f);
        Assert.Equal("tech", f!.Clauses["wing"]?.ToString());
        Assert.Equal(1, f.Clauses.Count);
    }

    [Fact]
    public void BuildFilter_RoomOnly_SingleClause()
    {
        var f = Searcher.BuildFilter(null, "backend");
        Assert.NotNull(f);
        Assert.Equal("backend", f!.Clauses["room"]?.ToString());
        Assert.Equal(1, f.Clauses.Count);
    }

    [Fact]
    public void BuildFilter_BothSet_TwoClauses()
    {
        var f = Searcher.BuildFilter("tech", "backend");
        Assert.NotNull(f);
        Assert.Equal(2, f!.Clauses.Count);
        Assert.Equal("tech", f.Clauses["wing"]?.ToString());
        Assert.Equal("backend", f.Clauses["room"]?.ToString());
    }

    // ── MetadataFilter API ────────────────────────────────────────────────────

    [Fact]
    public void MetadataFilter_Where_SingleClause()
    {
        var f = MetadataFilter.Where("wing", "tech");
        Assert.Single(f.Clauses);
        Assert.Equal("tech", f.Clauses["wing"]);
    }

    [Fact]
    public void MetadataFilter_And_AddsClause()
    {
        var f = MetadataFilter.Where("wing", "tech").And("room", "backend");
        Assert.Equal(2, f.Clauses.Count);
        Assert.Equal("tech", f.Clauses["wing"]);
        Assert.Equal("backend", f.Clauses["room"]);
    }

    [Fact]
    public void MetadataFilter_And_IsChainable()
    {
        var f = MetadataFilter.Where("wing", "a").And("room", "b").And("added_by", "mcp");
        Assert.Equal(3, f.Clauses.Count);
    }
}
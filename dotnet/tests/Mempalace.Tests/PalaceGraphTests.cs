using System.Text.Json.Nodes;

namespace Mempalace.Tests;

public sealed class PalaceGraphTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Dictionary<string, object?> Meta(
        string room, string wing, string hall = "", string date = "") =>
        new()
        {
            ["room"] = room,
            ["wing"] = wing,
            ["hall"] = hall,
            ["date"] = date,
        };

    // ── BuildFromData — nodes ─────────────────────────────────────────────────

    [Fact]
    public void Build_EmptyInput_ReturnsEmptyGraph()
    {
        var (nodes, edges) = PalaceGraph.BuildFromData([]);
        Assert.Empty(nodes);
        Assert.Empty(edges);
    }

    [Fact]
    public void Build_SingleRoomSingleWing_CreatesNodeNoEdges()
    {
        var data = new[] { Meta("auth", "backend") };
        var (nodes, edges) = PalaceGraph.BuildFromData(data);

        Assert.Single(nodes);
        Assert.True(nodes.ContainsKey("auth"));
        Assert.Empty(edges);
    }

    [Fact]
    public void Build_GeneralRoomSkipped()
    {
        var data = new[] { Meta("general", "backend"), Meta("auth", "backend") };
        var (nodes, _) = PalaceGraph.BuildFromData(data);

        Assert.DoesNotContain("general", nodes.Keys);
        Assert.Single(nodes);
    }

    [Fact]
    public void Build_NullOrEmptyRoomSkipped()
    {
        var data = new[]
        {
            new Dictionary<string, object?> { ["room"] = null, ["wing"] = "w" },
            new Dictionary<string, object?> { ["room"] = "",   ["wing"] = "w" },
        };
        var (nodes, _) = PalaceGraph.BuildFromData(data);
        Assert.Empty(nodes);
    }

    [Fact]
    public void Build_NullOrEmptyWingSkipped()
    {
        var data = new[]
        {
            new Dictionary<string, object?> { ["room"] = "auth", ["wing"] = null },
            new Dictionary<string, object?> { ["room"] = "auth", ["wing"] = "" },
        };
        var (nodes, _) = PalaceGraph.BuildFromData(data);
        Assert.Empty(nodes);
    }

    [Fact]
    public void Build_NullMetadataEntrySkipped()
    {
        var data = new Dictionary<string, object?>?[] { null, Meta("auth", "backend") };
        var (nodes, _) = PalaceGraph.BuildFromData(data);
        Assert.Single(nodes);
    }

    [Fact]
    public void Build_CountIncrementsPerEntry()
    {
        var data = new[] { Meta("auth", "backend"), Meta("auth", "backend") };
        var (nodes, _) = PalaceGraph.BuildFromData(data);
        Assert.Equal(2, nodes["auth"].Count);
    }

    [Fact]
    public void Build_WingsDeduplicatedAndSorted()
    {
        var data = new[]
        {
            Meta("auth", "frontend"),
            Meta("auth", "backend"),
            Meta("auth", "frontend"),
        };
        var (nodes, _) = PalaceGraph.BuildFromData(data);
        var wings = nodes["auth"].Wings;
        Assert.Equal(2, wings.Count);
        Assert.Equal(["backend", "frontend"], wings);
    }

    [Fact]
    public void Build_HallsDeduplicatedAndSorted()
    {
        var data = new[]
        {
            Meta("auth", "backend", hall: "security"),
            Meta("auth", "frontend", hall: "ux"),
            Meta("auth", "backend", hall: "security"),
        };
        var (nodes, _) = PalaceGraph.BuildFromData(data);
        var halls = nodes["auth"].Halls;
        Assert.Equal(2, halls.Count);
        Assert.Equal(["security", "ux"], halls);
    }

    [Fact]
    public void Build_EmptyHallNotAdded()
    {
        var data = new[] { Meta("auth", "backend", hall: "") };
        var (nodes, _) = PalaceGraph.BuildFromData(data);
        Assert.Empty(nodes["auth"].Halls);
    }

    [Fact]
    public void Build_RecentDatesTop5Descending()
    {
        var dates = new[] { "2024-01", "2024-05", "2023-12", "2024-03", "2024-02", "2024-04" };
        var data = dates.Select(d => Meta("auth", "backend", date: d)).ToArray();
        var (nodes, _) = PalaceGraph.BuildFromData(data);
        var recent = nodes["auth"].RecentDates;
        Assert.Equal(5, recent.Count);
        // Most recent first
        Assert.Equal("2024-05", recent[0]);
    }

    // ── BuildFromData — edges ─────────────────────────────────────────────────

    [Fact]
    public void Build_RoomInTwoWings_CreatesOneEdge()
    {
        var data = new[] { Meta("auth", "backend"), Meta("auth", "frontend") };
        var (_, edges) = PalaceGraph.BuildFromData(data);
        Assert.Single(edges);
        var e = edges[0];
        Assert.Equal("auth", e.Room);
        Assert.Equal("backend", e.WingA);
        Assert.Equal("frontend", e.WingB);
    }

    [Fact]
    public void Build_RoomInThreeWings_CreatesThreeEdges()
    {
        var data = new[]
        {
            Meta("auth", "backend"),
            Meta("auth", "frontend"),
            Meta("auth", "mobile"),
        };
        var (_, edges) = PalaceGraph.BuildFromData(data);
        // C(3,2) = 3 pairs × 1 hall (empty) = 3 edges
        Assert.Equal(3, edges.Count);
    }

    [Fact]
    public void Build_MultipleHalls_ProducesEdgePerHall()
    {
        var data = new[]
        {
            Meta("auth", "backend",  hall: "sec"),
            Meta("auth", "frontend", hall: "ux"),
        };
        var (_, edges) = PalaceGraph.BuildFromData(data);
        // 1 wing pair × 2 halls = 2 edges
        Assert.Equal(2, edges.Count);
    }

    // ── FindTunnels ────────────────────────────────────────────────────────────

    private static IEnumerable<Dictionary<string, object?>> TunnelData() =>
    [
        Meta("auth",    "backend"),  Meta("auth",    "frontend"),
        Meta("logging", "backend"),  Meta("logging", "infra"),
        Meta("ui-kit",  "frontend"),                              // single-wing — not a tunnel
    ];

    [Fact]
    public void FindTunnels_NoFilter_ReturnsOnlyMultiWingRooms()
    {
        var tunnels = PalaceGraph.FindTunnels(TunnelData());
        var rooms = tunnels.Select(t => t.Room).ToHashSet();
        Assert.Contains("auth", rooms);
        Assert.Contains("logging", rooms);
        Assert.DoesNotContain("ui-kit", rooms);
    }

    [Fact]
    public void FindTunnels_WingAFilter_RestrictsResults()
    {
        var tunnels = PalaceGraph.FindTunnels(TunnelData(), wingA: "frontend");
        Assert.All(tunnels, t => Assert.Contains("frontend", t.Wings));
        // logging is backend+infra — should not appear
        Assert.DoesNotContain("logging", tunnels.Select(t => t.Room));
    }

    [Fact]
    public void FindTunnels_BothWingsFilter_RequiresBoth()
    {
        var tunnels = PalaceGraph.FindTunnels(TunnelData(), wingA: "backend", wingB: "infra");
        Assert.Single(tunnels);
        Assert.Equal("logging", tunnels[0].Room);
    }

    [Fact]
    public void FindTunnels_NoMatch_ReturnsEmpty()
    {
        var tunnels = PalaceGraph.FindTunnels(TunnelData(), wingA: "nonexistent");
        Assert.Empty(tunnels);
    }

    [Fact]
    public void FindTunnels_SortedByCountDescending()
    {
        var data = new[]
        {
            Meta("a", "w1"), Meta("a", "w2"),
            Meta("b", "w1"), Meta("b", "w2"), Meta("b", "w3"),
            Meta("b", "w1"), // extra count for b
        };
        var tunnels = PalaceGraph.FindTunnels(data);
        Assert.Equal("b", tunnels[0].Room);
    }

    // ── GraphStats ────────────────────────────────────────────────────────────

    [Fact]
    public void GraphStats_Empty_AllZero()
    {
        var stats = PalaceGraph.GraphStats(Array.Empty<Dictionary<string, object?>>());
        Assert.Equal(0, stats.TotalRooms);
        Assert.Equal(0, stats.TunnelRooms);
        Assert.Equal(0, stats.TotalEdges);
        Assert.Empty(stats.RoomsPerWing);
        Assert.Empty(stats.TopTunnels);
    }

    [Fact]
    public void GraphStats_CountsTunnelRoomsAndEdges()
    {
        var data = TunnelData().ToArray();
        var stats = PalaceGraph.GraphStats(data);
        Assert.Equal(3, stats.TotalRooms);
        Assert.Equal(2, stats.TunnelRooms);
        Assert.Equal(2, stats.TotalEdges); // auth: 1 edge, logging: 1 edge
    }

    [Fact]
    public void GraphStats_RoomsPerWingCounted()
    {
        var data = TunnelData().ToArray();
        var stats = PalaceGraph.GraphStats(data);
        Assert.Equal(2, stats.RoomsPerWing["backend"]); // auth + logging
        Assert.Equal(2, stats.RoomsPerWing["frontend"]); // auth + ui-kit
        Assert.Equal(1, stats.RoomsPerWing["infra"]);
    }

    // ── Traverse ──────────────────────────────────────────────────────────────

    [Fact]
    public void Traverse_UnknownRoom_ReturnsErrorWithSuggestions()
    {
        var data = TunnelData();
        var result = PalaceGraph.Traverse(data, "aut"); // typo
        var obj = result.AsObject();
        Assert.NotNull(obj["error"]);
        var suggestions = obj["suggestions"]?.AsArray();
        Assert.NotNull(suggestions);
        Assert.True(suggestions!.Count > 0);
        Assert.Contains(suggestions, s => s?.GetValue<string>() == "auth");
    }

    [Fact]
    public void Traverse_StartRoom_AppearsAtHopZero()
    {
        var data = TunnelData();
        var result = PalaceGraph.Traverse(data, "auth");
        var arr = result.AsArray();
        var first = arr[0]!.AsObject();
        Assert.Equal("auth", first["room"]?.GetValue<string>());
        Assert.Equal(0, first["hop"]?.GetValue<int>());
    }

    [Fact]
    public void Traverse_MaxHopsZero_OnlyReturnsStartRoom()
    {
        var data = TunnelData();
        var result = PalaceGraph.Traverse(data, "auth", maxHops: 0);
        var arr = result.AsArray();
        Assert.Single(arr);
    }

    [Fact]
    public void Traverse_SharedWing_ReturnsNeighbourAtHop1()
    {
        var data = new[]
        {
            Meta("auth",    "backend"),
            Meta("auth",    "frontend"),
            Meta("logging", "backend"),  // shares "backend" with auth
        };
        var result = PalaceGraph.Traverse(data, "auth", maxHops: 1);
        var arr = result.AsArray();
        var rooms = arr.Select(n => n!["room"]?.GetValue<string>()).ToHashSet();
        Assert.Contains("logging", rooms);
    }

    [Fact]
    public void Traverse_ResultsSortedByHopThenCountDesc()
    {
        var data = new[]
        {
            Meta("auth",    "backend"),
            Meta("auth",    "frontend"),
            Meta("logging", "backend"),
            Meta("logging", "backend"), // count=2
            Meta("metrics", "backend"),  // count=1
        };
        var result = PalaceGraph.Traverse(data, "auth", maxHops: 1);
        var arr = result.AsArray();
        // hop-1 entries: logging (count=2) before metrics (count=1)
        var hop1 = arr.Skip(1).Select(n => n!["room"]?.GetValue<string>()).ToList();
        Assert.Equal("logging", hop1[0]);
        Assert.Equal("metrics", hop1[1]);
    }
}

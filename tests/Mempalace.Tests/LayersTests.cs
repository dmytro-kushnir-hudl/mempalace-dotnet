namespace Mempalace.Tests;

public sealed class LayersTests
{
    // ── Layer0 ────────────────────────────────────────────────────────────────

    [Fact]
    public void Layer0_MissingIdentityFile_ReturnsEmpty()
    {
        var l0 = new Layer0(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt"));
        Assert.Equal("", l0.Render());
    }

    [Fact]
    public void Layer0_ExistingFile_ReturnsContent()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "  I am an AI assistant.  ");
            var l0 = new Layer0(path);
            Assert.Equal("I am an AI assistant.", l0.Render());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Layer0_CachesContent_SameInstanceReturnsSameValue()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "identity");
            var l0 = new Layer0(path);
            var first  = l0.Render();
            File.WriteAllText(path, "changed");   // file changes after first load
            var second = l0.Render();
            Assert.Equal(first, second);           // cache wins
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Layer0_TokenEstimate_IsLengthDividedByFour()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, new string('x', 400));
            var l0 = new Layer0(path);
            Assert.Equal(100, l0.TokenEstimate());
        }
        finally { File.Delete(path); }
    }

    // ── Layer1 — requires no palace (empty palace returns empty) ─────────────

    [Fact]
    public void Layer1_EmptyPalace_ReturnsEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var l1 = new Layer1(path);
            Assert.Equal("", l1.Generate());
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    // ── Layer2 — no palace ────────────────────────────────────────────────────

    [Fact]
    public void Layer2_NonExistentPalace_ReturnsEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var l2   = new Layer2(path);
        Assert.Equal("", l2.Retrieve());
    }

    // ── MemoryStack ───────────────────────────────────────────────────────────

    [Fact]
    public void MemoryStack_WakeUp_WithIdentity_IncludesIdentitySection()
    {
        var idPath = Path.GetTempFileName();
        var dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dbPath);
        try
        {
            File.WriteAllText(idPath, "I am an assistant.");
            var stack  = new MemoryStack(dbPath, idPath);
            var output = stack.WakeUp();
            Assert.Contains("I am an assistant.", output);
        }
        finally
        {
            File.Delete(idPath);
            Directory.Delete(dbPath, recursive: true);
        }
    }

    [Fact]
    public void MemoryStack_Recall_NonExistentPalace_ReturnsEmpty()
    {
        var stack = new MemoryStack(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Assert.Equal("", stack.Recall());
    }
}

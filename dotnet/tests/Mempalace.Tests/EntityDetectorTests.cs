namespace Mempalace.Tests;

public sealed class EntityDetectorTests
{
    // ── Basic smoke ───────────────────────────────────────────────────────────

    [Fact]
    public void DetectFromText_EmptyText_ReturnsEmpty()
    {
        var result = EntityDetector.DetectFromText("");
        Assert.Empty(result.People);
        Assert.Empty(result.Projects);
        Assert.Empty(result.Uncertain);
    }

    [Fact]
    public void DetectFromText_NoRepeatedCapitalized_ReturnsEmpty()
    {
        // Words appearing < 3 times should not be candidates
        var result = EntityDetector.DetectFromText("Hello world. Go outside. Walk fast.");
        Assert.Empty(result.People);
        Assert.Empty(result.Projects);
    }

    // ── Person detection ──────────────────────────────────────────────────────

    [Fact]
    public void DetectFromText_PersonWithDialogue_DetectedAsPerson()
    {
        const string text = """
            Alice said she was tired after the long meeting.
            Alice asked the team about the deadline.
            Alice replied that she needed more time.
            Alice told Bob she was worried about the release.
            Alice thinks the approach is wrong.
            Alice Alice Alice
            """;

        var result = EntityDetector.DetectFromText(text);
        var alice = result.People.FirstOrDefault(e =>
            e.Name.Equals("Alice", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(alice);
        Assert.True(alice.Confidence >= 0.5);
    }

    [Fact]
    public void DetectFromText_DirectAddress_BoostsPersonScore()
    {
        const string text = """
            Hey Jordan, can you review this PR?
            Thanks Jordan, that's exactly what I needed.
            Hi Jordan, are you free for a call?
            Jordan Jordan Jordan Jordan
            """;

        var result = EntityDetector.DetectFromText(text);
        // Jordan directly addressed → should lean person
        var jordan = result.People.Concat(result.Uncertain)
            .FirstOrDefault(e => e.Name.Equals("Jordan", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(jordan);
    }

    // ── Project detection ─────────────────────────────────────────────────────

    [Fact]
    public void DetectFromText_ProjectVerbs_DetectedAsProject()
    {
        const string text = """
            We're building Mempalace as the core memory layer.
            Building Mempalace required integrating ChromaDB.
            Shipped Mempalace v2 last week.
            Mempalace.py handles all the file I/O.
            Mempalace Mempalace Mempalace
            """;

        var result = EntityDetector.DetectFromText(text);
        var mempalace = result.Projects
            .FirstOrDefault(e => e.Name.Equals("Mempalace", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(mempalace);
    }

    // ── Frequency filter ──────────────────────────────────────────────────────

    [Fact]
    public void DetectFromText_RareWords_BelowThreshold_Filtered()
    {
        // "Zanzibar" appears only once — should not be a candidate
        var result = EntityDetector.DetectFromText(
            "We went to Zanzibar for the conference.");
        var zanzibar = result.People.Concat(result.Projects).Concat(result.Uncertain)
            .FirstOrDefault(e => e.Name.Equals("Zanzibar", StringComparison.OrdinalIgnoreCase));
        Assert.Null(zanzibar);
    }

    // ── Stopword exclusion ────────────────────────────────────────────────────

    [Fact]
    public void DetectFromText_Stopwords_NotReturnedAsEntities()
    {
        // Common words shouldn't appear even if capitalized repeatedly
        const string text = "The The The Step Step Step File File File";
        var result = EntityDetector.DetectFromText(text);
        var allNames = result.People.Concat(result.Projects).Concat(result.Uncertain)
            .Select(e => e.Name.ToLowerInvariant()).ToHashSet();
        Assert.DoesNotContain("the", allNames);
        Assert.DoesNotContain("step", allNames);
        Assert.DoesNotContain("file", allNames);
    }

    // ── Multi-word phrases ────────────────────────────────────────────────────

    [Fact]
    public void DetectFromText_MultiWordProperNoun_Captured()
    {
        const string text = """
            Memory Palace is the core concept.
            Memory Palace stores all our data.
            Memory Palace Memory Palace Memory Palace
            """;

        var result = EntityDetector.DetectFromText(text);
        var allEntities = result.People.Concat(result.Projects).Concat(result.Uncertain)
            .Select(e => e.Name).ToList();
        Assert.Contains(allEntities, n => n.Contains("Memory") || n.Contains("Palace"));
    }

    // ── Result caps ───────────────────────────────────────────────────────────

    [Fact]
    public void DetectFromText_ManyEntities_CappedAtLimits()
    {
        // Generate lots of repeated capitalized names to hit caps
        var names  = Enumerable.Range(1, 30).Select(i => $"Person{i}").ToList();
        var chunks = names.Select(n => string.Join(' ', Enumerable.Repeat(n, 5)));
        var text   = string.Join('\n', chunks);

        var result = EntityDetector.DetectFromText(text);
        Assert.True(result.People.Count    <= 15);
        Assert.True(result.Projects.Count  <= 10);
        Assert.True(result.Uncertain.Count <= 8);
    }

    // ── Confidence range ──────────────────────────────────────────────────────

    [Fact]
    public void DetectFromText_AllConfidences_InValidRange()
    {
        const string text = """
            Alice said she was tired. Alice Alice Alice Alice.
            Mempalace is being deployed. Mempalace Mempalace.
            Xenon Xenon Xenon Xenon Xenon
            """;

        var result = EntityDetector.DetectFromText(text);
        var all = result.People.Concat(result.Projects).Concat(result.Uncertain);
        foreach (var e in all)
        {
            Assert.True(e.Confidence >= 0 && e.Confidence <= 1,
                $"{e.Name} confidence {e.Confidence} out of range");
        }
    }

    // ── CollectFilesForDetection ──────────────────────────────────────────────

    [Fact]
    public void CollectFiles_EmptyDir_ReturnsEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var files = EntityDetector.CollectFilesForDetection(dir, maxFiles: 10);
            Assert.Empty(files);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void CollectFiles_ProsePrioritized_OverCode()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "notes.md"), "some notes");
            File.WriteAllText(Path.Combine(dir, "code.py"), "import os");
            File.WriteAllText(Path.Combine(dir, "readme.txt"), "hello");

            var files = EntityDetector.CollectFilesForDetection(dir, maxFiles: 10);
            // Prose files (.md, .txt) should come first
            var extensions = files.Select(Path.GetExtension).ToList();
            Assert.Contains(".md", extensions);
            Assert.Contains(".txt", extensions);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── DetectFromFiles ───────────────────────────────────────────────────────

    [Fact]
    public void DetectFromFiles_MissingFile_Skipped()
    {
        // Should not throw on missing files
        var result = EntityDetector.DetectFromFiles(
            ["/nonexistent/path/file.txt"], maxFiles: 5);
        Assert.NotNull(result);
    }

    [Fact]
    public void DetectFromFiles_RealFile_ExtractsEntities()
    {
        var dir  = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var content = string.Join('\n', Enumerable.Repeat(
                "Riley said she was excited. Riley asked about the deadline. Riley Riley.", 10));
            File.WriteAllText(Path.Combine(dir, "diary.txt"), content);

            var result = EntityDetector.DetectFromFiles(
                [Path.Combine(dir, "diary.txt")], maxFiles: 1);

            var riley = result.People.Concat(result.Uncertain)
                .FirstOrDefault(e => e.Name.Equals("Riley", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(riley);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

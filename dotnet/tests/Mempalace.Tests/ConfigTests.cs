namespace Mempalace.Tests;

public sealed class ConfigTests
{
    // ── SanitizeName ─────────────────────────────────────────────────────────

    [Fact] public void SanitizeName_Valid_ReturnsValue() =>
        Assert.Equal("my_wing", Sanitizer.SanitizeName("my_wing"));

    [Fact] public void SanitizeName_Empty_Throws() =>
        Assert.Throws<ArgumentException>(() => Sanitizer.SanitizeName(""));

    [Fact] public void SanitizeName_NullByte_Throws() =>
        Assert.Throws<ArgumentException>(() => Sanitizer.SanitizeName("bad\0name"));

    [Fact] public void SanitizeName_PathTraversal_Throws() =>
        Assert.Throws<ArgumentException>(() => Sanitizer.SanitizeName("../../etc"));

    [Fact] public void SanitizeName_TooLong_Throws() =>
        Assert.Throws<ArgumentException>(() => Sanitizer.SanitizeName(new string('a', 200)));

    // ── SanitizeContent ───────────────────────────────────────────────────────

    [Fact] public void SanitizeContent_Valid_ReturnsValue() =>
        Assert.Equal("hello", Sanitizer.SanitizeContent("hello"));

    [Fact] public void SanitizeContent_Empty_Throws() =>
        Assert.Throws<ArgumentException>(() => Sanitizer.SanitizeContent(""));

    [Fact] public void SanitizeContent_TooLong_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            Sanitizer.SanitizeContent(new string('x', Constants.MaxContentLength + 1)));

    // ── ProjectConfig ─────────────────────────────────────────────────────────

    [Fact]
    public void ProjectConfig_TryLoad_NonExistentDir_ReturnsNull()
    {
        var result = ProjectConfig.TryLoad(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Assert.Null(result);
    }

    [Fact]
    public void ProjectConfig_TryLoad_ValidYaml_ParsesWingAndRooms()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "mempalace.yaml"), """
                wing: my_project
                rooms:
                  - name: backend
                    description: API layer
                    keywords: [api, database]
                  - name: frontend
                    keywords: [react, tsx]
                """);

            var config = ProjectConfig.TryLoad(dir);
            Assert.NotNull(config);
            Assert.Equal("my_project", config!.Wing);
            Assert.Equal(2, config.Rooms.Count);
            Assert.Equal("backend", config.Rooms[0].Name);
            Assert.Contains("api", config.Rooms[0].Keywords);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── Constants ─────────────────────────────────────────────────────────────

    [Fact] public void SkipDirs_ContainsNodeModules() =>
        Assert.Contains("node_modules", Constants.SkipDirs);

    [Fact] public void ReadableExtensions_ContainsCSharp() =>
        Assert.Contains(".cs", Constants.ReadableExtensions);
}

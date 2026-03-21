namespace ZLogger.Scintilla.Tests;

public sealed class SolutionStructureTests
{
    [Fact]
    public void ArchitectureDocument_MustDefineInPlaceRewriteBoundaries()
    {
        var architecturePath = GetRepoFilePath("docs/architecture.md");
        Assert.True(File.Exists(architecturePath), "docs/architecture.md must exist.");

        var content = File.ReadAllText(architecturePath);

        Assert.Contains("IScintillaTheme", content);
        Assert.Contains("ILogColorizer", content);
        Assert.Contains("ILinkifier", content);
        Assert.Contains("ArrayPool everywhere", content);
    }

    [Fact]
    public void RequiredDocsExist()
    {
        var requiredDocs = new[]
        {
            "docs/architecture.md",
            "docs/usage.md",
            "docs/benchmarks.md",
            "docs/testing.md",
            "README.md"
        };

        foreach (var doc in requiredDocs)
        {
            var path = GetRepoFilePath(doc);
            Assert.True(File.Exists(path), $"Required doc missing: {doc}");
        }
    }

    [Fact]
    public void MainProject_HasExpectedFolderStructure()
    {
        var expectedFolders = new[]
        {
            "ZLogger.Scintilla/Models",
            "ZLogger.Scintilla/Pipeline",
            "ZLogger.Scintilla/Public",
            "ZLogger.Scintilla/Rendering",
            "ZLogger.Scintilla/Services"
        };

        foreach (var folder in expectedFolders)
        {
            var path = GetRepoFilePath(folder);
            Assert.True(Directory.Exists(path), $"Expected folder missing: {folder}");
        }
    }

    [Fact]
    public void MainProject_HasMinimumSourceFiles()
    {
        var srcPath = GetRepoFilePath("ZLogger.Scintilla");
        var csFiles = Directory.GetFiles(srcPath, "*.cs", SearchOption.AllDirectories);
        Assert.True(csFiles.Length >= 15,
            $"Expected at least 15 .cs files in ZLogger.Scintilla, found {csFiles.Length}");
    }

    private static string GetRepoFilePath(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ZLogger.Scintilla.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
            throw new DirectoryNotFoundException("Unable to locate repository root from test output directory.");

        return Path.Combine(directory.FullName, relativePath);
    }
}

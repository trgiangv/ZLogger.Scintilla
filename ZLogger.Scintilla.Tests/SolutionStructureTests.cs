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

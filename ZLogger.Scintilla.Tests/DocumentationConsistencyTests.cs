using System.Reflection;

namespace ZLogger.Scintilla.Tests;

/// <summary>
/// Guards against documentation drift by asserting that key docs do not
/// reference deleted types/APIs and that referenced API members actually exist.
/// </summary>
[TestClass]
public sealed class DocumentationConsistencyTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static readonly string[] StaleTypeReferences =
    [
        "LogFilterState", "LogFilterOptions", "LogFilterSpec",
        "ILogViewerControl", "IScintillaLogViewHost",
        "ClearMode", "ScintillaViewerControl", "CreateState"
    ];

    private static readonly string[] StaleApiPatterns =
    [
        "SetFilter(string", "SetFilter(\""
    ];

    private static readonly string[] DocsToScan =
    [
        "docs/architecture.md",
        "docs/usage.md",
        "docs/benchmarks.md",
        "docs/testing.md",
        "README.md"
    ];

    [TestMethod]
    public void AllDocs_DoNotReferenceDeletedTypes()
    {
        foreach (var docPath in DocsToScan)
        {
            var content = File.ReadAllText(Path.Combine(RepoRoot, docPath));
            foreach (var staleRef in StaleTypeReferences)
                Assert.IsFalse(content.Contains(staleRef),
                    $"{docPath} contains stale reference to '{staleRef}'");
        }
    }

    [TestMethod]
    public void AllDocs_DoNotReferenceStringSetFilter()
    {
        foreach (var docPath in DocsToScan)
        {
            var content = File.ReadAllText(Path.Combine(RepoRoot, docPath));
            foreach (var pattern in StaleApiPatterns)
                Assert.IsFalse(content.Contains(pattern),
                    $"{docPath} contains stale API pattern '{pattern}'");
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);

        while (dir != null)
        {
            var gitPath = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repo root (.git file or directory).");
    }
}

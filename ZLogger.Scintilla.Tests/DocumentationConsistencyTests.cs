using System.Reflection;
using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Public;

namespace ZLogger.Scintilla.Tests;

/// <summary>
/// Guards against documentation drift by asserting that key docs do not
/// reference deleted types/APIs and that referenced API members actually exist.
/// </summary>
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

    // ── All docs: no stale type references ──────────────────────────────────

    [Fact]
    public void AllDocs_DoNotReferenceDeletedTypes()
    {
        foreach (var docPath in DocsToScan)
        {
            var fullPath = Path.Combine(RepoRoot, docPath);
            if (!File.Exists(fullPath)) continue;

            var content = File.ReadAllText(fullPath);
            foreach (var staleRef in StaleTypeReferences)
                Assert.False(content.Contains(staleRef),
                    $"{docPath} contains stale reference to '{staleRef}'");
        }
    }

    [Fact]
    public void AllDocs_DoNotReferenceStringSetFilter()
    {
        foreach (var docPath in DocsToScan)
        {
            var fullPath = Path.Combine(RepoRoot, docPath);
            if (!File.Exists(fullPath)) continue;

            var content = File.ReadAllText(fullPath);
            foreach (var pattern in StaleApiPatterns)
                Assert.False(content.Contains(pattern),
                    $"{docPath} contains stale API pattern '{pattern}'");
        }
    }

    // ── API existence checks ─────────────────────────────────────────────────

    [Fact]
    public void ScintillaLogViewerBase_SetFilter_AcceptsLogLevel()
    {
        var method = typeof(ScintillaLogViewerBase).GetMethod(
            nameof(ScintillaLogViewerBase.SetFilter),
            [typeof(LogLevel)]);
        Assert.NotNull(method);
    }

    [Fact]
    public void ScintillaLogViewerBase_FindNextAsync_Exists()
    {
        var method = typeof(ScintillaLogViewerBase).GetMethod(
            nameof(ScintillaLogViewerBase.FindNextAsync),
            [typeof(System.Threading.CancellationToken)]);
        Assert.NotNull(method);
    }

    [Fact]
    public void ScintillaLogViewerBase_FindPreviousAsync_Exists()
    {
        var method = typeof(ScintillaLogViewerBase).GetMethod(
            nameof(ScintillaLogViewerBase.FindPreviousAsync),
            [typeof(System.Threading.CancellationToken)]);
        Assert.NotNull(method);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string ReadDoc(string relativePath)
    {
        var path = Path.Combine(RepoRoot, relativePath);
        Assert.True(File.Exists(path), $"Doc not found: {path}");
        return File.ReadAllText(path);
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

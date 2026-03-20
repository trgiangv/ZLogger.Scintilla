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

    // ── docs/architecture.md ─────────────────────────────────────────────────

    [Fact]
    public void ArchitectureMd_DoesNotReferenceDeletedFilterState()
    {
        var content = ReadDoc("docs/architecture.md");
        Assert.DoesNotContain("LogFilterState", content);
    }

    [Fact]
    public void ArchitectureMd_DoesNotReferenceDeletedFilterOptions()
    {
        var content = ReadDoc("docs/architecture.md");
        Assert.DoesNotContain("LogFilterOptions", content);
    }

    [Fact]
    public void ArchitectureMd_DoesNotReferenceDeletedFilterSpec()
    {
        var content = ReadDoc("docs/architecture.md");
        Assert.DoesNotContain("LogFilterSpec", content);
    }

    [Fact]
    public void ArchitectureMd_DoesNotReferenceStringSetFilter()
    {
        var content = ReadDoc("docs/architecture.md");
        Assert.DoesNotContain("SetFilter(string", content);
        Assert.DoesNotContain("SetFilter(\"", content);
    }

    [Fact]
    public void ArchitectureMd_DoesNotReferenceCreateState()
    {
        var content = ReadDoc("docs/architecture.md");
        Assert.DoesNotContain("CreateState", content);
    }

    [Fact]
    public void ArchitectureMd_DoesNotReferenceDeletedTypes()
    {
        var content = ReadDoc("docs/architecture.md");
        Assert.DoesNotContain("ILogViewerControl", content);
        Assert.DoesNotContain("IScintillaLogViewHost", content);
        Assert.DoesNotContain("ClearMode", content);
        Assert.DoesNotContain("ScintillaViewerControl", content);
    }

    // ── README.md ────────────────────────────────────────────────────────────

    [Fact]
    public void ReadmeMd_DoesNotReferenceStringSetFilter()
    {
        var content = ReadDoc("README.md");
        Assert.DoesNotContain("SetFilter(\"", content);
        Assert.DoesNotContain("SetFilter(string", content);
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

using System.Reflection;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Pipeline;
using ZLogger.Scintilla.Public;

namespace ZLogger.Scintilla.Tests;

public sealed class AsyncSearchTests
{
    [Fact]
    public void ScintillaLogViewerBase_MustExposeAsyncSearchNavigation()
    {
        var methods = typeof(ScintillaLogViewerBase)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance);

        var findNextAsync = methods.SingleOrDefault(m =>
            m.Name == nameof(ScintillaLogViewerBase.FindNextAsync));
        var findPreviousAsync = methods.SingleOrDefault(m =>
            m.Name == nameof(ScintillaLogViewerBase.FindPreviousAsync));

        Assert.NotNull(findNextAsync);
        Assert.NotNull(findPreviousAsync);
        Assert.Equal(typeof(Task<SearchPosition>), findNextAsync!.ReturnType);
        Assert.Equal(typeof(Task<SearchPosition>), findPreviousAsync!.ReturnType);
    }

    [Fact]
    public void SearchCoordinator_MustProvideAsyncNavigationMethods()
    {
        var methods = typeof(SearchCoordinator)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public);

        Assert.Contains(methods, m => m.Name == "FindNextAsync");
        Assert.Contains(methods, m => m.Name == "FindPreviousAsync");
    }
}

using System.Reflection;
using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Public;

namespace ZLogger.Scintilla.Tests;

public sealed class HostLifecycleTests
{
    [Fact]
    public void ScintillaLogViewer_DoubleDispose_DoesNotThrow()
    {
        var viewer = new ScintillaLogViewer();
        viewer.Dispose();

        var ex = Record.Exception(() => viewer.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void ScintillaLogViewer_RegisterDisposeAction_FiresExactlyOnce()
    {
        var callCount = 0;
        var viewer = new ScintillaLogViewer();
        viewer.RegisterDisposeAction(() => callCount++);

        viewer.Dispose();
        viewer.Dispose();

        Assert.Equal(1, callCount);
    }

    [Fact]
    public void ScintillaLogViewer_TryPost_AfterDispose_ReturnsFalse()
    {
        var viewer = new ScintillaLogViewer();
        viewer.Dispose();

        var entry = MakeEntry("hello", LogLevel.Information);
        var result = viewer.TryPost(entry);

        Assert.False(result);
    }

    [Fact]
    public void ScintillaLogViewer_TryPost_AfterDispose_DoesNotThrow()
    {
        var viewer = new ScintillaLogViewer();
        viewer.Dispose();

        var entry = MakeEntry("hello", LogLevel.Information);
        var ex = Record.Exception(() => viewer.TryPost(entry));
        Assert.Null(ex);
    }

    [Fact]
    public void ScintillaLogViewerBase_IsDisposed_AfterDispose()
    {
        var viewer = new ScintillaLogViewer();
        viewer.Dispose();

        var isDisposed = (bool)typeof(ScintillaLogViewerBase)
            .GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(viewer)!;

        Assert.True(isDisposed);
    }

    [Fact]
    public void ScintillaLogViewerWpf_DoubleDispose_DoesNotThrow()
    {
        Exception? ex = null;
        var thread = new System.Threading.Thread(() =>
        {
            var viewer = new ScintillaLogViewerWpf();
            viewer.Dispose();
            ex = Record.Exception(() => viewer.Dispose());
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(ex);
    }

    [Fact]
    public void ScintillaLogViewerWpf_TryPost_AfterDispose_ReturnsFalse()
    {
        bool result = true;
        var thread = new System.Threading.Thread(() =>
        {
            var viewer = new ScintillaLogViewerWpf();
            viewer.Dispose();
            var entry = MakeEntry("hello", LogLevel.Information);
            result = viewer.TryPost(entry);
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.False(result);
    }

    private static LogEntry MakeEntry(string text, LogLevel level)
        => TestHelper.MakeEntry(text, level);
}

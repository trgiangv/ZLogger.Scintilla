using System.Reflection;
using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Public;

namespace ZLogger.Scintilla.Tests;

[TestClass]
public sealed class HostLifecycleTests
{
    [TestMethod]
    public void ScintillaLogViewer_DoubleDispose_DoesNotThrow()
    {
        var viewer = new ScintillaLogViewer();
        viewer.Dispose();

        var ex = TestHelper.CaptureException(() => viewer.Dispose());
        Assert.IsNull(ex);
    }

    [TestMethod]
    public void ScintillaLogViewer_RegisterDisposeAction_FiresExactlyOnce()
    {
        var callCount = 0;
        var viewer = new ScintillaLogViewer();
        viewer.RegisterDisposeAction(() => callCount++);

        viewer.Dispose();
        viewer.Dispose();

        Assert.AreEqual(1, callCount);
    }

    [TestMethod]
    public void ScintillaLogViewer_TryPost_AfterDispose_ReturnsFalse()
    {
        var viewer = new ScintillaLogViewer();
        viewer.Dispose();

        var entry = TestHelper.MakeEntry("hello", LogLevel.Information);
        var result = viewer.TryPost(entry);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void ScintillaLogViewer_TryPost_AfterDispose_DoesNotThrow()
    {
        var viewer = new ScintillaLogViewer();
        viewer.Dispose();

        var entry = TestHelper.MakeEntry("hello", LogLevel.Information);
        var ex = TestHelper.CaptureException(() => viewer.TryPost(entry));
        Assert.IsNull(ex);
    }

    [TestMethod]
    public void ScintillaLogViewerBase_IsDisposed_AfterDispose()
    {
        var viewer = new ScintillaLogViewer();
        viewer.Dispose();

        var isDisposed = (bool)typeof(ScintillaLogViewerBase)
            .GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(viewer)!;

        Assert.IsTrue(isDisposed);
    }

    [TestMethod]
    public void ScintillaLogViewerWpf_DoubleDispose_DoesNotThrow()
    {
        Exception? ex = null;
        var thread = new Thread(() =>
        {
            var viewer = new ScintillaLogViewerWpf();
            viewer.Dispose();
            ex = TestHelper.CaptureException(() => viewer.Dispose());
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.IsNull(ex);
    }

    [TestMethod]
    public void ScintillaLogViewerWpf_TryPost_AfterDispose_ReturnsFalse()
    {
        bool result = true;
        var thread = new Thread(() =>
        {
            var viewer = new ScintillaLogViewerWpf();
            viewer.Dispose();
            var entry = TestHelper.MakeEntry("hello", LogLevel.Information);
            result = viewer.TryPost(entry);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.IsFalse(result);
    }
}
